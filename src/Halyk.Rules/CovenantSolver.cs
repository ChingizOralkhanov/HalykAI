using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Halyk.Core.Ledger;
using Halyk.Core.Submissions;
using Halyk.Llm;

namespace Halyk.Rules;

public sealed record SolvedCell(
    string Clause,
    string Status,
    decimal Actual,
    string? EvidenceTxnId,
    string MetricKind,
    IReadOnlyList<string> ContributingTxnIds,
    decimal? RecomputedActual,
    string Reasoning);

public sealed record SolvedScenario(string ScenarioId, IReadOnlyList<SolvedCell> Cells, string? Error);

/// <summary>
/// The model reads the documents and decides what each covenant means; the arithmetic is then
/// re-derived here from the transactions it cited. Where the two disagree the ledger wins,
/// because a covenant verdict has to be reproducible from the numbers, not from a narration.
/// </summary>
public sealed class CovenantSolver
{
    public const string PromptVersion = "v3";

    private const string SystemPrompt = """
        You audit corporate loan covenants for a bank.

        You are given, for one borrower: the text of every document on file, and every ledger
        transaction on that borrower's account. Decide, for each covenant clause listed in the
        request, whether it is complied with or breached.

        Rules that govern your answer:
        - Outflows are negative in the ledger. `actual` is always a POSITIVE number: the
          magnitude of the metric the clause constrains, in USD, to two decimals. Report it for
          a compliant verdict too, not only for a breach.
        - `actual` is the value of the constrained metric itself, never the threshold and never
          the value of a condition attached to a carve-out. A clause may permit exceeding its
          limit when a proviso is met: report the real metric value, which may sit above the
          limit while the status is still COMPLIANT.
        - Ratios are plain numbers (1.68, not "1.68x"). Amounts are in USD; convert other
          currencies using a rate stated in the documents and report the rate you used.
        - Superseded or draft revisions of a document do not apply. Only the current revision
          for the reporting period governs.
        - `evidence_txn_id` is the SINGLE transaction that decides the outcome: the one whose
          reclassification, inclusion, exclusion or correction is what causes the breach.
          Remove it and the verdict flips. A transaction that merely contributes to a total is
          NOT evidence — not the largest line, not the last one before period end, not the one
          that happened to push a running total past the limit. Where no single transaction
          decides the outcome, such as a ratio test or an aggregate limit, return null.
        - List in `contributing_txn_ids` every transaction you counted towards the metric, so
          the arithmetic can be checked mechanically. Leave it empty only when the metric does
          not come from the transaction list.

        Be exact with numbers. Do not round to thousands.

        Fill the answer fields strictly in schema order. `reasoning` comes first: work the
        clause out there — metric, counted transactions, arithmetic, conclusion. `status` comes
        last and is the conclusion you just reached; if while reasoning you changed your mind,
        the final status reflects the corrected view, not the first guess.

        Never return 0 or a placeholder as `actual`. Every metric here is computable: a ratio's
        numerator and denominator come from the ledger and the financial figures quoted in the
        documents. If a figure seems missing, look again — statements, audit notes and annexes
        quote EBITDA, revenue and debt levels in their text.
        """;

    private readonly ModelClient _client;

    public CovenantSolver(ModelClient client) => _client = client;

