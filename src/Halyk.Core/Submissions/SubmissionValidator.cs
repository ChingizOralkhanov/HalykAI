namespace Halyk.Core.Submissions;

public sealed record ValidationProblem(string Location, string Message);

/// <summary>
/// Last gate before upload: a malformed file makes cells ungradable, and an ungradable cell is a zero.
/// </summary>
public static class SubmissionValidator
{
    public static IReadOnlyList<ValidationProblem> Validate(SubmissionDocument document, SubmissionDocument template)
    {
        var problems = new List<ValidationProblem>();

        if (string.IsNullOrWhiteSpace(document.Team)) problems.Add(new("team", "empty"));
        if (string.IsNullOrWhiteSpace(document.ContactEmail)) problems.Add(new("contact_email", "empty"));
        if (string.IsNullOrWhiteSpace(document.Model)) problems.Add(new("model", "empty"));

        foreach (var scenarioId in template.Answers.Keys)
        {
            if (!document.Answers.ContainsKey(scenarioId))
            {
                problems.Add(new($"answers.{scenarioId}", "scenario missing"));
                continue;
            }

            foreach (var clause in template.Answers[scenarioId].Keys)
            {
                var location = $"answers.{scenarioId}.{clause}";
                var answer = document.Cell(scenarioId, clause);
                if (answer is null)
                {
                    problems.Add(new(location, "cell missing"));
                    continue;
                }

                if (!CovenantStatus.IsValid(answer.Status))
                    problems.Add(new(location, $"status must be COMPLIANT or BREACH, got '{answer.Status ?? "null"}'"));

                if (answer.Actual is null)
                    problems.Add(new(location, "actual is null"));
                else if (answer.Actual <= 0)
                    problems.Add(new(location, $"actual must be positive, got {answer.Actual}"));
            }

            foreach (var clause in document.Answers[scenarioId].Keys)
                if (!template.Answers[scenarioId].ContainsKey(clause))
                    problems.Add(new($"answers.{scenarioId}.{clause}", "clause not in template"));
        }

        foreach (var scenarioId in document.Answers.Keys)
            if (!template.Answers.ContainsKey(scenarioId))
                problems.Add(new($"answers.{scenarioId}", "scenario not in template"));

        return problems;
    }
}
