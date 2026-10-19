CREATE TABLE IF NOT EXISTS accounts (
 id uuid PRIMARY KEY,
 balance numeric(20,2) NOT NULL DEFAULT 0 CHECK(balance >= 0)
);
CREATE TABLE IF NOT EXISTS movements (
 id uuid PRIMARY KEY,
 account_id uuid NOT NULL REFERENCES accounts(id),
 idempotency_key varchar(100) NOT NULL,
 amount numeric(18,2) NOT NULL CHECK(amount > 0),
 type varchar(6) NOT NULL CHECK(type IN ('credit','debit')),
 recorded_at timestamptz NOT NULL,
 UNIQUE(account_id, idempotency_key)
);
CREATE INDEX IF NOT EXISTS ix_movements_history ON movements(account_id, recorded_at) INCLUDE (amount, type);
-- A aplicação não possui operações de edição ou remoção do diário.
