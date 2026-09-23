-- The two roles the build doc (§3.2) requires be kept apart:
--   truvo_migrator — owns the schemas and runs DDL; CREATEROLE so Phase 2 can
--                    provision one role per Organization.
--   truvo_app      — runtime API role; DML grants only, never an owner, never
--                    BYPASSRLS, so row-level security always applies to it.
-- On RDS these are created once by the master user with real secrets.

CREATE ROLE truvo_migrator LOGIN PASSWORD 'truvo_migrator_dev' CREATEROLE;
CREATE ROLE truvo_app      LOGIN PASSWORD 'truvo_app_dev' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;

ALTER DATABASE truvoid OWNER TO truvo_migrator;
REVOKE ALL ON DATABASE truvoid FROM PUBLIC;
GRANT CONNECT ON DATABASE truvoid TO truvo_app;
