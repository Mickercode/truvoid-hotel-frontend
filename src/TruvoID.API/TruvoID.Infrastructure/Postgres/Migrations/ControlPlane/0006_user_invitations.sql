CREATE TABLE control.user_invitation (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    uuid NOT NULL REFERENCES control.organization (id),
    user_id            uuid NOT NULL REFERENCES control.app_user (id),
    token_hash         text NOT NULL UNIQUE,
    expires_at         timestamptz NOT NULL,
    accepted_at        timestamptz,
    created_by_user_id uuid NOT NULL REFERENCES control.app_user (id),
    created_at         timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX user_invitation_lookup_idx
    ON control.user_invitation (organization_id, user_id, expires_at);

GRANT SELECT, INSERT ON control.user_invitation TO {{app_role}};
GRANT UPDATE (accepted_at) ON control.user_invitation TO {{app_role}};