    public static JsonNode Schema(IEnumerable<string> clauses) => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["covenants"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    // Field order is deliberate: the verdict comes AFTER the reasoning. With the
                    // verdict first, the model committed to a status, changed its mind while
                    // writing the reasoning, and could not go back — three cells were lost to
                    // exactly that on the calibration set.
                    ["properties"] = new JsonObject
                    {
                        ["clause"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray(clauses.Select(c => (JsonNode)c!).ToArray()),
                        },
                        ["reasoning"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "work through the clause here first: what the metric is, which "
                                + "transactions and document facts count, the arithmetic, then the conclusion",
                        },
                        ["metric_kind"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("sum", "ratio", "count", "other"),
                        },
                        ["contributing_txn_ids"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                        },
                        ["fx_rate_used"] = new JsonObject
                        {
                            ["type"] = new JsonArray("number", "null"),
                            ["description"] = "USD per unit of the foreign currency, when one was applied",
                        },
                        ["threshold"] = new JsonObject { ["type"] = new JsonArray("number", "null") },
                        ["actual"] = new JsonObject
                        {
                            ["type"] = "number",
                            ["description"] = "positive, two decimals; the computed metric value from the reasoning above, never a placeholder",
                        },
                        ["evidence_txn_id"] = new JsonObject
                        {
                            ["type"] = new JsonArray("string", "null"),
                            ["description"] = "the single deciding transaction, or null",
                        },
                        ["status"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("COMPLIANT", "BREACH"),
                            ["description"] = "the conclusion of the reasoning above — they must agree",
                        },
                    },
                    ["required"] = new JsonArray("clause", "reasoning", "metric_kind", "contributing_txn_ids", "actual", "evidence_txn_id", "status"),
                },
            },
        },
        ["required"] = new JsonArray("covenants"),
    };

    public async Task<SolvedScenario> SolveAsync(
        string scenarioId,
        IReadOnlyList<string> clauses,
        IReadOnlyList<LedgerTransaction> transactions,
        IReadOnlyList<(string File, string Text)> documents,
        IReadOnlyList<string>? scannedPdfPaths = null,
        CancellationToken token = default)
    {
        try
        {
            var request = new ModelRequest(
                BuildPrompt(scenarioId, clauses, transactions, documents, scannedPdfPaths),
                SystemPrompt,
                "report_covenants",
                Schema(clauses),
                MaxTokens: 8000,
                PdfPaths: scannedPdfPaths);

            var reply = await _client.CallAsync($"solve:{scenarioId}", request, token);
            var cells = ReadCells(reply.Output, clauses, transactions);
            return new SolvedScenario(scenarioId, cells, null);
        }
        catch (Exception ex)
        {
            return new SolvedScenario(scenarioId, Array.Empty<SolvedCell>(), $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Second look at a cell whose stated value does not match the transactions it cited.
    /// The mismatch is often legitimate — a carve-out excludes a line, or the metric is net of
    /// something — so the model reconciles it. Code states the discrepancy, it does not resolve it.
    /// </summary>
    public async Task<SolvedCell?> ReconcileAsync(
        string scenarioId,
        SolvedCell cell,
        decimal citedSum,
        IReadOnlyList<LedgerTransaction> transactions,
        IReadOnlyList<(string File, string Text)> documents,
        CancellationToken token = default)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($"Borrower scenario: {scenarioId}");
        prompt.AppendLine($"Re-examine clause {cell.Clause} only.");
        prompt.AppendLine();
        prompt.AppendLine("Your earlier answer for this clause:");
        prompt.AppendLine($"  status: {cell.Status}");
        prompt.AppendLine($"  actual: {cell.Actual.ToString("0.00", CultureInfo.InvariantCulture)}");
        prompt.AppendLine($"  evidence_txn_id: {cell.EvidenceTxnId ?? "null"}");
        prompt.AppendLine($"  reasoning: {cell.Reasoning}");
        prompt.AppendLine($"  transactions you counted: {string.Join(", ", cell.ContributingTxnIds)}");
        prompt.AppendLine();
        prompt.AppendLine($"Those transactions add up to {citedSum.ToString("0.00", CultureInfo.InvariantCulture)} USD, "
                          + $"which is not the {cell.Actual.ToString("0.00", CultureInfo.InvariantCulture)} you reported.");
        prompt.AppendLine();
        prompt.AppendLine("""
            Both are plausible. Either the list is incomplete or too broad, or the metric is
            deliberately net of something the list does not capture — an excluded line under a
            carve-out, a reclassification, a currency conversion, a figure taken from a document
            rather than from the ledger.

            Work out which, then answer again for this clause. Make `actual` and
            `contributing_txn_ids` consistent with each other: either correct the number, or
            correct the list, and say in one sentence what the difference was.
            """);
        prompt.AppendLine();
        prompt.AppendLine(BuildPrompt(scenarioId, new[] { cell.Clause }, transactions, documents));

        var request = new ModelRequest(prompt.ToString(), SystemPrompt, "report_covenants", Schema(new[] { cell.Clause }), MaxTokens: 6000);
        var reply = await _client.CallAsync($"reconcile:{scenarioId}/{cell.Clause}", request, token);
        return ReadCells(reply.Output, new[] { cell.Clause }, transactions).FirstOrDefault();
    }

    private static string BuildPrompt(
        string scenarioId,
        IReadOnlyList<string> clauses,
        IReadOnlyList<LedgerTransaction> transactions,
        IReadOnlyList<(string File, string Text)> documents,
        IReadOnlyList<string>? scannedPdfPaths = null)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($"Borrower scenario: {scenarioId}");

        if (scannedPdfPaths is { Count: > 0 })
        {
            prompt.AppendLine();
            prompt.AppendLine("Some pages of the attached files are scans with no extractable text. "
                              + "The attachments above are authoritative for those pages; the text "
                              + "transcript below may show them as blank.");
        }

        prompt.AppendLine($"Clauses to answer: {string.Join(", ", clauses)}");
        prompt.AppendLine();
        prompt.AppendLine("## Ledger transactions for this borrower");
        prompt.AppendLine("txn_id | date | counterparty | description | amount | currency");

        foreach (var txn in transactions)
        {
            var amount = txn.Amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "MISSING";
            prompt.AppendLine($"{txn.TxnId} | {txn.Date:yyyy-MM-dd} | {txn.Counterparty} | {txn.Description} | {amount} | {txn.Currency}");
        }

        prompt.AppendLine();
        prompt.AppendLine("## Documents on file");
        foreach (var (file, text) in documents)
        {
            prompt.AppendLine($"--- document {file} ---");
            prompt.AppendLine(text);
            prompt.AppendLine();
        }

        prompt.AppendLine($"Answer every clause: {string.Join(", ", clauses)}.");
        return prompt.ToString();
    }

    private static IReadOnlyList<SolvedCell> ReadCells(
        JsonNode output,
        IReadOnlyList<string> clauses,
        IReadOnlyList<LedgerTransaction> transactions)
    {
        var byId = transactions.ToDictionary(t => t.TxnId, StringComparer.OrdinalIgnoreCase);
        var cells = new List<SolvedCell>();

        foreach (var item in output["covenants"]?.AsArray() ?? new JsonArray())
        {
            if (item is null) continue;

            var clause = item["clause"]?.GetValue<string>();
            if (clause is null || !clauses.Contains(clause)) continue;

            var status = item["status"]?.GetValue<string>() ?? CovenantStatus.Compliant;
            var actual = Math.Abs(ReadDecimal(item["actual"]) ?? 0m);
            var metricKind = item["metric_kind"]?.GetValue<string>() ?? "other";
            var fxRate = ReadDecimal(item["fx_rate_used"]);

            var contributing = (item["contributing_txn_ids"]?.AsArray() ?? new JsonArray())
                .Select(n => n?.GetValue<string>())
                .Where(id => id is not null)
                .Select(id => id!)
                .ToList();

            // Evidence has to name a transaction that exists on this borrower's account.
            var evidence = item["evidence_txn_id"]?.GetValue<string>();
            if (evidence is not null && !byId.ContainsKey(evidence)) evidence = null;

            decimal? recomputed = null;
            if (metricKind == "sum" && contributing.Count > 0)
            {
                var known = contributing.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                if (known.Count == contributing.Count && known.All(t => t.HasAmount))
                    recomputed = known.Sum(t => Convert(t, fxRate));
            }

            cells.Add(new SolvedCell(
                clause,
                CovenantStatus.IsValid(status) ? status : CovenantStatus.Compliant,
                actual,
                evidence,
                metricKind,
                contributing,
                recomputed,
                item["reasoning"]?.GetValue<string>() ?? string.Empty));
        }

        return cells;
    }

    private static decimal Convert(LedgerTransaction txn, decimal? fxRate)
    {
        var amount = txn.AbsAmount ?? 0m;
        return txn.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase) || fxRate is null
            ? amount
            : amount * fxRate.Value;
    }

    private static decimal? ReadDecimal(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            return node.GetValue<decimal>();
        }
        catch
        {
            return decimal.TryParse(node.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }
    }
}
