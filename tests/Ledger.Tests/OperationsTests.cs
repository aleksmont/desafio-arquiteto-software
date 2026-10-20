using Ledger.Api;
using Npgsql;
using Xunit;

namespace Ledger.Tests;

public sealed class OperationsTests
{
    private static string Connection => Environment.GetEnvironmentVariable("TEST_DATABASE")
        ?? throw new InvalidOperationException("Configure TEST_DATABASE.");

    [Fact]
    public async Task RuntimeCanOperateButCannotChangeSchemaOrDeleteData()
    {
        await using var admin = NpgsqlDataSource.Create(Connection);
        var role = "test_runtime_" + Guid.NewGuid().ToString("N");
        await using var create = admin.CreateCommand($"CREATE ROLE {role} LOGIN PASSWORD 'test-only' IN ROLE ledger_runtime");
        await create.ExecuteNonQueryAsync();
        try
        {
            var config = new NpgsqlConnectionStringBuilder(Connection) { Username = role, Password = "test-only", Pooling = false };
            await using var runtime = NpgsqlDataSource.Create(config.ConnectionString);
            var store = new Store(runtime);
            var id = Guid.NewGuid();
            await store.CreateAccount(id);
            await store.Record(id, new("runtime-credit", 10, "credit"));
            await store.Record(id, new("runtime-debit", 2, "debit"));
            Assert.Equal(8m, (await store.GetBalance(id, null)).Amount);
            foreach (var sql in new[] {
                "DELETE FROM accounts WHERE false", "DELETE FROM movements WHERE false",
                "UPDATE movements SET amount=amount WHERE false", "TRUNCATE movements",
                "ALTER TABLE movements DISABLE TRIGGER ALL", "CREATE TABLE public.forbidden_table(id int)",
                "UPDATE accounts SET id=id WHERE false" })
            {
                await using var denied = runtime.CreateCommand(sql);
                var error = await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteNonQueryAsync());
                Assert.Equal("42501", error.SqlState);
            }
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP ROLE {role}");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ReconciliationFindsDivergenceWithoutRepairingIt()
    {
        await using var source = NpgsqlDataSource.Create(Connection);
        var store = new Store(source);
        var good = Guid.NewGuid();
        var bad = Guid.NewGuid();
        await store.CreateAccount(good);
        await store.CreateAccount(bad);
        await store.Record(good, new("good", 12, "credit"));
        await using var corrupt = source.CreateCommand("UPDATE accounts SET balance=9 WHERE id=$1");
        corrupt.Parameters.AddWithValue(bad);
        await corrupt.ExecuteNonQueryAsync();
        try
        {
            var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "reconcile.sql"));
            await using var query = source.CreateCommand(sql);
            await using var reader = await query.ExecuteReaderAsync();
            var found = false;
            while (await reader.ReadAsync())
            {
                Assert.NotEqual(good, reader.GetGuid(0));
                if (reader.GetGuid(0) == bad)
                {
                    Assert.Equal(9m, reader.GetDecimal(1));
                    Assert.Equal(0m, reader.GetDecimal(2));
                    found = true;
                }
            }
            Assert.True(found);
            Assert.Equal(9m, (await store.GetBalance(bad, null)).Amount);
        }
        finally
        {
            await using var restore = source.CreateCommand("UPDATE accounts SET balance=0 WHERE id=$1");
            restore.Parameters.AddWithValue(bad);
            await restore.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CancellationWhileWaitingDoesNotConsumeKey()
    {
        var application = "cancel-test-" + Guid.NewGuid().ToString("N");
        var config = new NpgsqlConnectionStringBuilder(Connection) { ApplicationName = application };
        await using var source = NpgsqlDataSource.Create(config.ConnectionString);
        var store = new Store(source);
        var id = Guid.NewGuid();
        await store.CreateAccount(id);
        await using var conn = await source.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var locked = new NpgsqlCommand("SELECT id FROM accounts WHERE id=$1 FOR UPDATE", conn, tx);
        locked.Parameters.AddWithValue(id);
        await locked.ExecuteScalarAsync();
        using var cancellation = new CancellationTokenSource();
        var request = new MovementRequest("cancelled", 7, "credit");
        var pending = store.Record(id, request, cancellation.Token);
        try
        {
            // A sessão da operação deve chegar ao bloqueio antes do cancelamento.
            var waiting = false;
            for (var i = 0; i < 100; i++)
            {
                await using var probe = source.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name=$1 AND wait_event_type='Lock' AND query LIKE 'SELECT balance FROM accounts%')");
                probe.Parameters.AddWithValue(application);
                if ((bool)(await probe.ExecuteScalarAsync())!) { waiting = true; break; }
                await Task.Delay(10);
            }
            Assert.True(waiting);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { cancellation.Cancel(); await tx.RollbackAsync(); }
        Assert.Equal(0m, (await store.GetBalance(id, null)).Amount);
        Assert.True((await store.Record(id, request)).Created);
        Assert.Equal(7m, (await store.GetBalance(id, null)).Amount);
    }
}
