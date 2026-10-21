using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ledger.Api;
using Npgsql;
using Xunit;

namespace Ledger.Tests;

public sealed class CommitFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostConnectionAtCommitIsResolvedBySameKey(bool commitReachedServer)
    {
        var config = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DATABASE")
            ?? throw new InvalidOperationException("Configure TEST_DATABASE."));
        await using var direct = NpgsqlDataSource.Create(config.ConnectionString);
        var store = new Store(direct);
        var id = Guid.NewGuid();
        await store.CreateAccount(id);
        await using var proxy = new CommitProxy(config.Host!, config.Port, commitReachedServer);
        var intercepted = new NpgsqlConnectionStringBuilder(config.ConnectionString)
        {
            Host = "127.0.0.1", Port = proxy.Port, SslMode = SslMode.Disable,
            GssEncryptionMode = GssEncryptionMode.Disable, Pooling = false, CommandTimeout = 5
        };
        await using var source = NpgsqlDataSource.Create(intercepted.ConnectionString);
        var request = new MovementRequest("uncertain-commit", 17.25m, "credit");
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => new Store(source).Record(id, request));
        Assert.True(await proxy.Injected.WaitAsync(TimeSpan.FromSeconds(5)), "O COMMIT não foi interceptado.");
        Assert.Equal(commitReachedServer ? 17.25m : 0m, (await store.GetBalance(id, null)).Amount);
        var retry = await store.Record(id, request);
        Assert.Equal(!commitReachedServer, retry.Created);
        var repeat = await store.Record(id, request);
        Assert.False(repeat.Created);
        Assert.Equal(retry.Value.Id, repeat.Value.Id);
        Assert.Equal(17.25m, (await store.GetBalance(id, null)).Amount);
        Assert.Equal(17.25m, (await store.GetBalance(id, DateTimeOffset.UtcNow)).Amount);
        await using var count = direct.CreateCommand("SELECT COUNT(*) FROM movements WHERE account_id=$1");
        count.Parameters.AddWithValue(id);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }
}

// Proxy exclusivo dos testes. Lê frames do protocolo PostgreSQL sem TLS.
// Nunca habilitado nem referenciado pela aplicação de produção.
internal sealed class CommitProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
    private readonly TaskCompletionSource<bool> injected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task run;
    private int commitSeen;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public Task<bool> Injected => injected.Task;

    public CommitProxy(string host, int port, bool afterConfirmation)
    {
        listener.Start();
        run = Run(host, port, afterConfirmation);
    }

    private async Task Run(string host, int port, bool afterConfirmation)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            using var server = new TcpClient();
            await server.ConnectAsync(host, port, stop.Token);
            var incoming = client.GetStream();
            var outgoing = server.GetStream();
            // StartupMessage não tem byte de tipo; os demais frames têm.
            var header = new byte[4];
            await incoming.ReadExactlyAsync(header, stop.Token);
            var startup = new byte[BinaryPrimitives.ReadInt32BigEndian(header) - 4];
            await incoming.ReadExactlyAsync(startup, stop.Token);
            await outgoing.WriteAsync(header, stop.Token);
            await outgoing.WriteAsync(startup, stop.Token);
            var forward = Pump(incoming, outgoing, true, afterConfirmation);
            var backward = Pump(outgoing, incoming, false, afterConfirmation);
            await Task.WhenAny(forward, backward);
            await stop.CancelAsync();
            client.Close();
            server.Close();
            await Task.WhenAll(forward, backward);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { injected.TrySetResult(false); }
    }

    private async Task Pump(NetworkStream from, NetworkStream to, bool frontend, bool afterConfirmation)
    {
        var header = new byte[5];
        while (!stop.IsCancellationRequested)
        {
            await from.ReadExactlyAsync(header, stop.Token);
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            if (length < 4 || length > 16 * 1024 * 1024) throw new IOException("Frame inválido.");
            var payload = new byte[length - 4];
            await from.ReadExactlyAsync(payload, stop.Token);
            var text = Encoding.UTF8.GetString(payload).TrimEnd('\0');
            if (frontend && header[0] == (byte)'Q' && text.Trim().Equals("COMMIT", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Exchange(ref commitSeen, 1);
                if (!afterConfirmation) { injected.TrySetResult(true); return; }
            }
            if (!frontend && Volatile.Read(ref commitSeen) == 1 && header[0] == (byte)'C' && text == "COMMIT")
            {
                // Banco confirmou; não entregar CommandComplete ao driver.
                injected.TrySetResult(true);
                return;
            }
            await to.WriteAsync(header, stop.Token);
            await to.WriteAsync(payload, stop.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        await run;
        stop.Dispose();
    }
}
