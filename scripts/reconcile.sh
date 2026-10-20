#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rows="$(psql -X -q -t -A -F ',' -v ON_ERROR_STOP=1 "$@" -f "$root/database/operations/reconcile.sql")"
if [ -n "$rows" ]; then
  echo 'Divergências encontradas: account_id,stored_balance,journal_balance,difference' >&2
  printf '%s\n' "$rows"
  exit 10
fi
echo 'Reconciliação concluída sem divergências.'
