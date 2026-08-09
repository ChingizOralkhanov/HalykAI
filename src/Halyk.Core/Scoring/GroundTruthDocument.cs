using System.Text.Json;
using System.Text.Json.Serialization;
using Halyk.Core.Submissions;

namespace Halyk.Core.Scoring;

public sealed class GroundTruthDocument
{
    [JsonPropertyName("scenarios")]
    public Dictionary<string, GroundTruthScenario> Scenarios { get; set; } = new();

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    public IEnumerable<(string ScenarioId, string Clause, CovenantAnswer Key)> Cells()
    {
        foreach (var (scenarioId, scenario) in Scenarios)
        foreach (var (clause, key) in scenario.Covenants)
            yield return (scenarioId, clause, key);
    }

    public static GroundTruthDocument Load(string path) =>
        JsonSerializer.Deserialize<GroundTruthDocument>(File.ReadAllText(path), SubmissionIo.Options)
        ?? throw new InvalidDataException($"Could not parse ground truth at {path}");
}

public sealed class GroundTruthScenario
{
    [JsonPropertyName("covenants")]
    public Dictionary<string, CovenantAnswer> Covenants { get; set; } = new();
}
