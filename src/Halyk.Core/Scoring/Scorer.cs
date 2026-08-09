using System.Globalization;
using Halyk.Core.Submissions;

namespace Halyk.Core.Scoring;

public sealed record CellScore(
    string ScenarioId,
    string Clause,
    decimal Total,
    decimal StatusPoints,
    decimal ActualPoints,
    decimal EvidencePoints,
    decimal ActualFactor,
    string Note)
{
    public string Cell => $"{ScenarioId}/{Clause}";
    public bool StatusCorrect => StatusPoints > 0;
}

public sealed record ScoreReport(IReadOnlyList<CellScore> Cells)
{
    public decimal Total => Cells.Sum(c => c.Total);
    public int CellCount => Cells.Count;
    public decimal Max => CellCount;

    /// <summary>
    /// The official ranking weights cells by difficulty and those weights are not published,
    /// so this is a local proxy for progress, never the leaderboard number.
    /// </summary>
    public decimal UnweightedPercent => CellCount == 0 ? 0 : Math.Round(Total / Max * 100m, 2);

    public int StatusCorrect => Cells.Count(c => c.StatusCorrect);
    public decimal LostOnStatus => Cells.Count(c => !c.StatusCorrect);
    public decimal LostOnActual => Cells.Where(c => c.StatusCorrect).Sum(c => 0.30m - c.ActualPoints);
    public decimal LostOnEvidence => Cells.Where(c => c.StatusCorrect).Sum(c => 0.20m - c.EvidencePoints);
}

/// <summary>
/// Replicates the scoring described in the case: status 0.50, actual 0.30 on a linear
/// tolerance ramp, evidence 0.20. Where the key carries a null evidence id those 0.20
/// ride on the same ramp as actual instead of being free.
/// </summary>
public static class Scorer
{
    public const decimal StatusWeight = 0.50m;
    public const decimal ActualWeight = 0.30m;
    public const decimal EvidenceWeight = 0.20m;
    public const decimal Tolerance = 0.05m;

    public static ScoreReport Score(SubmissionDocument submission, GroundTruthDocument groundTruth)
    {
        var cells = groundTruth.Cells()
            .Select(c => ScoreCell(c.ScenarioId, c.Clause, submission.Cell(c.ScenarioId, c.Clause), c.Key))
            .ToList();

        return new ScoreReport(cells);
    }

    public static CellScore ScoreCell(string scenarioId, string clause, CovenantAnswer? answer, CovenantAnswer key)
    {
        if (answer is null)
            return Zero(scenarioId, clause, "cell missing");

        if (!CovenantStatus.IsValid(answer.Status))
            return Zero(scenarioId, clause, $"invalid status '{answer.Status ?? "null"}'");

        if (!string.Equals(answer.Status, key.Status, StringComparison.Ordinal))
            return Zero(scenarioId, clause, $"status {answer.Status}, expected {key.Status}");

        var factor = ActualFactor(answer.Actual, key.Actual);
        var actualPoints = ActualWeight * factor;
        var error = RelativeError(answer.Actual, key.Actual);
        var actualNote = factor == 1m
            ? "exact"
            : error is null
                ? "actual not comparable"
                : string.Create(CultureInfo.InvariantCulture, $"actual off by {error.Value:P2}");

        decimal evidencePoints;
        string note;
        if (key.EvidenceTxnId is null)
        {
            evidencePoints = EvidenceWeight * factor;
            note = actualNote;
        }
        else if (string.Equals(answer.EvidenceTxnId, key.EvidenceTxnId, StringComparison.Ordinal))
        {
            evidencePoints = EvidenceWeight;
            note = actualNote;
        }
        else
        {
            evidencePoints = 0m;
            note = $"evidence {answer.EvidenceTxnId ?? "null"}, expected {key.EvidenceTxnId}";
        }

        var total = StatusWeight + actualPoints + evidencePoints;
        return new(scenarioId, clause, total, StatusWeight, actualPoints, evidencePoints, factor, note);
    }

    /// <summary>
    /// 1.0 on an exact hit, sliding to 0 at a 5% relative miss. The submitted value is rounded
    /// the way it will be serialized, so the local score measures the artefact, not the decimal
    /// that happens to sit in memory.
    /// </summary>
    public static decimal ActualFactor(decimal? actual, decimal? key)
    {
        var error = RelativeError(actual, key);
        if (error is null) return actual is null || key is null ? 0m : actual == key ? 1m : 0m;

        var factor = 1m - error.Value / Tolerance;
        return factor < 0m ? 0m : factor;
    }

    /// <summary>
    /// Null when the comparison is undefined: a missing value, or a key of zero, which the
    /// case forbids and which therefore signals a malformed key rather than a hard answer.
    /// </summary>
    public static decimal? RelativeError(decimal? actual, decimal? key)
    {
        if (actual is null || key is null || key.Value == 0m) return null;
        return Math.Abs(TwoDecimalConverter.Round(actual.Value) - key.Value) / Math.Abs(key.Value);
    }

    private static CellScore Zero(string scenarioId, string clause, string note) =>
        new(scenarioId, clause, 0m, 0m, 0m, 0m, 0m, note);
}
