using System.Net;
using System.Net.Http.Json;
using Ledger.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace Ledger.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Key = "integration-test-secret-with-32-characters";
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Ledger", Environment.GetEnvironmentVariable("TEST_DATABASE")
            ?? throw new InvalidOperationException("Configure TEST_DATABASE."));
        builder.UseSetting("ApiKey", Key);
        builder.UseSetting("MaxConcurrentRequests", "1");
    }
    public HttpClient AuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", Key);
        return client;
    }
}

public sealed class HttpTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;
    public HttpTests(ApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task HealthAndAuthorization()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var denied = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("application/problem+json", denied.Content.Headers.ContentType?.MediaType);
        using var authorized = factory.AuthenticatedClient();
        Assert.Equal(HttpStatusCode.OK, (await authorized.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task FullContractAndRetry()
    {
        using var client = factory.AuthenticatedClient();
        var path = $"/accounts/{Guid.NewGuid()}";
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync(path, null)).StatusCode);
        var body = new MovementRequest("http-credit", 25.50m, "credit");
        var created = await client.PostAsJsonAsync(path + "/movements", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var first = await created.Content.ReadFromJsonAsync<Movement>();
        var repeated = await client.PostAsJsonAsync(path + "/movements", body);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(first, await repeated.Content.ReadFromJsonAsync<Movement>());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync(path, null)).StatusCode);
        Assert.Equal(25.50m, (await client.GetFromJsonAsync<Balance>(path + "/balance"))!.Amount);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path + "/movements", body with { Amount = 26 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path + "/movements", body with { Key = "invalid", Amount = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "/balance?at=2026-10-20T12:00:00")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path + "/balance?at=2026-10-20T12:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/accounts/{Guid.NewGuid()}/balance")).StatusCode);
    }

    [Fact]
    public async Task DatabaseUnavailableReturnsRetryableProblem()
    {
        using var unavailable = factory.WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Ledger",
            "Host=127.0.0.1;Port=1;Database=missing;Username=missing;Timeout=1;Pooling=false"));
        using var client = unavailable.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.Key);
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.DoesNotContain("Username", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PermanentSqlErrorDoesNotSuggestRetry()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DATABASE")) { SearchPath = "pg_catalog" };
        using var broken = factory.WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Ledger", connection.ConnectionString));
        using var client = broken.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.Key);
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null(response.Headers.RetryAfter);
        Assert.DoesNotContain("accounts", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LockTimeoutIsRetryableAndDoesNotConsumeKey()
    {
        using var client = factory.AuthenticatedClient();
        var id = Guid.NewGuid();
        var path = $"/accounts/{id}";
        await client.PutAsync(path, null);
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("TEST_DATABASE")!);
        await using var conn = await source.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var locked = new NpgsqlCommand("SELECT id FROM accounts WHERE id=$1 FOR UPDATE", conn, tx);
        locked.Parameters.AddWithValue(id);
        await locked.ExecuteScalarAsync();
        var body = new MovementRequest("timeout", 1, "credit");
        var failed = await client.PostAsJsonAsync(path + "/movements", body);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        await tx.RollbackAsync();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(path + "/movements", body)).StatusCode);
        Assert.Equal(1m, (await client.GetFromJsonAsync<Balance>(path + "/balance"))!.Amount);
    }

    [Fact]
    public async Task SaturationRejectsWithoutBlockingHealth()
    {
        using var client = factory.AuthenticatedClient();
        var id = Guid.NewGuid();
        var path = $"/accounts/{id}";
        await client.PutAsync(path, null);
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("TEST_DATABASE")!);
        await using var conn = await source.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var locked = new NpgsqlCommand("SELECT id FROM accounts WHERE id=$1 FOR UPDATE", conn, tx);
        locked.Parameters.AddWithValue(id);
        await locked.ExecuteScalarAsync();
        var blocked = client.PostAsJsonAsync(path + "/movements", new MovementRequest("blocked", 1, "credit"));
        try
        {
            // Espera o pedido alcançar a espera de lock, sem depender de um atraso arbitrário.
            var waiting = false;
            for (var i = 0; i < 100; i++)
            {
                await using var probe = source.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE wait_event_type='Lock' AND query LIKE 'SELECT balance FROM accounts%')");
                if ((bool)(await probe.ExecuteScalarAsync())!) { waiting = true; break; }
                await Task.Delay(10);
            }
            Assert.True(waiting, "A requisição não alcançou a espera de lock.");
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync(path + "/balance")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        }
        finally { await tx.RollbackAsync(); }
        Assert.Equal(HttpStatusCode.Created, (await blocked).StatusCode);
    }
}

public sealed class HistoryInstantTests
{
    [Theory]
    [InlineData("2026-10-20T12:00:00")]
    [InlineData("")]
    [InlineData("not-a-date")]
    public void RequiresExplicitOffset(string value) => Assert.Throws<LedgerException>(() => HistoryInstant.Parse(value));

    [Fact]
    public void NormalizesOffsets() => Assert.Equal(HistoryInstant.Parse("2026-10-20T12:00:00Z"), HistoryInstant.Parse("2026-10-20T09:00:00-03:00"));
}
