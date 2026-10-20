using System.Globalization;

namespace Ledger.Api;

public static class HistoryInstant
{
    public static DateTimeOffset? Parse(string? value)
    {
        if (value is null) return null;
        string[] formats = ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
        var normalized = value.EndsWith('Z') ? value[..^1] + "+00:00" : value;
        if (!DateTimeOffset.TryParseExact(normalized, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var result))
            throw new LedgerException(400, "Informe at em ISO 8601 com Z ou offset explícito.");
        return result.ToUniversalTime();
    }
}
