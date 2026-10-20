-- Aplicar após schema.sql, com o papel de migração. Reaplicável e transacional.
BEGIN;
SELECT pg_advisory_xact_lock(731902);
CREATE OR REPLACE FUNCTION reject_journal_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'O diário não permite alteração, remoção ou truncamento' USING ERRCODE = '55000';
END;
$$;
DROP TRIGGER IF EXISTS journal_immutable ON movements;
CREATE TRIGGER journal_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON movements
FOR EACH STATEMENT EXECUTE FUNCTION reject_journal_mutation();
COMMIT;
