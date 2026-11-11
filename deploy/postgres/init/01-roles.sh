#!/bin/sh
# Runs once, when the PostgreSQL data directory is first initialised (docker-entrypoint-initdb.d).
# Least privilege:
#   sync_migrator  owns the "sync" and "sync_admin" schemas and runs migrations (DDL);
#   sync_app       is what the server connects as. It gets DML on "sync" through default privileges, which the
#                  migrations then narrow further (the change log: SELECT and INSERT only, it is append-only).
#   "sync_admin" holds the migration history; the application role cannot see it.
set -eu

: "${MIGRATOR_PASSWORD:?MIGRATOR_PASSWORD must be set}"
: "${APP_PASSWORD:?APP_PASSWORD must be set}"

psql -v ON_ERROR_STOP=1 \
     -v migrator_password="$MIGRATOR_PASSWORD" \
     -v app_password="$APP_PASSWORD" \
     -v db="$POSTGRES_DB" \
     --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
CREATE ROLE sync_migrator LOGIN PASSWORD :'migrator_password';
CREATE ROLE sync_app LOGIN PASSWORD :'app_password';

REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO sync_migrator, sync_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE SCHEMA sync AUTHORIZATION sync_migrator;
CREATE SCHEMA sync_admin AUTHORIZATION sync_migrator;
GRANT USAGE ON SCHEMA sync TO sync_app;

ALTER DEFAULT PRIVILEGES FOR ROLE sync_migrator IN SCHEMA sync
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO sync_app;
SQL
