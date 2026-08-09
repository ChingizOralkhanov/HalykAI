using Halyk.Core.Ledger;

namespace Halyk.Ingest;

/// <summary>
/// Links a borrower's account id, which is what the documents show, to the scenario id,
/// which is what the submission is keyed by. The link lives only in the ledger.
/// </summary>
public sealed class ScenarioMap
{
    private readonly Dictionary<string, string> _accountToScenario;
    private readonly Dictionary<string, HashSet<string>> _scenarioToAccounts;
    private readonly List<string> _conflicts;

    private ScenarioMap(
        Dictionary<string, string> accountToScenario,
        Dictionary<string, HashSet<string>> scenarioToAccounts,
        List<string> conflicts)
    {
        _accountToScenario = accountToScenario;
        _scenarioToAccounts = scenarioToAccounts;
        _conflicts = conflicts;
    }

    /// <summary>
    /// Every scenario key seen in the ledger, noise accounts included. This is not the answer
    /// sheet — use <see cref="Restrict"/> with the template keys for that.
    /// </summary>
    public IReadOnlyCollection<string> ScenariosInLedger => _scenarioToAccounts.Keys;

    public string? ScenarioForAccount(string accountId) =>
        _accountToScenario.TryGetValue(accountId, out var scenario) ? scenario : null;

    public IReadOnlyCollection<string> AccountsForScenario(string scenarioId) =>
        _scenarioToAccounts.TryGetValue(scenarioId, out var accounts) ? accounts : Array.Empty<string>();

    public static ScenarioMap Build(IEnumerable<LedgerTransaction> transactions)
    {
        var accountToScenario = new Dictionary<string, string>(StringComparer.Ordinal);
        var scenarioToAccounts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var conflicts = new List<string>();

        foreach (var txn in transactions)
        {
            var scenarioId = txn.ScenarioId;
            if (scenarioId is null) continue;

            if (accountToScenario.TryGetValue(txn.AccountId, out var known) && known != scenarioId)
                conflicts.Add($"account {txn.AccountId} maps to both {known} and {scenarioId} (at {txn.TxnId})");
            else
                accountToScenario[txn.AccountId] = scenarioId;

            if (!scenarioToAccounts.TryGetValue(scenarioId, out var accounts))
                scenarioToAccounts[scenarioId] = accounts = new HashSet<string>(StringComparer.Ordinal);
            accounts.Add(txn.AccountId);
        }

        return new ScenarioMap(accountToScenario, scenarioToAccounts, conflicts);
    }

    /// <summary>
    /// Narrows the map to the scenarios the submission template asks about. The ledger also
    /// carries hundreds of unrelated accounts, and they must never reach the answer sheet.
    /// </summary>
    public IReadOnlyList<string> Restrict(IEnumerable<string> templateScenarios)
    {
        var wanted = new HashSet<string>(templateScenarios, StringComparer.Ordinal);
        return wanted.Where(_scenarioToAccounts.ContainsKey).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> MissingFromLedger(IEnumerable<string> templateScenarios) =>
        templateScenarios.Where(s => !_scenarioToAccounts.ContainsKey(s)).ToList();

    /// <summary>
    /// The case states one account per scenario in both directions. Anything else means a
    /// document could be filed under the wrong borrower, which is worth failing loudly for.
    /// </summary>
    public IReadOnlyList<string> Anomalies()
    {
        var problems = new List<string>(_conflicts);
        foreach (var (scenarioId, accounts) in _scenarioToAccounts)
            if (accounts.Count != 1)
                problems.Add($"scenario {scenarioId} spans {accounts.Count} accounts: {string.Join(", ", accounts)}");
        return problems;
    }
}
