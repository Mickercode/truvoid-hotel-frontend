-- Platform-admin review of a submitted organization profile. Approval is what unlocks
-- live verifications; until then a workspace can only use free test mode.
ALTER TABLE control.organization_setup
    ADD COLUMN review_note text,
    ADD COLUMN reviewed_at timestamptz,
    ADD COLUMN reviewed_by uuid REFERENCES control.app_user (id),
    ADD COLUMN submitted_at timestamptz;

GRANT UPDATE (review_note, reviewed_at, reviewed_by, submitted_at) ON control.organization_setup TO {{app_role}};
