using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Halyk.Core.Ledger;

namespace Halyk.Ingest;

public static class LedgerReader
{
    /// <summary>
    /// Deliberately narrow: thousands separators and a leading sign are accepted, anything
    /// exotic is not. A blank cell means "recover this from a document" and must stay
    /// distinguishable from a value the parser simply failed to understand.
    /// </summary>
    private const NumberStyles AmountStyles =
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands;

    public static IReadOnlyList<LedgerTransaction> Read(string path)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectColumnCountChanges = true,
            TrimOptions = TrimOptions.Trim,
        };

        using var reader = new StreamReader(path, Encoding.UTF8);
        using var csv = new CsvReader(reader, config);

        var rows = new List<LedgerTransaction>();
        csv.Read();
        csv.ReadHeader();

        while (csv.Read())
        {
            var txnId = csv.GetField("txn_id")!.Trim();
            rows.Add(new LedgerTransaction(
                txnId,
                ParseDate(csv.GetField("date")!.Trim(), txnId),
                csv.GetField("account_id")!.Trim(),
                csv.GetField("counterparty")?.Trim() ?? string.Empty,
                csv.GetField("description")?.Trim() ?? string.Empty,
                ParseAmount(csv.GetField("amount"), txnId),
                csv.GetField("currency")!.Trim()));
        }

        return rows;
    }

    private static decimal? ParseAmount(string? raw, string txnId)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return null;

        if (!decimal.TryParse(value, AmountStyles, CultureInfo.InvariantCulture, out var parsed))
            throw new InvalidDataException($"{txnId}: amount '{value}' is not a number");

        return parsed;
    }

    private static DateOnly ParseDate(string raw, string txnId)
    {
        if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidDataException($"{txnId}: date '{raw}' is not yyyy-MM-dd");

        return date;
    }
}
