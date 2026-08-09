using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Halyk.Cli;
using Halyk.Core.Scoring;
using Halyk.Core.Submissions;
using Halyk.Ingest;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var options = CommandLine.Parse(args.Skip(1));

try
{
    return command switch
    {
        "ingest" => Ingest(),
        "ledger" => Ledger(),
        "link" => Link(),
        "solve" => SolveCommand.RunAsync(options).GetAwaiter().GetResult(),
        "init" => Init(),
        "validate" => Validate(),
        "score" => Score(),
        _ => Help(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

int Help()
{
    Console.WriteLine("""
        halyk <command> [options]

          ingest    --dataset <dir> [--out <dir>] [--force] [--documents <dir>]
          ledger    --dataset <dir> [--template <file>]       show the scenario/account map and ledger shape
          init      --template <file> --out <file> --team <s> --email <s> --model <s>
          validate  --submission <file> --template <file>     check keys, statuses and types before upload
          solve     --dataset <dir> --work <dir> --template <file> --out <file> --team <s> --email <s> [--model <s>] [--concurrency <n>]
          score     --submission <file> --key <file>          score against a ground truth file
        """);
    return 0;
}

int Ingest()
{
    var dataset = options.Required("dataset");
    var output = options.Value("out") ?? Path.Combine(Directory.GetCurrentDirectory(), "work");
    var documents = options.Value("documents") ?? Path.Combine(dataset, "documents");
    if (!Directory.Exists(documents)) throw new DirectoryNotFoundException($"no documents folder at {documents}");

    var started = Stopwatch.StartNew();
    var records = DocumentInventory.Build(documents, output, options.Flag("force"));
    started.Stop();

    Console.WriteLine($"documents: {records.Count} in {started.ElapsedMilliseconds} ms");
    Console.WriteLine($"pages:     {records.Sum(r => r.Pages)}");
    Console.WriteLine($"chars:     {records.Sum(r => (long)r.Chars):N0}");
    Console.WriteLine($"text out:  {Path.Combine(output, "text")}");

    var failed = records.Where(r => r.Failed).ToList();
    var vision = records.Where(r => r.NeedsVision).ToList();

    Console.WriteLine($"needs vision: {vision.Count}");
    foreach (var record in vision)
        Console.WriteLine($"  {record.File}  pages={record.Pages}  chars={record.Chars}  image pages: {string.Join(",", record.ImagePages)}");

    Console.WriteLine($"failed: {failed.Count}");
    foreach (var record in failed)
        Console.WriteLine($"  {record.File}  {record.Error}");

    return failed.Count == 0 ? 0 : 1;
}

int Ledger()
{
    var dataset = options.Required("dataset");
    var candidates = Directory.EnumerateFiles(dataset, "*.csv", SearchOption.TopDirectoryOnly).ToList();
    if (candidates.Count != 1)
        throw new FileNotFoundException($"expected exactly one ledger csv in {dataset}, found {candidates.Count}");
    var path = candidates[0];

    var transactions = LedgerReader.Read(path);
    var map = ScenarioMap.Build(transactions);

    Console.WriteLine($"ledger:       {Path.GetFileName(path)}");
    Console.WriteLine($"transactions: {transactions.Count}");
    Console.WriteLine($"scenarios:    {map.ScenariosInLedger.Count} in the ledger (noise accounts included)");

    if (options.Value("template") is { } templatePath)
    {
        var template = SubmissionIo.Load(templatePath);
        var matched = map.Restrict(template.Answers.Keys);
        var missing = map.MissingFromLedger(template.Answers.Keys);
        Console.WriteLine($"template:     {template.Answers.Count} scenarios, {matched.Count} found in the ledger");
        if (missing.Count > 0) Console.WriteLine($"missing:      {string.Join(", ", missing)}");
    }

    Console.WriteLine($"currencies:   {string.Join(", ", transactions.GroupBy(t => t.Currency).Select(g => $"{g.Key}={g.Count()}"))}");

    var missingAmount = transactions.Where(t => !t.HasAmount).ToList();
    Console.WriteLine($"blank amounts: {missingAmount.Count}");
    foreach (var txn in missingAmount)
        Console.WriteLine($"  {txn.TxnId}  {txn.Date:yyyy-MM-dd}  {txn.AccountId}  {txn.Currency}");

    foreach (var problem in map.Anomalies()) Console.WriteLine($"anomaly: {problem}");

    Console.WriteLine();
    Console.WriteLine("scenario  account     txns   outflow        inflow");
    foreach (var group in transactions.Where(t => t.ScenarioId is not null)
                 .GroupBy(t => t.ScenarioId!)
                 .OrderByDescending(g => g.Count())
                 .Take(20))
    {
        var outflow = group.Where(t => t.IsOutflow).Sum(t => t.AbsAmount ?? 0m);
        var inflow = group.Where(t => t.HasAmount && !t.IsOutflow).Sum(t => t.Amount ?? 0m);
        Console.WriteLine($"{group.Key,-9} {string.Join(",", map.AccountsForScenario(group.Key)),-11} {group.Count(),-6} {outflow,14:N2} {inflow,14:N2}");
    }

    return 0;
}

int Link()
{
    var dataset = options.Required("dataset");
    var work = options.Value("work") ?? Path.Combine(Directory.GetCurrentDirectory(), "work");
    var template = SubmissionIo.Load(options.Required("template"));

    var ledgerPath = Directory.EnumerateFiles(dataset, "*.csv", SearchOption.TopDirectoryOnly).Single();
    var map = ScenarioMap.Build(LedgerReader.Read(ledgerPath));
    var records = JsonSerializer.Deserialize<List<DocumentRecord>>(
        File.ReadAllText(Path.Combine(work, "inventory.json")))!;

    var result = DocumentLinker.Link(records, work, map, template.Answers.Keys);

    var orphans = result.Links.Where(l => l.IsOrphan).ToList();
    var ambiguous = result.Links.Where(l => l.IsAmbiguous).ToList();

    Console.WriteLine($"documents linked: {result.Links.Count - orphans.Count} of {result.Links.Count}");
    Console.WriteLine($"unlinked:         {orphans.Count}");
    Console.WriteLine($"multi-borrower:   {ambiguous.Count}");
    Console.WriteLine();
    Console.WriteLine("scenario  docs");
    foreach (var (scenario, files) in result.ByScenario.OrderBy(p => p.Key, StringComparer.Ordinal))
        Console.WriteLine($"{scenario,-9} {files.Count}");

    var outputPath = Path.Combine(work, "links.json");
    File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\nwrote {outputPath}");

    var empty = result.ByScenario.Where(p => p.Value.Count == 0).Select(p => p.Key).ToList();
    if (empty.Count > 0) Console.WriteLine($"NO DOCUMENTS: {string.Join(", ", empty)}");
    return 0;
}

int Init()
{
    var template = options.Required("template");
    var output = options.Required("out");
    var document = SubmissionIo.FromTemplate(
        template,
        options.Required("team"),
        options.Required("email"),
        options.Required("model"));

    // A baseline file with every cell filled is worth having on disk from the first minute:
    // an empty cell and a wrong cell score the same, so a valid floor beats a perfect plan.
    if (options.Flag("fill")) SubmissionIo.FillGaps(document);

    SubmissionIo.Save(document, output);
    Console.WriteLine($"wrote {output}: {document.Answers.Count} scenarios, {document.Cells().Count()} cells");
    return 0;
}

int Validate()
{
    var submissionPath = options.Required("submission");

    // Raw shape first: a wrong value type would otherwise throw on the typed load and never
    // reach the report, which is the opposite of useful at 13:50.
    var problems = JsonShape.CheckActualIsNumeric(submissionPath).ToList();
    if (problems.Count == 0)
    {
        var submission = SubmissionIo.Load(submissionPath);
        var template = SubmissionIo.Load(options.Required("template"));
        problems.AddRange(SubmissionValidator.Validate(submission, template));
    }

    if (problems.Count == 0)
    {
        Console.WriteLine($"ok: {SubmissionIo.Load(submissionPath).Cells().Count()} cells, shape matches the template");
        return 0;
    }

    foreach (var problem in problems) Console.WriteLine($"{problem.Location}: {problem.Message}");
    Console.WriteLine($"{problems.Count} problem(s)");
    return 1;
}

int Score()
{
    var submission = SubmissionIo.Load(options.Required("submission"));
    var key = GroundTruthDocument.Load(options.Required("key"));
    var report = Scorer.Score(submission, key);

    Console.WriteLine("cell        score  status  actual  evid   note");
    foreach (var cell in report.Cells.OrderBy(c => c.ScenarioId, StringComparer.Ordinal).ThenBy(c => c.Clause, StringComparer.Ordinal))
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{cell.Cell,-11} {cell.Total,5:0.00}  {cell.StatusPoints,5:0.00}  {cell.ActualPoints,5:0.00}  {cell.EvidencePoints,5:0.00}  {cell.Note}"));

    Console.WriteLine();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"total {report.Total:0.00} / {report.Max:0} ({report.UnweightedPercent:0.00}% unweighted)   status {report.StatusCorrect}/{report.CellCount}"));
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"lost: status {report.LostOnStatus:0.00}   actual {report.LostOnActual:0.00}   evidence {report.LostOnEvidence:0.00}"));
    return 0;
}


