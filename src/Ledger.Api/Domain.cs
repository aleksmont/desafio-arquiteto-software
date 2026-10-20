namespace Ledger.Api;

public sealed record MovementRequest(
    [property: System.Text.Json.Serialization.JsonRequired] string Key,
    [property: System.Text.Json.Serialization.JsonRequired] decimal Amount,
    [property: System.Text.Json.Serialization.JsonRequired] string Type)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key) || Key.Length > 100)
            throw new LedgerException(400, "A chave deve ter entre 1 e 100 caracteres.");
        if (Amount <= 0 || Amount > 9999999999999999.99m || decimal.Round(Amount, 2) != Amount)
            throw new LedgerException(400, "O valor deve ser positivo, com até duas casas decimais e no máximo 9999999999999999.99.");
        if (Type is not ("credit" or "debit"))
            throw new LedgerException(400, "Tipo deve ser credit ou debit.");
    }
}
public sealed record Movement(Guid Id, Guid AccountId, string Key, decimal Amount, string Type, DateTimeOffset RecordedAt);
public sealed record Balance(Guid AccountId, decimal Amount, DateTimeOffset? At, string Currency = "BRL");
public sealed class LedgerException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
