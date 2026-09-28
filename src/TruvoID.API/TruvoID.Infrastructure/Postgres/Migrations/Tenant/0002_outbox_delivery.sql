-- Revenue/audit outbox delivery state. The runtime tenant role can only insert;
-- the migrator-owned relay marks events after the central ledger write succeeds.
ALTER TABLE {{schema}}.outbox
    ADD COLUMN IF NOT EXISTS delivered_at timestamptz;

CREATE INDEX IF NOT EXISTS outbox_pending_delivery_idx
    ON {{schema}}.outbox (occurred_at)
    WHERE delivered_at IS NULL;

-- The relay runs separately as the DDL/migrator role. Runtime tenant roles can
-- emit events but cannot read or mark them delivered.
CREATE POLICY outbox_relay_read ON {{schema}}.outbox FOR SELECT
    USING (current_user = 'truvo_migrator');
CREATE POLICY outbox_relay_update ON {{schema}}.outbox FOR UPDATE
    USING (current_user = 'truvo_migrator')
    WITH CHECK (current_user = 'truvo_migrator');
