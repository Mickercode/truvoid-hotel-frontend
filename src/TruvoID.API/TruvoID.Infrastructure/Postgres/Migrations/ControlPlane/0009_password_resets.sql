-- Single-use password reset links. Only a SHA-256 hash of the token is stored.
CREATE TABLE control.password_reset (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id    uuid NOT NULL REFERENCES control.app_user (id),
    token_hash text NOT NULL UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    used_at    timestamptz
);

CREATE INDEX password_reset_user_idx ON control.password_reset (user_id) WHERE used_at IS NULL;

GRANT SELECT, INSERT ON control.password_reset TO {{app_role}};
GRANT UPDATE (used_at) ON control.password_reset TO {{app_role}};
