-- Per-account lockout for online password guessing. The "auth" rate limit is per
-- client IP, so an attacker rotating IPs (or behind carrier NAT) is not throttled
-- per account; this counter closes that gap.
ALTER TABLE control.app_user
    ADD COLUMN failed_login_attempts integer NOT NULL DEFAULT 0,
    ADD COLUMN locked_until timestamptz;

GRANT UPDATE (failed_login_attempts, locked_until) ON control.app_user TO {{app_role}};
