-- RC-21: opt-in local LAB Telegram durable cursor and metadata-only blockers.
-- Migration does NOT enable polling, consume Telegram or ACK an update.
-- No token, chat id, content, username, sender, raw service event or IP.
CREATE TABLE IF NOT EXISTS residentchat.telegram_poll_cursors (
    tenant_id uuid NOT NULL,
    consumer_key varchar(64) NOT NULL
        CHECK (consumer_key ~ '^[a-z0-9_]{8,64}$'),
    next_offset bigint NOT NULL CHECK (next_offset >= 0),
    modified_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (consumer_key),
    UNIQUE (tenant_id, consumer_key)
);

CREATE TABLE IF NOT EXISTS residentchat.telegram_poll_blockers (
    tenant_id uuid NOT NULL,
    consumer_key varchar(64) NOT NULL,
    update_id bigint NOT NULL CHECK (update_id >= 0),
    reason smallint NOT NULL CHECK (reason IN (1,2,3)),
    recorded_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, consumer_key, update_id),
    FOREIGN KEY (tenant_id, consumer_key)
        REFERENCES residentchat.telegram_poll_cursors(tenant_id, consumer_key)
);
CREATE INDEX IF NOT EXISTS ix_telegram_poll_blockers_recent
    ON residentchat.telegram_poll_blockers(tenant_id, consumer_key, recorded_at DESC);

ALTER TABLE residentchat.telegram_poll_cursors ENABLE ROW LEVEL SECURITY;
ALTER TABLE residentchat.telegram_poll_cursors FORCE ROW LEVEL SECURITY;
ALTER TABLE residentchat.telegram_poll_blockers ENABLE ROW LEVEL SECURITY;
ALTER TABLE residentchat.telegram_poll_blockers FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation ON residentchat.telegram_poll_cursors;
CREATE POLICY tenant_isolation ON residentchat.telegram_poll_cursors
    USING (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid);
DROP POLICY IF EXISTS tenant_isolation ON residentchat.telegram_poll_blockers;
CREATE POLICY tenant_isolation ON residentchat.telegram_poll_blockers
    USING (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid);

-- For the RLS-protected LAB runtime only:
-- GRANT SELECT, INSERT, UPDATE ON residentchat.telegram_poll_cursors;
-- GRANT SELECT, INSERT ON residentchat.telegram_poll_blockers;
-- NEVER grant DELETE. No production enablement.
