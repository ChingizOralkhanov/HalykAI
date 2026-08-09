using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Halyk.Ingest;

public sealed class DocumentRecord
{
    [JsonPropertyName("file")] public string File { get; set; } = string.Empty;
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("pages")] public int Pages { get; set; }
    [JsonPropertyName("chars")] public int Chars { get; set; }
    [JsonPropertyName("needs_vision")] public bool NeedsVision { get; set; }
    [JsonPropertyName("image_pages")] public IReadOnlyList<int> ImagePages { get; set; } = Array.Empty<int>();
    [JsonPropertyName("text_file")] public string? TextFile { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }

    [JsonIgnore] public bool Failed => Error is not null;
}

/// <summary>
/// Turns the opaque documents folder into a cached text corpus plus a manifest.
/// The cache lives in one sidecar per document, so a run killed halfway keeps everything
/// it already extracted: inventory.json is a derived rollup, never the source of truth.
/// </summary>
public static class DocumentInventory
{
    private static readonly string[] TextLikeExtensions = { ".txt", ".csv", ".md", ".json" };
    private static readonly string[] IgnoredExtensions = { ".db", ".ini" };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static IReadOnlyList<DocumentRecord> Build(
        string documentsDirectory,
        string outputDirectory,
        bool force = false,
        int? maxDegreeOfParallelism = null)
    {
        var textDirectory = Path.Combine(outputDirectory, "text");
        var metaDirectory = Path.Combine(outputDirectory, "meta");
        Directory.CreateDirectory(textDirectory);
        Directory.CreateDirectory(metaDirectory);

        var files = Directory
            .EnumerateFiles(documentsDirectory, "*", SearchOption.AllDirectories)
            .Where(p => !IgnoredExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        // One document up front, on this thread: removes any lazy-static race inside the
        // PDF library before the parallel loop opens the throttle.
        var warmUp = files.FirstOrDefault(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        var results = new ConcurrentBag<DocumentRecord>();
        if (warmUp is not null)
            results.Add(Process(warmUp, documentsDirectory, textDirectory, metaDirectory, force));

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism ?? Environment.ProcessorCount,
        };

        Parallel.ForEach(files.Where(p => p != warmUp), options, path =>
            results.Add(Process(path, documentsDirectory, textDirectory, metaDirectory, force)));

        var records = results.OrderBy(r => r.File, StringComparer.Ordinal).ToList();
        WriteAtomic(Path.Combine(outputDirectory, "inventory.json"), JsonSerializer.Serialize(records, JsonOptions));
        return records;
    }

    private static DocumentRecord Process(string path, string documentsDirectory, string textDirectory, string metaDirectory, bool force)
    {
        var relative = Path.GetRelativePath(documentsDirectory, path).Replace('\\', '/');
        var slug = relative.Replace('/', '_');
        var textFile = Path.Combine(textDirectory, slug + ".txt");
        var metaFile = Path.Combine(metaDirectory, slug + ".json");
        var hash = HashFile(path);

        if (!force && TryReadCached(metaFile, hash, textFile) is { } cached) return cached;

        var record = new DocumentRecord
        {
            File = relative,
            Sha256 = hash,
            Bytes = new FileInfo(path).Length,
        };

        var extension = Path.GetExtension(path).ToLowerInvariant();
        string? text = null;

        if (extension == ".pdf")
        {
            var extraction = PdfTextExtractor.Extract(path);
            record.Pages = extraction.PageCount;
            record.Chars = extraction.Text.Length;
            record.NeedsVision = extraction.NeedsVision;
            record.ImagePages = extraction.ImagePages;
            record.Error = extraction.Error;
            if (!extraction.Failed) text = extraction.Text;
        }
        else if (TextLikeExtensions.Contains(extension))
        {
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
                record.Pages = 1;
                record.Chars = text.Length;
            }
            catch (Exception ex)
            {
                record.Error = $"{ex.GetType().Name}: {ex.Message}";
            }
        }
        else
        {
            record.Error = $"unsupported extension {extension}";
        }

        // A failure is never cached: a file lock or an out-of-memory hit would otherwise be
        // baked in until someone thinks to pass --force.
        if (text is not null && !record.Failed)
        {
            WriteAtomic(textFile, text);
            record.TextFile = Path.GetRelativePath(Path.GetDirectoryName(textDirectory)!, textFile).Replace('\\', '/');
            WriteAtomic(metaFile, JsonSerializer.Serialize(record, JsonOptions));
        }

        return record;
    }

    private static DocumentRecord? TryReadCached(string metaFile, string hash, string textFile)
    {
        if (!File.Exists(metaFile) || !File.Exists(textFile)) return null;

        try
        {
            var cached = JsonSerializer.Deserialize<DocumentRecord>(File.ReadAllText(metaFile));
            return cached is not null && cached.Sha256 == hash && !cached.Failed ? cached : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, content, Utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
