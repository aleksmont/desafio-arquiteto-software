using Ledger.Api;
using Npgsql;
using Xunit;

namespace Ledger.Tests;

public sealed class PersistenceTests
{
    private static NpgsqlDataSource Source() => NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("TEST_DATABASE")
        ?? throw new InvalidOperationException("Configure TEST_DATABASE."));

    [Theory]
    [InlineData("UPDATE movements SET amount=amount WHERE false")]
    [InlineData("DELETE FROM movements WHERE false")]
    [InlineData("TRUNCATE movements")]
    public async Task JournalRejectsMutation(string sql)
    {
        await using var source = Source();
        await using var cmd = source.CreateCommand(sql);
        var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("55000", error.SqlState);
    }

    [Fact]
    public async Task FailureAfterInsertRollsBackJournalAndBalance()
    {
        await using var source = Source();
        var store = new Store(source);
        var id = Guid.NewGuid();
        await store.CreateAccount(id);
        // Constraint restrita a esta conta injeta falha no UPDATE, depois do INSERT no diário.
        var name = "test_failure_" + id.ToString("N");
        await using var add = source.CreateCommand($"ALTER TABLE accounts ADD CONSTRAINT {name} CHECK (id <> '{id}'::uuid OR balance = 0) NOT VALID");
        await add.ExecuteNonQueryAsync();
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => store.Record(id, new("rollback", 10, "credit")));
            Assert.Equal(0m, (await store.GetBalance(id, null)).Amount);
            Assert.Equal(0m, (await store.GetBalance(id, DateTimeOffset.UtcNow)).Amount);
        }
        finally
        {
            await using var drop = source.CreateCommand($"ALTER TABLE accounts DROP CONSTRAINT {name}");
            await drop.ExecuteNonQueryAsync();
        }
        Assert.True((await store.Record(id, new("rollback", 10, "credit"))).Created);
    }
}
