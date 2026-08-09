using System.Text.Json.Serialization;

namespace Halyk.Core.Submissions;

/// <summary>
/// Mirrors submission_template.json one to one. Keys are never renamed, added or removed.
/// </summary>
public sealed class SubmissionDocument
{
    [JsonPropertyName("team")]
    public string Team { get; set; } = string.Empty;

    [JsonPropertyName("contact_email")]
    public string ContactEmail { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("answers")]
    public Dictionary<string, Dictionary<string, CovenantAnswer>> Answers { get; set; } = new();

    public IEnumerable<(string ScenarioId, string Clause, CovenantAnswer Answer)> Cells()
    {
        foreach (var (scenarioId, covenants) in Answers)
        foreach (var (clause, answer) in covenants)
            yield return (scenarioId, clause, answer);
    }

    public CovenantAnswer? Cell(string scenarioId, string clause) =>
        Answers.TryGetValue(scenarioId, out var covenants) && covenants.TryGetValue(clause, out var answer)
            ? answer
            : null;
}
