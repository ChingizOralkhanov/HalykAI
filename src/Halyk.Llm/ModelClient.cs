using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Halyk.Llm;

public sealed record ModelRequest(
    string Prompt,
    string System,
    string ToolName,
    JsonNode ToolSchema,
    int MaxTokens = 8000,
    IReadOnlyList<string>? PdfPaths = null);

public sealed record ModelReply(JsonNode Output, bool FromCache, int InputTokens, int OutputTokens);

/// <summary>
/// Structured-output client for the Messages API. Answers are cached on disk by the hash of
/// everything that can change them, so a killed run resumes for free and the resume path is
/// the same code path as a normal run.
/// </summary>
public sealed class ModelClient : IDisposable
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string _cacheDirectory;
    private readonly string _model;
    private readonly string _promptVersion;
    private readonly string? _journalPath;
    private readonly SemaphoreSlim _gate;
    private long _pauseUntilTicks;

    public ModelClient(string apiKey, string model, string cacheDirectory, string promptVersion, int concurrency, string? journalPath = null)
    {
        _model = model;
        _cacheDirectory = cacheDirectory;
        _promptVersion = promptVersion;
        _journalPath = journalPath;
        _gate = new SemaphoreSlim(concurrency, concurrency);
        Directory.CreateDirectory(cacheDirectory);

        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ModelReply> CallAsync(string label, ModelRequest request, CancellationToken token = default)
    {
        var key = CacheKey(request);
        var cachePath = Path.Combine(_cacheDirectory, key + ".json");

        if (File.Exists(cachePath))
        {
            try
            {
                var cached = JsonNode.Parse(await File.ReadAllTextAsync(cachePath, token));
                if (cached?["output"] is { } output) return new ModelReply(output, true, 0, 0);
            }
            catch (JsonException)
            {
                // A half-written cache entry is worth one more call, not a crash.
            }
        }

        await _gate.WaitAsync(token);
        try
        {
            var reply = await SendWithRetriesAsync(label, request, token);
            var envelope = new JsonObject
            {
                ["label"] = label,
                ["model"] = _model,
                ["prompt_version"] = _promptVersion,
                ["output"] = reply.Output.DeepClone(),
            };
            WriteAtomic(cachePath, envelope.ToJsonString());
            Journal(label, key, reply);
            return reply;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ModelReply> SendWithRetriesAsync(string label, ModelRequest request, CancellationToken token)
    {
        const int maxAttempts = 6;
        Exception? last = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            await WaitForGateAsync(token);

            try
            {
                using var response = await _http.PostAsync(Endpoint, BuildBody(request), token);
                var body = await response.Content.ReadAsStringAsync(token);

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
                {
                    TripGate(response.Headers.RetryAfter?.Delta, attempt);
                    last = new HttpRequestException($"{label}: {(int)response.StatusCode} {body}");
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"{label}: {(int)response.StatusCode} {body}");

                return Parse(body, request.ToolName);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
            {
                last = ex;
                TripGate(null, attempt);
            }
        }

        throw new InvalidOperationException($"{label}: giving up after {maxAttempts} attempts: {last?.Message}", last);
    }

    private StringContent BuildBody(ModelRequest request)
    {
        var payload = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = request.MaxTokens,
            // Exact figures, not prose: sampling at the default temperature made two runs of the
            // same input disagree on a ninth of the verdicts.
            ["temperature"] = 0,
            ["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = request.System,
                // The instruction block is identical across every call, so it is worth caching.
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            }),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = request.ToolName,
                ["description"] = "Return the structured result.",
                ["input_schema"] = request.ToolSchema.DeepClone(),
            }),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = request.ToolName },
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = BuildContent(request),
            }),
        };

        return new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
    }

    /// <summary>
    /// Pages without a text layer never reach the prompt as text, so the file itself is attached
    /// and the model renders it. Attachments come first: the text that follows refers to them.
    /// </summary>
    private static JsonArray BuildContent(ModelRequest request)
    {
        var content = new JsonArray();

        foreach (var path in request.PdfPaths ?? Array.Empty<string>())
        {
            content.Add(new JsonObject
            {
                ["type"] = "document",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = "application/pdf",
                    ["data"] = Convert.ToBase64String(File.ReadAllBytes(path)),
                },
                ["title"] = Path.GetFileName(path),
            });
        }

        content.Add(new JsonObject { ["type"] = "text", ["text"] = request.Prompt });
        return content;
    }

    private static ModelReply Parse(string body, string toolName)
    {
        var root = JsonNode.Parse(body)!;
        var blocks = root["content"]?.AsArray() ?? throw new InvalidDataException("no content in reply");

        foreach (var block in blocks)
        {
            if (block?["type"]?.GetValue<string>() != "tool_use") continue;
            if (block["name"]?.GetValue<string>() != toolName) continue;

            return new ModelReply(
                block["input"]!.DeepClone(),
                false,
                root["usage"]?["input_tokens"]?.GetValue<int>() ?? 0,
                root["usage"]?["output_tokens"]?.GetValue<int>() ?? 0);
        }

        throw new InvalidDataException($"reply carried no {toolName} tool call");
    }

    /// <summary>
    /// One worker hitting a rate limit means the others are about to. The gate pauses everyone,
    /// with full jitter so they do not resynchronise into a second burst.
    /// </summary>
    private async Task WaitForGateAsync(CancellationToken token)
    {
        while (true)
        {
            var until = new DateTime(Interlocked.Read(ref _pauseUntilTicks), DateTimeKind.Utc);
            var wait = until - DateTime.UtcNow;
            if (wait <= TimeSpan.Zero) return;
            await Task.Delay(wait, token);
        }
    }

    private void TripGate(TimeSpan? retryAfter, int attempt)
    {
        var backoff = retryAfter ?? TimeSpan.FromMilliseconds(
            Random.Shared.Next(500, (int)Math.Min(30_000, 1000 * Math.Pow(2, attempt + 1))));
        var until = DateTime.UtcNow.Add(backoff).Ticks;

        long previous;
        do
        {
            previous = Interlocked.Read(ref _pauseUntilTicks);
            if (until <= previous) return;
        }
        while (Interlocked.CompareExchange(ref _pauseUntilTicks, until, previous) != previous);
    }

    private string CacheKey(ModelRequest request)
    {
        var attachments = string.Join(',', (request.PdfPaths ?? Array.Empty<string>())
            .Select(p => $"{Path.GetFileName(p)}:{new FileInfo(p).Length}"));
        var material = string.Join(' ', _model, _promptVersion, request.System, request.Prompt, request.ToolSchema.ToJsonString(), attachments);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..32];
    }

    private void Journal(string label, string key, ModelReply reply)
    {
        if (_journalPath is null) return;

        var line = new JsonObject
        {
            ["at"] = DateTime.UtcNow.ToString("O"),
            ["label"] = label,
            ["model"] = _model,
            ["cache_key"] = key,
            ["input_tokens"] = reply.InputTokens,
            ["output_tokens"] = reply.OutputTokens,
        }.ToJsonString();

        lock (_http) File.AppendAllText(_journalPath, line + Environment.NewLine);
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
