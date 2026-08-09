using System.Text.RegularExpressions;
using Halyk.Core.Ledger;

namespace Halyk.Ingest;

public sealed record DocumentLink(string File, IReadOnlyList<string> Scenarios, IReadOnlyList<string> Accounts)
{
    public bool IsAmbiguous => Scenarios.Count > 1;
    public bool IsOrphan => Scenarios.Count == 0;
}

public sealed record LinkResult(
    IReadOnlyList<DocumentLink> Links,
    IReadOnlyDictionary<string, List<string>> ByScenario);

/// <summary>
/// Files every document under a borrower without asking a model anything: the account ids are
/// already known from the ledger, and a document that concerns a borrower names its account.
/// Cheap, deterministic and exact — worth doing before any classification call.
/// </summary>
public static class DocumentLinker
{
    public static LinkResult Link(
        IEnumerable<DocumentRecord> records,
        string workDirectory,
        ScenarioMap map,
        IEnumerable<string> templateScenarios)
    {
        var accounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scenario in templateScenarios)
        foreach (var account in map.AccountsForScenario(scenario))
            accounts[account] = scenario;

        // Longest first so that ACC-70011 is never matched as ACC-7001.
        var pattern = new Regex(
            string.Join("|", accounts.Keys.OrderByDescending(a => a.Length).Select(Regex.Escape)),
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var links = new List<DocumentLink>();
        var byScenario = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var scenario in templateScenarios) byScenario[scenario] = new List<string>();

        foreach (var record in records)
        {
            if (record.TextFile is null) continue;

            var path = Path.Combine(workDirectory, record.TextFile);
            if (!File.Exists(path)) continue;

            var found = pattern.Matches(File.ReadAllText(path))
                .Select(m => m.Value.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var scenarios = found
                .Select(a => accounts.TryGetValue(a, out var s) ? s : null)
                .Where(s => s is not null)
                .Select(s => s!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            links.Add(new DocumentLink(record.File, scenarios, found));
            foreach (var scenario in scenarios) byScenario[scenario].Add(record.File);
        }

        return new LinkResult(links, byScenario);
    }
}
