using System.Text.Json.Serialization;

namespace Halyk.Core.Submissions;

public static class CovenantStatus
{
    public const string Compliant = "COMPLIANT";
    public const string Breach = "BREACH";

    public static bool IsValid(string? value) => value is Compliant or Breach;
}

/// <summary>
/// One graded cell of the submission. Property names must stay exactly as in the template.
/// </summary>
public sealed class CovenantAnswer
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("actual")]
    [JsonConverter(typeof(TwoDecimalConverter))]
    public decimal? Actual { get; set; }

    [JsonPropertyName("evidence_txn_id")]
    public string? EvidenceTxnId { get; set; }

    public static CovenantAnswer Empty() => new();
}
