-- RC-10 next step: durable proposals from ALREADY admitted synthetic test-group
-- metadata. No Telegram messages, user IDs, chat hashes, prompts or model text.
-- Classifier/dispatch is a separate, not-yet-authorized upstream stage.
CREATE TABLE IF NOT EXISTS residentchat.operator_proposals (
    tenant_id uuid NOT NULL,
    proposal_id uuid NOT NULL,
    source_update_id bigint NOT NULL CHECK (source_update_id >= 0),
    category smallint NOT NULL CHECK (category BETWEEN 1 AND 6),
    priority smallint NOT NULL CHECK (priority IN (1, 2)),
    state smallint NOT NULL DEFAULT 1 CHECK (state IN (1, 2, 3)),
    ticket_id uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    decided_at timestamptz,
    PRIMARY KEY (tenant_id, proposal_id),
    UNIQUE (tenant_id, source_update_id),
    FOREIGN KEY (tenant_id, source_update_id)
        REFERENCES residentchat.telegram_intake(tenant_id, update_id),
    FOREIGN KEY (tenant_id, ticket_id)
        REFERENCES residentchat.tickets(tenant_id, ticket_id),
    CONSTRAINT proposal_lifecycle CHECK (
        (state = 1 AND ticket_id IS NULL AND decided_at IS NULL)
        OR (state = 2 AND ticket_id IS NOT NULL AND decided_at IS NOT NULL)
        OR (state = 3 AND ticket_id IS NULL AND decided_at IS NOT NULL))
);

CREATE INDEX IF NOT EXISTS ix_residentchat_operator_proposals_recent
    ON residentchat.operator_proposals (tenant_id, created_at DESC, proposal_id DESC);

CREATE TABLE IF NOT EXISTS residentchat.operator_proposal_decisions (
    tenant_id uuid NOT NULL,
    proposal_id uuid NOT NULL,
    decision smallint NOT NULL CHECK (decision IN (2, 3)),
    actor_hash char(64) NOT NULL CHECK (actor_hash ~ '^[0-9A-F]{64}$'),
    decided_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, proposal_id),
    FOREIGN KEY (tenant_id, proposal_id)
        REFERENCES residentchat.operator_proposals(tenant_id, proposal_id)
);

ALTER TABLE residentchat.operator_proposals ENABLE ROW LEVEL SECURITY;
ALTER TABLE residentchat.operator_proposals FORCE ROW LEVEL SECURITY;
ALTER TABLE residentchat.operator_proposal_decisions ENABLE ROW LEVEL SECURITY;
ALTER TABLE residentchat.operator_proposal_decisions FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation ON residentchat.operator_proposals;
CREATE POLICY tenant_isolation ON residentchat.operator_proposals
    USING (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid);
DROP POLICY IF EXISTS tenant_isolation ON residentchat.operator_proposal_decisions;
CREATE POLICY tenant_isolation ON residentchat.operator_proposal_decisions
    USING (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid);

-- Runtime role is NEVER the owner or BYPASSRLS. No DELETE grant.
-- GRANT SELECT, INSERT, UPDATE ON residentchat.operator_proposals TO residentchat_runtime;
-- GRANT SELECT, INSERT ON residentchat.operator_proposal_decisions TO residentchat_runtime;
