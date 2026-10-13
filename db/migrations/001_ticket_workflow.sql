-- RC-02: intentionally stores NO resident names, addresses, phones,
-- apartment numbers, full-text conversation bodies or attachments.
-- Apply as a trusted migration role, not the runtime application login.
CREATE SCHEMA IF NOT EXISTS residentchat;

CREATE TABLE IF NOT EXISTS residentchat.tickets (
    tenant_id uuid NOT NULL,
    ticket_id uuid NOT NULL,
    category smallint NOT NULL CHECK (category BETWEEN 1 AND 6),
    priority smallint NOT NULL CHECK (priority IN (1, 2)),
    status smallint NOT NULL CHECK (status IN (1, 2, 3)),
    version integer NOT NULL CHECK (version >= 1),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, ticket_id)
);

CREATE TABLE IF NOT EXISTS residentchat.ticket_audit (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id uuid NOT NULL,
    ticket_id uuid NOT NULL,
    action text NOT NULL CHECK (action IN ('created', 'created_escalated', 'escalated', 'resolved')),
    actor_hash text NOT NULL CHECK (length(actor_hash) = 64),
    at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, ticket_id)
        REFERENCES residentchat.tickets(tenant_id, ticket_id)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_residentchat_tickets_tenant_status
    ON residentchat.tickets (tenant_id, status, updated_at DESC);
CREATE INDEX IF NOT EXISTS ix_residentchat_audit_tenant_ticket
    ON residentchat.ticket_audit (tenant_id, ticket_id, at DESC);

-- Defense in depth against missing tenant filters in future queries.
ALTER TABLE residentchat.tickets ENABLE ROW LEVEL SECURITY;
ALTER TABLE residentchat.tickets FORCE ROW LEVEL SECURITY;
ALTER TABLE residentchat.ticket_audit ENABLE ROW LEVEL SECURITY;
ALTER TABLE residentchat.ticket_audit FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS tenant_isolation ON residentchat.tickets;
CREATE POLICY tenant_isolation ON residentchat.tickets
    USING (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid);

DROP POLICY IF EXISTS tenant_isolation ON residentchat.ticket_audit;
CREATE POLICY tenant_isolation ON residentchat.ticket_audit
    USING (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('residentchat.tenant_id', true), '')::uuid);

-- IMPORTANT: database migration owner/superusers may bypass RLS.
-- Runtime role must be a separate non-owner non-superuser, NO BYPASSRLS.
-- Example grants (create role and credentials through your secret manager):
-- GRANT USAGE ON SCHEMA residentchat TO residentchat_runtime;
-- GRANT SELECT, INSERT, UPDATE ON residentchat.tickets TO residentchat_runtime;
-- GRANT SELECT, INSERT ON residentchat.ticket_audit TO residentchat_runtime;
-- GRANT USAGE, SELECT ON SEQUENCE residentchat.ticket_audit_id_seq
--     TO residentchat_runtime;
