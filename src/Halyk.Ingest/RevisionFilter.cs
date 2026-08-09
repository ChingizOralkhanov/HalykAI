using System.Text.RegularExpressions;

namespace Halyk.Ingest;

/// <summary>
/// The pack ships superseded revisions alongside the current ones, and they carry different
/// thresholds. They announce themselves in their own first line, so they are dropped before
/// anything reaches the model rather than left for it to adjudicate.
/// </summary>
public static class RevisionFilter
{
    private static readonly Regex Superseded = new(
        @"НЕДЕЙСТВУЮЩАЯ\s+РЕДАКЦИЯ|ЗАМЕНЕНА\s+И\s+ИЗЛОЖЕНА\s+В\s+НОВОЙ\s+РЕДАКЦИИ|SUPERSEDED\s+REVISION|NO\s+LONGER\s+IN\s+FORCE",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsSuperseded(string text) => Superseded.IsMatch(text);
}
