-- RC-10: real group ingestion, privacy-preserving metadata only.
-- No message text, Telegram token, user IDs, phone, attachments or addresses.
-- Run once as the migration owner BEFORE enabling DurableIntake.
CREATE TABLE IF NOT EXISTS residentchat.telegram_intake (
    tenant_id uuid NOT NULL,
    update_id bigint NOT NULL CHECK (update_id >= 0),
    chat_hash char(64) NOT NULL CHECK (chat_hash ~ '^[0-9A-F]{64}$'),
    state smallint NOT NULL DEFAULT 1 CHECK (state IN (1, 2, 3)),
    received_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, update_id)
);

CREATE INDEX IF NOT EXISTS ix_residentchat_intake_tenant_state
    ON residentchat.telegram_intake (tenant_id, state, received_at);

ALTER TABLE residentchat.telegram_intake ENABLE ROW LEVEL SECURITY;
ALTER TABLE residentchat.telegram_intake FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS tenant_isolation ON residentchat.telegram_intake;
CREATE POLICY tenant_isolation ON residentchat.telegram_intake
    USING (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid);

-- Runtime app role must be non-owner, non-superuser and without BYPASSRLS.
-- GRANT SELECT, INSERT ON residentchat.telegram_intake TO residentchat_runtime;
