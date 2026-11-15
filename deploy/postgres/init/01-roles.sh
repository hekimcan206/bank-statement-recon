#!/bin/sh
# Runs once, when the PostgreSQL data directory is first initialised (docker-entrypoint-initdb.d).
# Least privilege:
#   recon_migrator  owns the "recon" and "recon_admin" schemas and runs migrations (DDL);
#   recon_app       is what the API connects as: DML on "recon" through default privileges, narrowed further by
#                   the migrations (no DELETE anywhere, no UPDATE on imported files and statements).
#   "recon_admin" holds the migration history; the application role cannot see it.
set -eu

: "${MIGRATOR_PASSWORD:?MIGRATOR_PASSWORD must be set}"
: "${APP_PASSWORD:?APP_PASSWORD must be set}"

psql -v ON_ERROR_STOP=1 \
     -v migrator_password="$MIGRATOR_PASSWORD" \
     -v app_password="$APP_PASSWORD" \
     -v db="$POSTGRES_DB" \
     --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
CREATE ROLE recon_migrator LOGIN PASSWORD :'migrator_password';
CREATE ROLE recon_app LOGIN PASSWORD :'app_password';

REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO recon_migrator, recon_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE SCHEMA recon AUTHORIZATION recon_migrator;
CREATE SCHEMA recon_admin AUTHORIZATION recon_migrator;
GRANT USAGE ON SCHEMA recon TO recon_app;

ALTER DEFAULT PRIVILEGES FOR ROLE recon_migrator IN SCHEMA recon
    GRANT SELECT, INSERT, UPDATE ON TABLES TO recon_app;
ALTER DEFAULT PRIVILEGES FOR ROLE recon_migrator IN SCHEMA recon
    GRANT USAGE, SELECT ON SEQUENCES TO recon_app;
SQL
