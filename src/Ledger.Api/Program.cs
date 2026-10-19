using System.Security.Cryptography;
using System.Text;
using Ledger.Api;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration.GetConnectionString("Ledger") ?? throw new InvalidOperationException("Configure ConnectionStrings__Ledger.");
var apiKey = builder.Configuration["ApiKey"] ?? throw new InvalidOperationException("Configure ApiKey com pelo menos 32 caracteres.");
if (apiKey.Length < 32) throw new InvalidOperationException("ApiKey deve ter pelo menos 32 caracteres.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connection));
builder.Services.AddSingleton<Store>();
builder.Services.AddProblemDetails();
var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/health/live") { await next(context); return; }
    var supplied = context.Request.Headers["X-Api-Key"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))))
    { context.Response.StatusCode = 401; return; }
    try { await next(context); }
    catch (LedgerException ex) { await Results.Problem(statusCode: ex.Status, title: ex.Message).ExecuteAsync(context); }
    catch (NpgsqlException ex)
    {
        app.Logger.LogWarning("Falha de persistência: {ErrorType}", ex.GetType().Name);
        await Results.Problem(statusCode: 503, title: "Persistência indisponível. Repita com a mesma chave.").ExecuteAsync(context);
    }
});
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (NpgsqlDataSource source, CancellationToken ct) =>
{
    await using var cmd = source.CreateCommand("SELECT COUNT(*) FROM accounts");
    await cmd.ExecuteScalarAsync(ct);
    return Results.Ok(new { status = "ready" });
});
app.MapPut("/accounts/{id:guid}", async (Guid id, Store store, CancellationToken ct) =>
{
    await store.CreateAccount(id, ct);
    return Results.NoContent();
});
app.MapPost("/accounts/{id:guid}/movements", async (Guid id, MovementRequest request, Store store, CancellationToken ct) =>
{
    var result = await store.Record(id, request, ct);
    return Results.Json(result.Value, statusCode: result.Created ? 201 : 200);
});
app.MapGet("/accounts/{id:guid}/balance", async (Guid id, DateTimeOffset? at, Store store, CancellationToken ct) =>
    Results.Ok(await store.GetBalance(id, at, ct)));
app.Run();
