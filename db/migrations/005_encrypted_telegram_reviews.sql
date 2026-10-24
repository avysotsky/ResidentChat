-- RC-10: encrypted local operator review for unclassified group messages.
-- Always created only in explicitly authorized fictional test-group mode.
-- No plaintext, sender/user ID, group ID, bot token, attachment or hosted AI.
CREATE TABLE IF NOT EXISTS residentchat.telegram_reviews (
    tenant_id uuid NOT NULL,
    update_id bigint NOT NULL CHECK (update_id >= 0),
    reason smallint NOT NULL CHECK (reason BETWEEN 1 AND 5),
    nonce bytea NOT NULL CHECK (octet_length(nonce) = 12),
    cipher bytea NOT NULL CHECK (octet_length(cipher) <= 16384),
    tag bytea NOT NULL CHECK (octet_length(tag) = 16),
    received_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL DEFAULT (now() + interval '7 days'),
    PRIMARY KEY (tenant_id, update_id),
    FOREIGN KEY (tenant_id, update_id)
        REFERENCES residentchat.telegram_intake(tenant_id, update_id),
    CHECK (expires_at > received_at)
);
CREATE INDEX IF NOT EXISTS ix_residentchat_telegram_reviews_expiration
    ON residentchat.telegram_reviews (expires_at);
ALTER TABLE residentchat.telegram_reviews ENABLE ROW LEVEL SECURITY;
ALTER TABLE residentchat.telegram_reviews FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation ON residentchat.telegram_reviews;
CREATE POLICY tenant_isolation ON residentchat.telegram_reviews
    USING (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid);
-- Runtime role needs only SELECT/INSERT, NEVER DELETE or table ownership.
-- Owner-managed offline purge is required; query-time expiry alone is NOT erasure.
-- GRANT SELECT, INSERT ON residentchat.telegram_reviews TO residentchat_runtime;

-- Each explicit decrypted text read is audited, including the authenticated
-- operator's pseudonymous actor fingerprint. Not a text-content log.
CREATE TABLE IF NOT EXISTS residentchat.telegram_review_reads (
    tenant_id uuid NOT NULL,
    read_id uuid NOT NULL,
    update_id bigint NOT NULL,
    actor_hash char(64) NOT NULL CHECK (actor_hash ~ '^[0-9A-F]{64}$'),
    accessed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, read_id),
    FOREIGN KEY (tenant_id, update_id)
        REFERENCES residentchat.telegram_reviews(tenant_id, update_id)
);
ALTER TABLE residentchat.telegram_review_reads ENABLE ROW LEVEL SECURITY;
ALTER TABLE residentchat.telegram_review_reads FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation ON residentchat.telegram_review_reads;
CREATE POLICY tenant_isolation ON residentchat.telegram_review_reads
    USING (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid);
-- GRANT INSERT ON residentchat.telegram_review_reads TO residentchat_runtime;
