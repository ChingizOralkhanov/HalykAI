using System.Text.Encodings.Web;
using System.Text.Json;

namespace Halyk.Core.Submissions;

public static class SubmissionIo
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static SubmissionDocument Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SubmissionDocument>(json, Options)
               ?? throw new InvalidDataException($"Could not parse submission at {path}");
    }

    public static void Save(SubmissionDocument document, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(document, Options));
    }

    /// <summary>
    /// Builds an answer sheet whose shape is taken from the template, so the scenario list
    /// is never hardcoded — the private dataset may ship a different set of borrowers.
    /// </summary>
    public static SubmissionDocument FromTemplate(string templatePath, string team, string contactEmail, string model)
    {
        var template = Load(templatePath);
        var document = new SubmissionDocument { Team = team, ContactEmail = contactEmail, Model = model };

        foreach (var (scenarioId, covenants) in template.Answers)
        {
            var cells = new Dictionary<string, CovenantAnswer>();
            foreach (var clause in covenants.Keys) cells[clause] = CovenantAnswer.Empty();
            document.Answers[scenarioId] = cells;
        }

        return document;
    }

    /// <summary>
    /// An empty cell and a wrong cell score the same, so nothing is ever left null.
    /// A negative value keeps its magnitude: the case defines actual as the modulus, and a
    /// sign slip in the rules engine would otherwise throw away a fully correct number.
    /// </summary>
    public static int FillGaps(SubmissionDocument document, string fallbackStatus = CovenantStatus.Compliant, decimal fallbackActual = 0.01m)
    {
        var touched = 0;
        foreach (var (_, _, answer) in document.Cells())
        {
            var changed = false;

            if (!CovenantStatus.IsValid(answer.Status))
            {
                answer.Status = fallbackStatus;
                changed = true;
            }

            switch (answer.Actual)
            {
                case null or 0m:
                    answer.Actual = fallbackActual;
                    changed = true;
                    break;
                case < 0m:
                    answer.Actual = Math.Abs(answer.Actual.Value);
                    changed = true;
                    break;
            }

            if (changed) touched++;
        }

        return touched;
    }
}
