-- Contas antigas ficam isoladas sob legacy até atribuição explícita pelo administrador.
BEGIN;
SELECT pg_advisory_xact_lock(731902);
ALTER TABLE accounts ADD COLUMN IF NOT EXISTS owner_id varchar(100) NOT NULL DEFAULT 'legacy';
ALTER TABLE accounts ALTER COLUMN owner_id DROP DEFAULT;
GRANT INSERT (id, owner_id) ON accounts TO ledger_runtime;
COMMIT;
