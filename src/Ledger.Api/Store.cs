using System.Data;
using Npgsql;
namespace Ledger.Api;

public sealed class Store(NpgsqlDataSource source)
{
    public async Task CreateAccount(Guid id, CancellationToken ct = default)
    {
        if (id == Guid.Empty) throw new LedgerException(400, "Conta inválida.");
        await using var cmd = source.CreateCommand("INSERT INTO accounts(id) VALUES ($1) ON CONFLICT DO NOTHING");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(Movement Value, bool Created)> Record(Guid account, MovementRequest request, CancellationToken ct = default)
    {
        request.Validate();
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var limits = new NpgsqlCommand("SET LOCAL lock_timeout = '3s'", conn, tx);
        await limits.ExecuteNonQueryAsync(ct);
        // Serializa apenas movimentações da mesma conta; contas diferentes progridem em paralelo.
        await using var locked = new NpgsqlCommand("SELECT balance FROM accounts WHERE id=$1 FOR UPDATE", conn, tx);
        locked.Parameters.AddWithValue(account);
        var current = await locked.ExecuteScalarAsync(ct);
        if (current is null) throw new LedgerException(404, "Conta não encontrada.");
        await using var lookup = new NpgsqlCommand("SELECT id, amount, type, recorded_at FROM movements WHERE account_id=$1 AND idempotency_key=$2", conn, tx);
        lookup.Parameters.AddWithValue(account);
        lookup.Parameters.AddWithValue(request.Key);
        await using (var reader = await lookup.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                var previous = new Movement(reader.GetGuid(0), account, request.Key, reader.GetDecimal(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
                if (previous.Amount != request.Amount || previous.Type != request.Type)
                    throw new LedgerException(409, "Chave já utilizada com outro conteúdo.");
                return (previous, false);
            }
        }
        var next = (decimal)current + (request.Type == "credit" ? request.Amount : -request.Amount);
        if (next < 0) throw new LedgerException(409, "Saldo insuficiente.");
        if (next > 999999999999999999.99m) throw new LedgerException(409, "Limite de saldo excedido.");
        var id = Guid.NewGuid();
        await using var insert = new NpgsqlCommand("INSERT INTO movements(id, account_id, idempotency_key, amount, type, recorded_at) VALUES ($1,$2,$3,$4,$5,clock_timestamp()) RETURNING recorded_at", conn, tx);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(account);
        insert.Parameters.AddWithValue(request.Key);
        insert.Parameters.AddWithValue(request.Amount);
        insert.Parameters.AddWithValue(request.Type);
        var recordedAt = (DateTime)(await insert.ExecuteScalarAsync(ct))!;
        await using var update = new NpgsqlCommand("UPDATE accounts SET balance=$2 WHERE id=$1", conn, tx);
        update.Parameters.AddWithValue(account);
        update.Parameters.AddWithValue(next);
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return (new Movement(id, account, request.Key, request.Amount, request.Type, new DateTimeOffset(recordedAt)), true);
    }

    public async Task<Balance> GetBalance(Guid account, DateTimeOffset? at, CancellationToken ct = default)
    {
        // Uma única instrução oferece uma fotografia consistente sob READ COMMITTED.
        await using var cmd = source.CreateCommand(at is null
            ? "SELECT balance FROM accounts WHERE id=$1"
            : "SELECT COALESCE((SELECT SUM(CASE WHEN type='credit' THEN amount ELSE -amount END) FROM movements WHERE account_id=$1 AND recorded_at <= $2),0) FROM accounts WHERE id=$1");
        cmd.Parameters.AddWithValue(account);
        if (at is not null) cmd.Parameters.AddWithValue(at.Value.ToUniversalTime());
        var value = await cmd.ExecuteScalarAsync(ct);
        if (value is null) throw new LedgerException(404, "Conta não encontrada.");
        return new Balance(account, (decimal)value, at);
    }
}
