namespace Halyk.Cli;

public sealed class CommandLine
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static CommandLine Parse(IEnumerable<string> args)
    {
        var parsed = new CommandLine();
        string? pending = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null) parsed._flags.Add(pending);
                pending = arg[2..];
            }
            else if (pending is not null)
            {
                parsed._values[pending] = arg;
                pending = null;
            }
        }

        if (pending is not null) parsed._flags.Add(pending);
        return parsed;
    }

    public string? Value(string name) => _values.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Accepts both `--force` and `--force true`, because the second form silently produced a
    /// fully cached run that looked like it had forced.
    /// </summary>
    public bool Flag(string name) =>
        _flags.Contains(name) || Value(name) is "true" or "1" or "yes";

    public string Required(string name) =>
        Value(name) ?? throw new ArgumentException($"missing --{name}");
}
