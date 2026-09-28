CREATE TABLE control.organization_setup (
    organization_id uuid PRIMARY KEY REFERENCES control.organization (id),
    general_info    jsonb NOT NULL DEFAULT '{}',
    contacts        jsonb NOT NULL DEFAULT '{}',
    business        jsonb NOT NULL DEFAULT '{}',
    ownership       jsonb NOT NULL DEFAULT '{}',
    directors       jsonb NOT NULL DEFAULT '{}',
    services        jsonb NOT NULL DEFAULT '{}',
    integration     jsonb NOT NULL DEFAULT '{}',
    compliance      jsonb NOT NULL DEFAULT '{}',
    legal           jsonb NOT NULL DEFAULT '{}',
    access_level    smallint,
    attested_at     timestamptz,
    status          text NOT NULL DEFAULT 'incomplete'
                    CHECK (status IN ('incomplete', 'submitted', 'approved', 'needs_changes')),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE control.organization_document (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL REFERENCES control.organization (id),
    document_type   text NOT NULL,
    file_name       text NOT NULL,
    content_type    text NOT NULL,
    content         bytea NOT NULL,
    status          text NOT NULL DEFAULT 'uploaded'
                    CHECK (status IN ('uploaded', 'accepted', 'rejected')),
    uploaded_by     uuid REFERENCES control.app_user (id),
    uploaded_at     timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX organization_document_lookup_idx
    ON control.organization_document (organization_id, document_type);

GRANT SELECT, INSERT ON control.organization_setup TO {{app_role}};
GRANT UPDATE (general_info, contacts, business, ownership, directors, services,
              integration, compliance, legal, access_level, attested_at, status, updated_at)
    ON control.organization_setup TO {{app_role}};
GRANT SELECT, INSERT ON control.organization_document TO {{app_role}};
GRANT UPDATE (status) ON control.organization_document TO {{app_role}};
