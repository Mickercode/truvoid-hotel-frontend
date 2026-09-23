-- Per-Organization schema (build doc §3, §6). One copy per Institution/Agency,
-- owned by the migrator role and used at runtime only by {{tenant_role}}, which
-- has no access to any other schema. Outlets are rows in here, separated by RLS.
--
-- Every runtime transaction declares its scope with SET LOCAL (via set_config):
--   app.scope     = 'org'    → Organization-wide (Institution/Agency admins & staff)
--   app.scope     = 'outlet' → one Outlet, identified by app.outlet_id
-- No scope set → every policy below matches nothing (fail closed).

REVOKE ALL ON SCHEMA {{schema}} FROM PUBLIC;
GRANT USAGE ON SCHEMA {{schema}} TO {{tenant_role}};

-- ── Session scope helpers ───────────────────────────────────────────────────

CREATE FUNCTION {{schema}}.session_is_org_scope() RETURNS boolean
LANGUAGE sql STABLE AS $$
    SELECT coalesce(current_setting('app.scope', true), '') = 'org'
$$;

CREATE FUNCTION {{schema}}.session_outlet_id() RETURNS uuid
LANGUAGE sql STABLE AS $$
    SELECT CASE WHEN coalesce(current_setting('app.scope', true), '') = 'outlet'
                THEN nullif(current_setting('app.outlet_id', true), '')::uuid
           END
$$;

CREATE FUNCTION {{schema}}.reject_modification() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '%.% is append-only', TG_TABLE_SCHEMA, TG_TABLE_NAME
        USING ERRCODE = 'insufficient_privilege';
END $$;

-- ── Which Organization this schema belongs to ──────────────────────────────

CREATE TABLE {{schema}}.tenant (
    organization_id uuid PRIMARY KEY,
    org_type        text NOT NULL CHECK (org_type IN ('institution', 'agency'))
);

INSERT INTO {{schema}}.tenant (organization_id, org_type)
VALUES ('{{organization_id}}', '{{org_type}}');

CREATE TRIGGER tenant_immutable
    BEFORE UPDATE OR DELETE OR TRUNCATE ON {{schema}}.tenant
    FOR EACH STATEMENT EXECUTE FUNCTION {{schema}}.reject_modification();

-- ── Wallets ─────────────────────────────────────────────────────────────────
-- The Organization's own wallet, plus one per Agency Outlet. Institution Outlets
-- share the Organization wallet (§2.3) — that's fixed when the Outlet is created.

CREATE TABLE {{schema}}.wallet (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    kind         text NOT NULL CHECK (kind IN ('organization', 'outlet')),
    balance_kobo bigint NOT NULL DEFAULT 0 CHECK (balance_kobo >= 0),
    version      bigint NOT NULL DEFAULT 0,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz
);

CREATE UNIQUE INDEX wallet_one_organization_wallet ON {{schema}}.wallet (kind) WHERE kind = 'organization';

INSERT INTO {{schema}}.wallet (kind) VALUES ('organization');

-- ── Outlets ─────────────────────────────────────────────────────────────────

CREATE TABLE {{schema}}.outlet (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name               text NOT NULL,
    status             text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'suspended', 'closed')),
    wallet_id          uuid NOT NULL REFERENCES {{schema}}.wallet (id),
    created_by_user_id uuid,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz
);

-- Institution Outlets must use the Organization wallet; Agency Outlets need a
-- wallet of their own that no other Outlet uses.
CREATE FUNCTION {{schema}}.check_outlet_wallet() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    tenant_type text;
    wallet_kind text;
BEGIN
    SELECT org_type INTO tenant_type FROM {{schema}}.tenant;
    SELECT kind INTO wallet_kind FROM {{schema}}.wallet WHERE id = NEW.wallet_id;

    IF tenant_type = 'institution' AND wallet_kind IS DISTINCT FROM 'organization' THEN
        RAISE EXCEPTION 'Institution outlets must share the organization wallet'
            USING ERRCODE = 'check_violation';
    END IF;

    IF tenant_type = 'agency' AND (
           wallet_kind IS DISTINCT FROM 'outlet'
           OR EXISTS (SELECT 1 FROM {{schema}}.outlet WHERE wallet_id = NEW.wallet_id AND id <> NEW.id)) THEN
        RAISE EXCEPTION 'Agency outlets need a wallet of their own'
            USING ERRCODE = 'check_violation';
    END IF;

    RETURN NEW;
