-- Papel de permissões, sem login. Executar com administrador/migrador.
BEGIN;
SELECT pg_advisory_xact_lock(731902);
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ledger_runtime') THEN
        CREATE ROLE ledger_runtime NOLOGIN;
    END IF;
END;
$$;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO ledger_runtime;
GRANT SELECT ON accounts, movements TO ledger_runtime;
GRANT INSERT (id) ON accounts TO ledger_runtime;
GRANT UPDATE (balance) ON accounts TO ledger_runtime;
GRANT INSERT ON movements TO ledger_runtime;
COMMIT;
