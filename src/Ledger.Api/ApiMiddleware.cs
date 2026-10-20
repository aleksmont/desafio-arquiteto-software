using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Ledger.Api;

public sealed class ApiMiddleware(RequestDelegate next, ILogger<ApiMiddleware> logger, IConfiguration configuration)
{
    private readonly byte[] keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuration["ApiKey"]!));

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path == "/health/live") { await next(context); return; }
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers["X-Api-Key"].ToString()));
        if (!CryptographicOperations.FixedTimeEquals(supplied, keyHash))
        {
            await Problem(context, 401, "Credencial inválida.");
            return;
        }
        try { await next(context); }
        catch (BadHttpRequestException ex)
        {
            await Problem(context, ex.StatusCode, ex.StatusCode == 413 ? "Corpo da requisição excede o limite permitido." : "Corpo ou parâmetros da requisição inválidos.");
        }
        catch (LedgerException ex) { await Problem(context, ex.Status, ex.Message); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // O consumidor desistiu; não tente escrever outra resposta em uma conexão encerrada.
            context.Response.StatusCode = 499;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            var transient = ex is TimeoutException || ex is NpgsqlException { IsTransient: true }
                || ex is PostgresException { SqlState: "55P03" or "57014" };
            // Não registrar mensagem SQL, parâmetros, credenciais ou corpo da requisição.
            logger.LogWarning("Persistência falhou. Tipo={ErrorType}, Transitoria={Transient}, Trace={TraceId}",
                ex.GetType().Name, transient, context.TraceIdentifier);
            if (transient) context.Response.Headers.RetryAfter = "1";
            await Problem(context, transient ? 503 : 500,
                transient ? "Persistência temporariamente indisponível. Repita com a mesma chave." : "Falha interna de persistência.");
        }
    }

    private static Task Problem(HttpContext context, int status, string title) =>
        Results.Problem(statusCode: status, title: title,
            extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
}
