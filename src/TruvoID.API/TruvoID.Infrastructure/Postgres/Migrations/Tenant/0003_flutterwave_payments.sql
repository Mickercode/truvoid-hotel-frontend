CREATE TABLE {{schema}}.wallet_payment (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tx_ref text NOT NULL UNIQUE,
    provider_transaction_id text,
    amount_kobo bigint NOT NULL CHECK (amount_kobo > 0),
    currency text NOT NULL DEFAULT 'NGN',
    status text NOT NULL CHECK (status IN ('pending', 'succeeded', 'failed')),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz,
    paid_at timestamptz
);

ALTER TABLE {{schema}}.wallet_payment ENABLE ROW LEVEL SECURITY;
ALTER TABLE {{schema}}.wallet_payment FORCE ROW LEVEL SECURITY;
CREATE POLICY wallet_payment_org ON {{schema}}.wallet_payment
    USING ({{schema}}.session_is_org_scope()) WITH CHECK ({{schema}}.session_is_org_scope());
GRANT SELECT, INSERT, UPDATE ON {{schema}}.wallet_payment TO {{tenant_role}};
