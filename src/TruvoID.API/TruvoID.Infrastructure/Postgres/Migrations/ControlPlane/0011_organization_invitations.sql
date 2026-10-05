-- Ops-created organizations (build doc §2.4: TruvoID Ops create Institutions and Agencies).
-- An invitation is only a pending record: the organization and its administrator login
-- are created when the invitation is accepted, so a cancelled or expired invitation leaves
-- nothing behind and never reserves the email address.
CREATE TABLE control.organization_invitation (
    id                       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_name        text NOT NULL CHECK (length(trim(organization_name)) BETWEEN 2 AND 120),
    organization_type        text NOT NULL CHECK (organization_type IN ('institution', 'agency')),
    admin_full_name          text NOT NULL,
    admin_email              text NOT NULL,
    token_hash               text NOT NULL UNIQUE,
    expires_at               timestamptz NOT NULL,
    created_by_user_id       uuid NOT NULL REFERENCES control.app_user (id),
    created_at               timestamptz NOT NULL DEFAULT now(),
    last_sent_at             timestamptz NOT NULL DEFAULT now(),
    send_count               integer NOT NULL DEFAULT 1,
    accepted_at              timestamptz,
    accepted_organization_id uuid REFERENCES control.organization (id),
    cancelled_at             timestamptz,
    CONSTRAINT organization_invitation_one_outcome CHECK (accepted_at IS NULL OR cancelled_at IS NULL)
);

-- At most one open invitation per email address.
CREATE UNIQUE INDEX organization_invitation_open_email
    ON control.organization_invitation (lower(admin_email))
    WHERE accepted_at IS NULL AND cancelled_at IS NULL;

GRANT SELECT, INSERT ON control.organization_invitation TO {{app_role}};
GRANT UPDATE (token_hash, expires_at, last_sent_at, send_count, accepted_at, accepted_organization_id, cancelled_at)
    ON control.organization_invitation TO {{app_role}};
