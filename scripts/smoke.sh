#!/usr/bin/env bash
set -euo pipefail
: "${API_KEY:?Defina API_KEY}"
base="${BASE_URL:-http://localhost:8080}"
account="$(python3 -c 'import uuid; print(uuid.uuid4())')"
for attempt in {1..30}; do
  if curl -fsS "$base/health/ready" -H "X-Api-Key: $API_KEY" >/dev/null 2>&1; then break; fi
  if [ "$attempt" = 30 ]; then echo 'API não ficou pronta.' >&2; exit 1; fi
  sleep 1
done
curl -fsS -X PUT "$base/accounts/$account" -H "X-Api-Key: $API_KEY"
body='{"key":"smoke-credit","amount":10.25,"type":"credit"}'
for expected in 201 200; do
  actual="$(curl -sS -o /dev/null -w '%{http_code}' -X POST "$base/accounts/$account/movements" -H "X-Api-Key: $API_KEY" -H 'Content-Type: application/json' -d "$body")"
  [ "$actual" = "$expected" ] || { echo "HTTP esperado $expected, recebido $actual" >&2; exit 1; }
done
curl -fsS "$base/accounts/$account/balance" -H "X-Api-Key: $API_KEY" | python3 -c 'import json,sys; from decimal import Decimal; assert json.load(sys.stdin, parse_float=Decimal)["amount"] == Decimal("10.25")'
echo 'Smoke test passou: criação, crédito, repetição e saldo.'
