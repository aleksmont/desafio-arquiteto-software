#!/usr/bin/env bash
set -euo pipefail
: "${LEDGER_APP_PASSWORD:?Configure LEDGER_APP_PASSWORD}"
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  --set=app_password="$LEDGER_APP_PASSWORD" <<'SQL'
SELECT format('CREATE ROLE ledger_app LOGIN PASSWORD %L IN ROLE ledger_runtime', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ledger_app')
\gexec
ALTER ROLE ledger_app PASSWORD :'app_password';
GRANT ledger_runtime TO ledger_app;
SQL
