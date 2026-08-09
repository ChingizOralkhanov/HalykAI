using System.Text.Json;
using Halyk.Core.Ledger;
using Halyk.Core.Submissions;
using Halyk.Ingest;
using Halyk.Llm;
using Halyk.Rules;

namespace Halyk.Cli;

public static class SolveCommand
{
    private static int _supersededDropped;

    public static async Task<int> RunAsync(CommandLine options)
    {
        var dataset = options.Required("dataset");
        var work = options.Value("work") ?? Path.Combine(Directory.GetCurrentDirectory(), "work");
        var templatePath = options.Required("template");
        var outputPath = options.Value("out") ?? Path.Combine(work, "submission.json");
        var model = options.Value("model") ?? "claude-opus-5";
        var concurrency = int.TryParse(options.Value("concurrency"), out var c) ? c : 6;

        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                     ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is not set");

        var template = SubmissionIo.Load(templatePath);
        var ledgerPath = Directory.EnumerateFiles(dataset, "*.csv", SearchOption.TopDirectoryOnly).Single();
        var transactions = LedgerReader.Read(ledgerPath);
        var byScenario = transactions
            .Where(t => t.ScenarioId is not null)
            .GroupBy(t => t.ScenarioId!)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LedgerTransaction>)g.ToList(), StringComparer.Ordinal);

        var links = JsonSerializer.Deserialize<LinkResultDto>(
            await File.ReadAllTextAsync(Path.Combine(work, "links.json")))!;

        // Documents whose pages carry no text layer are attached to the call as files, so the
        // model can read the scanned pages that never made it into the transcript.
        var inventory = JsonSerializer.Deserialize<List<DocumentRecord>>(
            await File.ReadAllTextAsync(Path.Combine(work, "inventory.json")))!;
        var scanned = inventory.Where(r => r.NeedsVision).Select(r => r.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var documentsRoot = Path.Combine(dataset, "documents");

        using var client = new ModelClient(
            apiKey, model,
            Path.Combine(work, "llm"),
            CovenantSolver.PromptVersion,
            concurrency,
            Path.Combine(work, "llm-log.jsonl"));

        var solver = new CovenantSolver(client);
        var submission = SubmissionIo.FromTemplate(
            templatePath,
            options.Required("team"),
            options.Required("email"),
            model);

        var scenarios = template.Answers.Keys.ToList();
        if (int.TryParse(options.Value("limit"), out var limit) && limit > 0 && limit < scenarios.Count)
        {
            scenarios = scenarios.Take(limit).ToList();
            Console.WriteLine($"limited to {limit} scenario(s): {string.Join(", ", scenarios)}");
        }
        var solved = new System.Collections.Concurrent.ConcurrentBag<SolvedScenario>();
        var done = 0;

        await Parallel.ForEachAsync(
            scenarios,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (scenarioId, token) =>
            {
                var clauses = template.Answers[scenarioId].Keys.ToList();
                var documents = LoadDocuments(work, links, scenarioId);
                var rows = byScenario.TryGetValue(scenarioId, out var found) ? found : Array.Empty<LedgerTransaction>();

                var attachments = links.ByScenario.TryGetValue(scenarioId, out var files)
                    ? files.Where(scanned.Contains).Select(f => Path.Combine(documentsRoot, f)).Where(File.Exists).ToList()
                    : new List<string>();

                var result = await solver.SolveAsync(scenarioId, clauses, rows, documents, attachments, token);
                solved.Add(result);

                var index = Interlocked.Increment(ref done);
                var note = result.Error is null ? $"{result.Cells.Count} cells" : result.Error;
                var scan = attachments.Count > 0 ? $", {attachments.Count} scanned file(s) attached" : string.Empty;
                Console.WriteLine($"[{index}/{scenarios.Count}] {scenarioId}: {note}{scan}");
            });

        // Where the stated value and the cited transactions disagree, the model gets one focused
        // second look. Code names the discrepancy; it never silently substitutes its own number,
        // because a carve-out or a reclassification makes the two legitimately differ.
        var disputed = options.Flag("no-reconcile")
            ? new List<(string Scenario, SolvedCell Cell)>()
            : solved
                .SelectMany(s => s.Cells.Select(cell => (Scenario: s.ScenarioId, Cell: cell)))
                .Where(x => x.Cell.RecomputedActual is not null
                            && x.Cell.Actual > 0
                            && Math.Abs(x.Cell.RecomputedActual!.Value - x.Cell.Actual) / x.Cell.Actual > 0.01m)
                .ToList();

        Console.WriteLine($"\nreconciling {disputed.Count} cell(s) where the cited transactions do not add up to the stated value");

        var reconciled = new System.Collections.Concurrent.ConcurrentDictionary<(string, string), SolvedCell>();
        await Parallel.ForEachAsync(
            disputed,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (item, token) =>
            {
                var rows = byScenario.TryGetValue(item.Scenario, out var found) ? found : Array.Empty<LedgerTransaction>();
                var documents = LoadDocuments(work, links, item.Scenario);

                try
                {
                    var result = await solver.ReconcileAsync(item.Scenario, item.Cell, item.Cell.RecomputedActual!.Value, rows, documents, token);
                    if (result is null) return;

                    reconciled[(item.Scenario, item.Cell.Clause)] = result;
                    Console.WriteLine($"  {item.Scenario}/{item.Cell.Clause}: {item.Cell.Actual:0.00} -> {result.Actual:0.00} ({result.Status})");
                }
                catch (Exception ex)
                {
                    // A failed second look keeps the first answer. One bad call must never cost
                    // the whole file.
                    Console.WriteLine($"  {item.Scenario}/{item.Cell.Clause}: reconcile failed, keeping the first answer — {ex.Message}");
                }
            });

        foreach (var scenario in solved)
        foreach (var cell in scenario.Cells)
        {
            var answer = submission.Cell(scenario.ScenarioId, cell.Clause);
            if (answer is null) continue;

            var final = reconciled.TryGetValue((scenario.ScenarioId, cell.Clause), out var fixedCell) ? fixedCell : cell;
            answer.Status = final.Status;
            answer.Actual = final.Actual;
            answer.EvidenceTxnId = final.EvidenceTxnId;
        }

        var filled = SubmissionIo.FillGaps(submission);
        SubmissionIo.Save(submission, outputPath);

        var problems = SubmissionValidator.Validate(submission, template);
        Console.WriteLine();
        Console.WriteLine($"solved {solved.Count(s => s.Error is null)}/{scenarios.Count} scenarios, {filled} cells fell back to a default");
        Console.WriteLine($"superseded revisions dropped before the model saw them: {_supersededDropped}");
        Console.WriteLine($"wrote {outputPath}, {problems.Count} validation problem(s)");
        foreach (var problem in problems.Take(10)) Console.WriteLine($"  {problem.Location}: {problem.Message}");

        await File.WriteAllTextAsync(
            Path.Combine(work, "solved.json"),
            JsonSerializer.Serialize(solved.OrderBy(s => s.ScenarioId, StringComparer.Ordinal), new JsonSerializerOptions { WriteIndented = true }));

        return problems.Count == 0 ? 0 : 1;
    }

    private static IReadOnlyList<(string File, string Text)> LoadDocuments(string work, LinkResultDto links, string scenarioId)
    {
        if (!links.ByScenario.TryGetValue(scenarioId, out var files)) return Array.Empty<(string, string)>();

        var textDirectory = Path.Combine(work, "text");
        var documents = new List<(string, string)>();

        foreach (var file in files)
        {
            var path = Path.Combine(textDirectory, file.Replace('/', '_') + ".txt");
            if (!File.Exists(path)) continue;

            var text = File.ReadAllText(path);
            if (RevisionFilter.IsSuperseded(text))
            {
                Interlocked.Increment(ref _supersededDropped);
                continue;
            }

            documents.Add((file, text));
        }

        return documents;
    }

    private sealed class LinkResultDto
    {
        public Dictionary<string, List<string>> ByScenario { get; set; } = new();
    }
}
