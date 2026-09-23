-- Control-plane schema (build doc §3.3, §6): tenant registry, identity, API keys,
-- pricing, and the deliberately-central audit log and Slogani revenue ledger.
-- No verification or wallet data ever lives here — that stays in each
-- Organization's own schema, behind its own database role.
--
-- Runs as the migrator role (table owner). {{app_role}} is the runtime role:
-- DML only, never DDL, and only the columns it actually needs to change.

REVOKE ALL ON SCHEMA control FROM PUBLIC;

-- ── Reference data ──────────────────────────────────────────────────────────

CREATE TABLE control.verification_type (
    code text PRIMARY KEY CHECK (code ~ '^[a-z][a-z0-9_]*$'),
    name text NOT NULL
);

INSERT INTO control.verification_type (code, name) VALUES
    ('nin',   'NIN verification'),
    ('bvn',   'BVN verification'),
    ('phone', 'Phone number verification');

-- ── Tenant registry ─────────────────────────────────────────────────────────

CREATE TABLE control.organization (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name         text NOT NULL,
    type         text NOT NULL CHECK (type IN ('institution', 'agency')),
    -- 'pending' until its schema and role have been provisioned (Phase 2)
    status       text NOT NULL DEFAULT 'pending'
                 CHECK (status IN ('pending', 'active', 'suspended', 'closed')),
    schema_name  text NOT NULL UNIQUE CHECK (schema_name  ~ '^[a-z_][a-z0-9_]{0,62}$'),
    db_role_name text NOT NULL UNIQUE CHECK (db_role_name ~ '^[a-z_][a-z0-9_]{0,62}$'),
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz
);

-- ── Identity (central so login can resolve the Organization before connecting) ──

CREATE TABLE control.app_user (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    email           text NOT NULL,
    full_name       text,
    credential_hash text NOT NULL,
    organization_id uuid REFERENCES control.organization (id),
    -- Outlets live in the parent Organization's schema, so no FK is possible here
    outlet_id       uuid,
    role            text NOT NULL CHECK (role IN (
                        'platform_admin',
                        'institution_admin', 'institution_staff',
                        'agency_admin', 'agency_user',
                        'outlet_owner', 'outlet_staff')),
    status          text NOT NULL DEFAULT 'active' CHECK (status IN ('invited', 'active', 'disabled')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz,
    last_login_at   timestamptz,
    CONSTRAINT app_user_scope_matches_role CHECK (
        CASE
            WHEN role = 'platform_admin'
                THEN organization_id IS NULL AND outlet_id IS NULL
            WHEN role IN ('outlet_owner', 'outlet_staff')
                THEN organization_id IS NOT NULL AND outlet_id IS NOT NULL
            ELSE organization_id IS NOT NULL AND outlet_id IS NULL
        END)
);

-- One account per email across the whole platform — login looks users up by email alone.
CREATE UNIQUE INDEX app_user_email_key ON control.app_user (lower(email));
CREATE INDEX app_user_organization_idx ON control.app_user (organization_id);

-- institution_* roles only on institutions, agency_* roles only on agencies.
CREATE FUNCTION control.check_user_role_matches_org() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    role_family text := split_part(NEW.role, '_', 1);
    org_type    text;
BEGIN
    IF role_family NOT IN ('institution', 'agency') THEN
        RETURN NEW;
    END IF;

    SELECT type INTO org_type FROM control.organization WHERE id = NEW.organization_id;
    IF org_type IS DISTINCT FROM role_family THEN
        RAISE EXCEPTION 'role % cannot belong to a % organization', NEW.role, org_type
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER app_user_role_matches_org
    BEFORE INSERT OR UPDATE OF role, organization_id ON control.app_user
    FOR EACH ROW EXECUTE FUNCTION control.check_user_role_matches_org();

-- ── API keys (central for the same reason as users) ─────────────────────────

CREATE TABLE control.api_key (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid NOT NULL REFERENCES control.organization (id),
    outlet_id          uuid,
    environment        text NOT NULL CHECK (environment IN ('live', 'test')),
    -- Non-secret prefix shown in the UI; the raw key is never stored
    key_prefix         text NOT NULL UNIQUE,
    key_hash           text NOT NULL UNIQUE,
    -- Which of the Organization's own customers this key was handed to
    customer_label     text,
    scopes             text[] NOT NULL DEFAULT '{verify}',
    rate_limit_tier    text NOT NULL DEFAULT 'standard',
    spend_cap_kobo     bigint CHECK (spend_cap_kobo > 0),
    spend_cap_period   text CHECK (spend_cap_period IN ('daily', 'monthly')),
    status             text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'revoked')),
    created_by_user_id uuid REFERENCES control.app_user (id),
    created_at         timestamptz NOT NULL DEFAULT now(),
    last_used_at       timestamptz,
    revoked_at         timestamptz,
    revoked_by_user_id uuid REFERENCES control.app_user (id),
    CONSTRAINT api_key_spend_cap_complete CHECK ((spend_cap_kobo IS NULL) = (spend_cap_period IS NULL)),
    CONSTRAINT api_key_revocation_complete CHECK ((status = 'revoked') = (revoked_at IS NOT NULL))
);

