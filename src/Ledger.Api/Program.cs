using Ledger.Api;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration.GetConnectionString("Ledger") ?? throw new InvalidOperationException("Configure ConnectionStrings__Ledger.");
var apiKey = builder.Configuration["ApiKey"] ?? throw new InvalidOperationException("Configure ApiKey com pelo menos 32 caracteres.");
if (apiKey.Length < 32) throw new InvalidOperationException("ApiKey deve ter pelo menos 32 caracteres.");
var maxRequests = builder.Configuration.GetValue("MaxConcurrentRequests", 32);
if (maxRequests < 1) throw new InvalidOperationException("MaxConcurrentRequests deve ser positivo.");
var connectionBuilder = new NpgsqlConnectionStringBuilder(connection);
// Limites padrão também valem quando o consumidor não os informou na configuração.
if (!connectionBuilder.ContainsKey("Timeout")) connectionBuilder.Timeout = 5;
if (!connectionBuilder.ContainsKey("Command Timeout")) connectionBuilder.CommandTimeout = 10;
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionBuilder.ConnectionString));
builder.Services.AddSingleton<Store>();
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options =>
{
    options.AddConcurrencyLimiter("database", limiter =>
    {
        limiter.PermitLimit = maxRequests;
        limiter.QueueLimit = 0;
    });
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "1";
        await Results.Problem(statusCode: 429, title: "Capacidade simultânea atingida. Tente novamente com a mesma chave.")
            .ExecuteAsync(context.HttpContext);
    };
});
var app = builder.Build();
app.UseExceptionHandler();
app.UseMiddleware<ApiMiddleware>();
app.UseRateLimiter();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (NpgsqlDataSource source, CancellationToken ct) =>
{
    await using var cmd = source.CreateCommand("SELECT id FROM accounts LIMIT 1");
    await cmd.ExecuteScalarAsync(ct);
    return Results.Ok(new { status = "ready" });
});
var accounts = app.MapGroup("/accounts").RequireRateLimiting("database");
accounts.MapPut("/{id:guid}", async (Guid id, Store store, CancellationToken ct) =>
{
    await store.CreateAccount(id, ct);
    return Results.NoContent();
});
accounts.MapPost("/{id:guid}/movements", async (Guid id, MovementRequest request, Store store, CancellationToken ct) =>
{
    var result = await store.Record(id, request, ct);
    return Results.Json(result.Value, statusCode: result.Created ? 201 : 200);
});
accounts.MapGet("/{id:guid}/balance", async (Guid id, string? at, Store store, CancellationToken ct) =>
    Results.Ok(await store.GetBalance(id, HistoryInstant.Parse(at), ct)));
app.Run();

public partial class Program;
