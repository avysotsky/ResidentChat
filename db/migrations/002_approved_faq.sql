-- RC-03b: tenant-isolated, READ-ONLY lookup for manually approved
-- building-specific FAQ materials. Migration contains NO real documents,
-- messages, personal names, apartment identifiers or example resident data.
-- Apply only as a trusted migration owner, not the runtime application role.
CREATE SCHEMA IF NOT EXISTS residentchat;

CREATE TABLE IF NOT EXISTS residentchat.approved_faq (
    tenant_id uuid NOT NULL,
    faq_id uuid NOT NULL,
    question text NOT NULL CHECK (char_length(question) BETWEEN 1 AND 240),
    answer text NOT NULL CHECK (char_length(answer) BETWEEN 1 AND 2048),
    source_ref text NOT NULL CHECK (char_length(source_ref) BETWEEN 1 AND 256),
    approved_at timestamptz NOT NULL,
    search_vector tsvector GENERATED ALWAYS AS (
        to_tsvector('simple'::regconfig, question || ' ' || answer)
    ) STORED,
    PRIMARY KEY (tenant_id, faq_id)
);

CREATE INDEX IF NOT EXISTS ix_residentchat_approved_faq_fts
    ON residentchat.approved_faq USING gin (search_vector);

-- For PostgreSQL pooling safety, an authorized tenant is set inside
-- a transaction with set_config(..., true) by the application.
ALTER TABLE residentchat.approved_faq ENABLE ROW LEVEL SECURITY;
ALTER TABLE residentchat.approved_faq FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS approved_faq_tenant_select ON residentchat.approved_faq;
CREATE POLICY approved_faq_tenant_select ON residentchat.approved_faq
    FOR SELECT
    USING (
        tenant_id =
        nullif(current_setting('residentchat.tenant_id', true), '')::uuid
        AND approved_at IS NOT NULL
    );

-- Runtime credentials must be for a separate non-owner, non-superuser role
-- with NO BYPASSRLS, and must receive only:
-- GRANT USAGE ON SCHEMA residentchat TO residentchat_runtime;
-- GRANT SELECT ON residentchat.approved_faq TO residentchat_runtime;
-- Never grant FAQ INSERT/UPDATE/DELETE or ownership to the runtime role.
-- Content curation and approval occur in a future explicitly authorized
-- human-only admin workflow, with audit, retention and source review.
-- 'simple' FTS is deliberately multilingual but lacks morphological stemming.
-- This migration alone is NOT authorization to import any real materials.
