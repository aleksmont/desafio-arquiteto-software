using System.Security.Cryptography;
using System.Text;

namespace Ledger.Api;

// Identidades de serviço: a chave principal representa demo; chaves adicionais têm IDs próprios.
public sealed class ServiceCredentials
{
    private readonly Dictionary<string, byte[]> hashes = new(StringComparer.Ordinal);
    public ServiceCredentials(IConfiguration configuration)
    {
        var entries = new Dictionary<string, string?> { ["demo"] = configuration["ApiKey"] };
        foreach (var child in configuration.GetSection("ServiceKeys").GetChildren())
            if (!entries.TryAdd(child.Key, child.Value)) throw new InvalidOperationException("Identidade de serviço duplicada.");
        foreach (var (id, key) in entries)
        {
            if (id.Length > 100 || string.IsNullOrWhiteSpace(id) || key is null || key.Length < 32)
                throw new InvalidOperationException("Identidades exigem ID de até 100 caracteres e chave com pelo menos 32 caracteres.");
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            if (hashes.Values.Any(existing => CryptographicOperations.FixedTimeEquals(existing, hash)))
                throw new InvalidOperationException("Cada identidade precisa de uma chave distinta.");
            hashes.Add(id, hash);
        }
    }
    public string? Authenticate(string key)
    {
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string? identity = null;
        foreach (var (id, hash) in hashes)
            if (CryptographicOperations.FixedTimeEquals(hash, supplied)) identity = id;
        return identity;
    }
}