END $$;

CREATE TRIGGER outlet_wallet_matches_tenant_type
    BEFORE INSERT OR UPDATE OF wallet_id ON {{schema}}.outlet
    FOR EACH ROW EXECUTE FUNCTION {{schema}}.check_outlet_wallet();

-- ── Ledger & verification calls ─────────────────────────────────────────────

CREATE TABLE {{schema}}.wallet_ledger_entry (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    wallet_id          uuid NOT NULL REFERENCES {{schema}}.wallet (id),
    -- Who caused the entry; for Institution Outlets this is descriptive only
    outlet_id          uuid REFERENCES {{schema}}.outlet (id),
    entry_type         text NOT NULL CHECK (entry_type IN ('credit', 'debit', 'refund')),
    amount_kobo        bigint NOT NULL CHECK (amount_kobo > 0),
    balance_after_kobo bigint NOT NULL CHECK (balance_after_kobo >= 0),
    unit_price_kobo    bigint CHECK (unit_price_kobo >= 0),
    reference_id       text,
    created_at         timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX wallet_ledger_entry_wallet_idx ON {{schema}}.wallet_ledger_entry (wallet_id, created_at);
CREATE INDEX wallet_ledger_entry_outlet_idx ON {{schema}}.wallet_ledger_entry (outlet_id, created_at);

CREATE TRIGGER wallet_ledger_entry_append_only
    BEFORE UPDATE OR DELETE OR TRUNCATE ON {{schema}}.wallet_ledger_entry
    FOR EACH STATEMENT EXECUTE FUNCTION {{schema}}.reject_modification();

CREATE TABLE {{schema}}.verification_call (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    outlet_id         uuid REFERENCES {{schema}}.outlet (id),
    user_id           uuid,
    api_key_id        uuid,
    verification_type text NOT NULL,
    -- Hashed/minimal reference to the subject, never the raw NIN/BVN
    subject_ref       text NOT NULL,
    status            text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'succeeded', 'failed')),
    ledger_entry_id   uuid UNIQUE REFERENCES {{schema}}.wallet_ledger_entry (id),
    idempotency_key   text,
    -- Minimized result only (status + matched fields), not the raw provider payload
    result            jsonb,
    created_at        timestamptz NOT NULL DEFAULT now(),
    completed_at      timestamptz,
    CONSTRAINT verification_call_single_caller CHECK ((user_id IS NULL) <> (api_key_id IS NULL))
);

CREATE INDEX verification_call_outlet_idx ON {{schema}}.verification_call (outlet_id, created_at);
CREATE UNIQUE INDEX verification_call_idempotency_key
    ON {{schema}}.verification_call (coalesce(api_key_id, user_id), idempotency_key)
    WHERE idempotency_key IS NOT NULL;

-- ── Outbox → control.audit_log / control.slogani_revenue_ledger ─────────────

CREATE TABLE {{schema}}.outbox (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    event_type  text NOT NULL CHECK (event_type IN ('audit', 'revenue')),
    payload     jsonb NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT now()
);

-- ── Row-level security ──────────────────────────────────────────────────────
-- FORCE so the owning migrator role is subject to it too. Permissive policies OR
-- together: Organization scope sees everything, Outlet scope only its own rows.

ALTER TABLE {{schema}}.outlet              ENABLE ROW LEVEL SECURITY;
ALTER TABLE {{schema}}.outlet              FORCE ROW LEVEL SECURITY;
ALTER TABLE {{schema}}.wallet              ENABLE ROW LEVEL SECURITY;
ALTER TABLE {{schema}}.wallet              FORCE ROW LEVEL SECURITY;
ALTER TABLE {{schema}}.wallet_ledger_entry ENABLE ROW LEVEL SECURITY;
ALTER TABLE {{schema}}.wallet_ledger_entry FORCE ROW LEVEL SECURITY;
ALTER TABLE {{schema}}.verification_call   ENABLE ROW LEVEL SECURITY;
ALTER TABLE {{schema}}.verification_call   FORCE ROW LEVEL SECURITY;
ALTER TABLE {{schema}}.outbox              ENABLE ROW LEVEL SECURITY;
ALTER TABLE {{schema}}.outbox              FORCE ROW LEVEL SECURITY;

