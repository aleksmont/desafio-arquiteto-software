using Ledger.Api;
using Npgsql;
using Xunit;

namespace Ledger.Tests;

public class ValidationTests
{
    [Theory]
    [InlineData(0, "credit")]
    [InlineData(-1, "credit")]
    [InlineData(1.001, "credit")]
    [InlineData(1, "transfer")]
    public void RejectsInvalidAmountsAndTypes(decimal amount, string type) =>
        Assert.Equal(400, Assert.Throws<LedgerException>(() => new MovementRequest("key", amount, type).Validate()).Status);

    [Fact]
    public void RejectsEmptyKey() => Assert.Throws<LedgerException>(() => new MovementRequest("", 1, "credit").Validate());
}

// Banco dedicado de testes: falha explicitamente se não estiver configurado.
public class LedgerTests : IAsyncLifetime
{
    private NpgsqlDataSource source = null!;
    private Store store = null!;
    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_DATABASE")
            ?? throw new InvalidOperationException("Configure TEST_DATABASE apontando para um banco dedicado de testes com schema.sql aplicado.");
        source = NpgsqlDataSource.Create(connection);
        store = new Store(source);
        await Task.CompletedTask;
    }
    public async Task DisposeAsync() => await source.DisposeAsync();
    private async Task<Guid> Account()
    {
        var id = Guid.NewGuid();
        await store.CreateAccount(id);
        return id;
    }
    [Fact]
    public async Task CreditDebitAndHistoricalBalance()
    {
        var id = await Account();
        var credit = await store.Record(id, new("credit", 100.50m, "credit"));
        await store.Record(id, new("debit", 30.25m, "debit"));
        Assert.Equal(70.25m, (await store.GetBalance(id, null)).Amount);
        Assert.Equal(100.50m, (await store.GetBalance(id, credit.Value.RecordedAt)).Amount);
        Assert.Equal(0m, (await store.GetBalance(id, credit.Value.RecordedAt.AddSeconds(-1))).Amount);
    }
    [Fact]
    public async Task ConcurrentDuplicatesOnlyApplyOnce()
    {
        var id = await Account();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.Record(id, new("same", 10, "credit"))));
        Assert.Single(results, r => r.Created);
        Assert.Single(results.Select(r => r.Value.Id).Distinct());
        Assert.Equal(10m, (await store.GetBalance(id, null)).Amount);
    }
    [Fact]
    public async Task DifferentPayloadWithSameKeyConflicts()
    {
        var id = await Account();
        await store.Record(id, new("same", 10, "credit"));
        var error = await Assert.ThrowsAsync<LedgerException>(() => store.Record(id, new("same", 20, "credit")));
        Assert.Equal(409, error.Status);
        Assert.Equal(10m, (await store.GetBalance(id, null)).Amount);
    }
    [Fact]
    public async Task ConcurrentDebitsNeverOverdraw()
    {
        var id = await Account();
        await store.Record(id, new("seed", 100, "credit"));
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            try { await store.Record(id, new($"debit-{i}", 10, "debit")); return true; }
            catch (LedgerException ex) when (ex.Status == 409) { return false; }
        }));
        Assert.Equal(10, results.Count(r => r));
        Assert.Equal(0m, (await store.GetBalance(id, null)).Amount);
    }
    [Fact]
    public async Task InsufficientFundsDoesNotReserveKey()
    {
        var id = await Account();
        await Assert.ThrowsAsync<LedgerException>(() => store.Record(id, new("retry", 10, "debit")));
        await store.Record(id, new("seed", 10, "credit"));
        Assert.True((await store.Record(id, new("retry", 10, "debit"))).Created);
        Assert.Equal(0m, (await store.GetBalance(id, null)).Amount);
    }
    [Fact]
    public async Task MissingAccountIsNotZeroBalance()
    {
        Assert.Equal(404, (await Assert.ThrowsAsync<LedgerException>(() => store.GetBalance(Guid.NewGuid(), null))).Status);
    }
}
