using System.Text.Json;
using Halyk.Core.Submissions;

namespace Halyk.Cli;

/// <summary>
/// Reads the file as raw JSON rather than through the model, so the check sees exactly what
/// the grader will parse. Typed loading is too forgiving to catch a wrong value type.
/// </summary>
public static class JsonShape
{
    public static IReadOnlyList<ValidationProblem> CheckActualIsNumeric(string path)
    {
        var problems = new List<ValidationProblem>();

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("answers", out var answers)) return problems;

        foreach (var scenario in answers.EnumerateObject())
        foreach (var clause in scenario.Value.EnumerateObject())
        {
            var location = $"answers.{scenario.Name}.{clause.Name}";

            if (clause.Value.TryGetProperty("actual", out var actual) && actual.ValueKind != JsonValueKind.Number)
                problems.Add(new(location, $"actual must be a JSON number, found {actual.ValueKind}"));

            if (clause.Value.TryGetProperty("status", out var status) && status.ValueKind != JsonValueKind.String)
                problems.Add(new(location, $"status must be a JSON string, found {status.ValueKind}"));

            if (clause.Value.TryGetProperty("evidence_txn_id", out var evidence)
                && evidence.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                problems.Add(new(location, $"evidence_txn_id must be a string or null, found {evidence.ValueKind}"));
        }

        return problems;
    }
}