CREATE POLICY outlet_org ON {{schema}}.outlet
    USING ({{schema}}.session_is_org_scope()) WITH CHECK ({{schema}}.session_is_org_scope());
CREATE POLICY outlet_self_read ON {{schema}}.outlet FOR SELECT
    USING (id = {{schema}}.session_outlet_id());

CREATE POLICY wallet_org ON {{schema}}.wallet
    USING ({{schema}}.session_is_org_scope()) WITH CHECK ({{schema}}.session_is_org_scope());
-- An Outlet sees and debits the wallet it's linked to: its own (Agency) or the
-- shared Organization wallet (Institution) — without seeing sibling Outlets' rows.
CREATE POLICY wallet_outlet_read ON {{schema}}.wallet FOR SELECT
    USING (id = (SELECT wallet_id FROM {{schema}}.outlet WHERE id = {{schema}}.session_outlet_id()));
CREATE POLICY wallet_outlet_update ON {{schema}}.wallet FOR UPDATE
    USING (id = (SELECT wallet_id FROM {{schema}}.outlet WHERE id = {{schema}}.session_outlet_id()))
    WITH CHECK (id = (SELECT wallet_id FROM {{schema}}.outlet WHERE id = {{schema}}.session_outlet_id()));

CREATE POLICY wallet_ledger_entry_org ON {{schema}}.wallet_ledger_entry
    USING ({{schema}}.session_is_org_scope()) WITH CHECK ({{schema}}.session_is_org_scope());
CREATE POLICY wallet_ledger_entry_outlet_read ON {{schema}}.wallet_ledger_entry FOR SELECT
    USING (outlet_id = {{schema}}.session_outlet_id());
CREATE POLICY wallet_ledger_entry_outlet_insert ON {{schema}}.wallet_ledger_entry FOR INSERT
    WITH CHECK (outlet_id = {{schema}}.session_outlet_id()
                AND wallet_id = (SELECT wallet_id FROM {{schema}}.outlet WHERE id = {{schema}}.session_outlet_id()));

CREATE POLICY verification_call_org ON {{schema}}.verification_call
    USING ({{schema}}.session_is_org_scope()) WITH CHECK ({{schema}}.session_is_org_scope());
CREATE POLICY verification_call_outlet_read ON {{schema}}.verification_call FOR SELECT
    USING (outlet_id = {{schema}}.session_outlet_id());
CREATE POLICY verification_call_outlet_insert ON {{schema}}.verification_call FOR INSERT
    WITH CHECK (outlet_id = {{schema}}.session_outlet_id());
CREATE POLICY verification_call_outlet_update ON {{schema}}.verification_call FOR UPDATE
    USING (outlet_id = {{schema}}.session_outlet_id()) WITH CHECK (outlet_id = {{schema}}.session_outlet_id());

-- Any scoped session may emit events; nobody at runtime reads them back (the
-- relay gets its own role in Phase 3).
CREATE POLICY outbox_insert ON {{schema}}.outbox FOR INSERT
    WITH CHECK ({{schema}}.session_is_org_scope() OR {{schema}}.session_outlet_id() IS NOT NULL);

-- ── Runtime role grants ─────────────────────────────────────────────────────

GRANT SELECT ON {{schema}}.tenant TO {{tenant_role}};
GRANT SELECT, INSERT ON {{schema}}.outlet TO {{tenant_role}};
GRANT UPDATE (name, status, updated_at) ON {{schema}}.outlet TO {{tenant_role}};
GRANT SELECT, INSERT ON {{schema}}.wallet TO {{tenant_role}};
GRANT UPDATE (balance_kobo, version, updated_at) ON {{schema}}.wallet TO {{tenant_role}};
GRANT SELECT, INSERT ON {{schema}}.wallet_ledger_entry TO {{tenant_role}};
GRANT SELECT, INSERT ON {{schema}}.verification_call TO {{tenant_role}};
GRANT UPDATE (status, ledger_entry_id, result, completed_at) ON {{schema}}.verification_call TO {{tenant_role}};
GRANT INSERT ON {{schema}}.outbox TO {{tenant_role}};
