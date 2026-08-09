using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Halyk.Ingest;

public sealed record PdfExtraction(
    string Text,
    int PageCount,
    IReadOnlyList<int> PageCharCounts,
    string? Error)
{
    /// <summary>
    /// Below this many characters a page is treated as an image and has to reach a
    /// vision-capable model. Checked per page: a scanned annex behind a typed cover page
    /// would pass any whole-document average.
    /// </summary>
    public const int MinCharsPerPage = 60;

    public bool Failed => Error is not null;

    public IReadOnlyList<int> ImagePages => PageCharCounts
        .Select((chars, index) => (chars, page: index + 1))
        .Where(p => p.chars < MinCharsPerPage)
        .Select(p => p.page)
        .ToList();

    public bool NeedsVision => !Failed && (PageCount == 0 || ImagePages.Count > 0);
}

public static class PdfTextExtractor
{
    public static PdfExtraction Extract(string path)
    {
        try
        {
            using var document = PdfDocument.Open(path);
            var builder = new StringBuilder();
            var pageCharCounts = new List<int>();
            var pageErrors = new List<string>();

            foreach (var page in document.GetPages())
            {
                string pageText;
                try
                {
                    pageText = ContentOrderTextExtractor.GetText(page);
                }
                catch (Exception ex)
                {
                    pageErrors.Add($"page {page.Number}: {ex.GetType().Name}");
                    try
                    {
                        pageText = page.Text;
                    }
                    catch (Exception fallbackEx)
                    {
                        pageErrors.Add($"page {page.Number} fallback: {fallbackEx.GetType().Name}");
                        pageText = string.Empty;
                    }
                }

                pageCharCounts.Add(pageText.Trim().Length);
                builder.AppendLine(pageText);
                builder.AppendLine();
            }

            var error = pageErrors.Count > 0 && pageCharCounts.All(c => c == 0)
                ? string.Join("; ", pageErrors)
                : null;

            return new PdfExtraction(builder.ToString().Trim(), pageCharCounts.Count, pageCharCounts, error);
        }
        catch (Exception ex)
        {
            return new PdfExtraction(string.Empty, 0, Array.Empty<int>(), $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
