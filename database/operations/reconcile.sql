-- Somente leitura: uma fotografia consistente para todas as contas.
-- Uso: psql -v ON_ERROR_STOP=1 "$CONNECTION" -f database/operations/reconcile.sql
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '60s';
WITH journal AS (
    SELECT account_id, SUM(CASE WHEN type='credit' THEN amount ELSE -amount END) AS total
    FROM movements GROUP BY account_id
), differences AS (
    SELECT a.id AS account_id, a.balance AS stored_balance, COALESCE(j.total, 0) AS journal_balance
    FROM accounts a LEFT JOIN journal j ON j.account_id = a.id
    WHERE a.balance <> COALESCE(j.total, 0)
)
SELECT account_id, stored_balance, journal_balance, stored_balance - journal_balance AS difference
FROM differences ORDER BY account_id;
COMMIT;
