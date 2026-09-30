CREATE TABLE control.organization_branding (
    organization_id uuid PRIMARY KEY REFERENCES control.organization (id),
    workspace_name  text,
    primary_color   text NOT NULL DEFAULT '#72e6c2' CHECK (primary_color ~ '^#[0-9a-fA-F]{6}$'),
    accent_color    text NOT NULL DEFAULT '#e9b65b' CHECK (accent_color ~ '^#[0-9a-fA-F]{6}$'),
    welcome_message text,
    logo_content_type text,
    logo_content    bytea,
    updated_at      timestamptz NOT NULL DEFAULT now()
);

GRANT SELECT, INSERT ON control.organization_branding TO {{app_role}};
GRANT UPDATE (workspace_name, primary_color, accent_color, welcome_message,
              logo_content_type, logo_content, updated_at)
    ON control.organization_branding TO {{app_role}};
