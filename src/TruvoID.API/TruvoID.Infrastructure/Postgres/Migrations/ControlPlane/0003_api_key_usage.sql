ALTER TABLE control.api_key
    ADD COLUMN call_count bigint NOT NULL DEFAULT 0;

GRANT UPDATE (call_count, last_used_at) ON control.api_key TO {{app_role}};
