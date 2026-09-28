CREATE TABLE control.agency_invitation (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid NOT NULL REFERENCES control.organization (id),
    user_id            uuid NOT NULL REFERENCES control.app_user (id),
    token_hash         text NOT NULL UNIQUE,
    expires_at         timestamptz NOT NULL,
    accepted_at        timestamptz,
    created_by_user_id uuid NOT NULL REFERENCES control.app_user (id),
    created_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT agency_invitation_acceptance_complete CHECK (
        accepted_at IS NULL OR accepted_at >= created_at
    )
);

CREATE INDEX agency_invitation_lookup_idx
    ON control.agency_invitation (organization_id, user_id, expires_at);

GRANT SELECT, INSERT ON control.agency_invitation TO {{app_role}};
GRANT UPDATE (accepted_at) ON control.agency_invitation TO {{app_role}};
