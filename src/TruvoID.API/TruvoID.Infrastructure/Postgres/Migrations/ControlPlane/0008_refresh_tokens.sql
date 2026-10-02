-- Server-side refresh tokens. Only a SHA-256 hash is stored. Every refresh rotates
-- the token; all tokens descended from one sign-in share a family_id, so presenting
-- an already-rotated token (a sign of theft) revokes the whole family.
CREATE TABLE control.refresh_token (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     uuid NOT NULL REFERENCES control.app_user (id),
    family_id   uuid NOT NULL,
    token_hash  text NOT NULL UNIQUE,
    created_at  timestamptz NOT NULL DEFAULT now(),
    expires_at  timestamptz NOT NULL,
    revoked_at  timestamptz,
    replaced_by uuid REFERENCES control.refresh_token (id)
);

CREATE INDEX refresh_token_user_idx ON control.refresh_token (user_id) WHERE revoked_at IS NULL;
CREATE INDEX refresh_token_family_idx ON control.refresh_token (family_id);

GRANT SELECT, INSERT ON control.refresh_token TO {{app_role}};
GRANT UPDATE (revoked_at, replaced_by) ON control.refresh_token TO {{app_role}};
