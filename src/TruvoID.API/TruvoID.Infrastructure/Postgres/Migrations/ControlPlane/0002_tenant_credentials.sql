-- Per-Organization database role passwords (build doc §3.1), AES-GCM encrypted by
-- the application with a master key held outside the database. Written only by the
-- provisioner (migrator role); the runtime role can read them to open tenant
-- connections but can never change them.

CREATE TABLE control.organization_db_credential (
    organization_id     uuid PRIMARY KEY REFERENCES control.organization (id),
    password_ciphertext bytea NOT NULL,
    key_id              text NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    rotated_at          timestamptz
);

GRANT SELECT ON control.organization_db_credential TO {{app_role}};