CREATE INDEX api_key_organization_idx ON control.api_key (organization_id, outlet_id);

-- ── Pricing (versioned: new rows take effect, old rows are never rewritten) ──

CREATE TABLE control.platform_rate (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    verification_type  text NOT NULL REFERENCES control.verification_type (code),
    price_kobo         bigint NOT NULL CHECK (price_kobo >= 0),
    -- What Slogani pays upstream per call
    cost_kobo          bigint NOT NULL CHECK (cost_kobo >= 0),
    effective_from     timestamptz NOT NULL,
    created_by_user_id uuid REFERENCES control.app_user (id),
    created_at         timestamptz NOT NULL DEFAULT now(),
    UNIQUE (verification_type, effective_from)
);

-- Per-Institution / per-Agency price, overriding platform_rate
CREATE TABLE control.organization_rate (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid NOT NULL REFERENCES control.organization (id),
    verification_type  text NOT NULL REFERENCES control.verification_type (code),
    price_kobo         bigint NOT NULL CHECK (price_kobo >= 0),
    effective_from     timestamptz NOT NULL,
    created_by_user_id uuid REFERENCES control.app_user (id),
    created_at         timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, verification_type, effective_from)
);

-- ── Append-only ledgers (filled by the outbox relay from tenant schemas) ─────

CREATE TABLE control.audit_log (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at     timestamptz NOT NULL,
    recorded_at     timestamptz NOT NULL DEFAULT now(),
    actor_type      text NOT NULL CHECK (actor_type IN ('user', 'api_key', 'system')),
    actor_id        uuid,
    action          text NOT NULL,
    entity          text NOT NULL,
    entity_id       text,
    organization_id uuid REFERENCES control.organization (id),
    outlet_id       uuid,
    api_key_id      uuid REFERENCES control.api_key (id),
    metadata        jsonb NOT NULL DEFAULT '{}',
    -- Outbox event id: makes relay retries idempotent
    source_event_id uuid UNIQUE
);

CREATE INDEX audit_log_organization_idx ON control.audit_log (organization_id, occurred_at);

CREATE TABLE control.slogani_revenue_ledger (
    id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at       timestamptz NOT NULL,
    recorded_at       timestamptz NOT NULL DEFAULT now(),
    -- The buying Organization (for outlet_resale: the Agency that sold to its Outlet)
    organization_id   uuid NOT NULL REFERENCES control.organization (id),
    outlet_id         uuid,
    -- credit_sale/refund are Slogani income; outlet_resale is visibility only and
    -- must be excluded when summing revenue
    entry_type        text NOT NULL CHECK (entry_type IN ('credit_sale', 'refund', 'outlet_resale')),
    amount_kobo       bigint NOT NULL CHECK (amount_kobo <> 0),
    verification_type text REFERENCES control.verification_type (code),
    credit_batch_ref  text,
    source_event_id   uuid UNIQUE
);

CREATE INDEX slogani_revenue_ledger_organization_idx ON control.slogani_revenue_ledger (organization_id, occurred_at);

-- Enforced by trigger, not just grants, so even the owning migrator role can't rewrite
-- history. Statement-level, so any UPDATE/DELETE/TRUNCATE fails even if it matches no rows.
CREATE FUNCTION control.reject_modification() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '%.% is append-only', TG_TABLE_SCHEMA, TG_TABLE_NAME
        USING ERRCODE = 'insufficient_privilege';
END $$;

CREATE TRIGGER audit_log_append_only
    BEFORE UPDATE OR DELETE OR TRUNCATE ON control.audit_log
    FOR EACH STATEMENT EXECUTE FUNCTION control.reject_modification();

CREATE TRIGGER slogani_revenue_ledger_append_only
    BEFORE UPDATE OR DELETE OR TRUNCATE ON control.slogani_revenue_ledger
    FOR EACH STATEMENT EXECUTE FUNCTION control.reject_modification();

-- ── Runtime role grants ─────────────────────────────────────────────────────
-- No DELETE anywhere: Organizations, users and keys change status, never disappear.

GRANT USAGE ON SCHEMA control TO {{app_role}};

GRANT SELECT ON control.verification_type TO {{app_role}};

GRANT SELECT, INSERT ON control.organization TO {{app_role}};
GRANT UPDATE (name, status, updated_at) ON control.organization TO {{app_role}};

GRANT SELECT, INSERT ON control.app_user TO {{app_role}};
GRANT UPDATE (full_name, credential_hash, role, status, updated_at, last_login_at)
    ON control.app_user TO {{app_role}};

GRANT SELECT, INSERT ON control.api_key TO {{app_role}};
GRANT UPDATE (customer_label, rate_limit_tier, spend_cap_kobo, spend_cap_period,
              status, last_used_at, revoked_at, revoked_by_user_id)
    ON control.api_key TO {{app_role}};

GRANT SELECT, INSERT ON control.platform_rate, control.organization_rate TO {{app_role}};
GRANT SELECT, INSERT ON control.audit_log, control.slogani_revenue_ledger TO {{app_role}};
