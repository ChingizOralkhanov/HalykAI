namespace Halyk.Core.Ledger;

public sealed record LedgerTransaction(
    string TxnId,
    DateOnly Date,
    string AccountId,
    string Counterparty,
    string Description,
    decimal? Amount,
    string Currency)
{
    /// <summary>
    /// Scenario key encoded in the transaction id: TXN-{scenario}-{seq}.
    /// Parsed once at construction because the rules engine reads it inside nested loops.
    /// </summary>
    public string? ScenarioId { get; } = ParseScenarioId(TxnId);

    /// <summary>
    /// The ledger ships rows with a blank amount. They stay nullable on purpose: zeroing them
    /// would quietly change every aggregate, so the value has to be recovered from a document.
    /// </summary>
    public bool HasAmount => Amount.HasValue;

    public bool IsOutflow => Amount < 0;

    public decimal? AbsAmount => Amount.HasValue ? Math.Abs(Amount.Value) : null;

    public static string? ParseScenarioId(string txnId)
    {
        if (string.IsNullOrWhiteSpace(txnId)) return null;
        var parts = txnId.Split('-');
        return parts.Length >= 3 ? parts[1] : null;
    }
}
