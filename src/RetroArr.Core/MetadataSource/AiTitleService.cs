using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetroArr.Core.Games;

namespace RetroArr.Core.MetadataSource
{
    /// <summary>
    /// Asks an OpenAI model which game and platform a release is, from its name and the files in it, the way GameSauce
    /// did. The import uses the answer first and has IGDB double-check the name. Off unless OPENAI_API_KEY is set;
    /// OPENAI_MODEL picks the model, AI_MIN_CONFIDENCE (default 0.70) how sure it has to be. Each release is asked once.
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public class AiTitleService
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetLogger(Logging.AppLoggerService.DownloadsImport);

        public sealed record Guess(string Platform, string Title, double Confidence, string Reason);

        private readonly HttpClient _http;
        private readonly string? _apiKey;
        private readonly string _model;
        private readonly string _baseUrl;
        private readonly ConcurrentDictionary<string, Guess?> _answers = new();

        public double MinConfidence { get; }

        public AiTitleService(HttpClient http, string? apiKey = null, string? model = null, string? baseUrl = null, double? minConfidence = null)
        {
            _http = http;
            _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            _model = model ?? Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-6-luna";
            _baseUrl = (baseUrl ?? Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "https://api.openai.com/v1").TrimEnd('/');
            MinConfidence = minConfidence
                ?? (double.TryParse(Environment.GetEnvironmentVariable("AI_MIN_CONFIDENCE"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var min) ? min : 0.70);
        }

        public bool IsEnabled => !string.IsNullOrWhiteSpace(_apiKey);

        /// <summary>A confident guess, or null when the model is off, unsure, names no known platform or can't be reached.</summary>
        public async Task<Guess?> IdentifyAsync(string releaseName, IEnumerable<string> fileNames, CancellationToken ct = default)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(releaseName)) return null;
            var files = fileNames.Take(300).ToList();
            var key = releaseName + "\n" + string.Join("\n", files);
            if (_answers.TryGetValue(key, out var known)) return known;

            Guess? guess = null;
            try
            {
                guess = await AskAsync(releaseName, files, ct).ConfigureAwait(false);
                _logger.Info(guess == null
                    ? $"[AI] No confident answer for '{releaseName}'"
                    : $"[AI] '{releaseName}' -> {guess.Platform} / {guess.Title} ({guess.Confidence:P0}): {guess.Reason}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A failed call is asked again next time; the import falls back to IGDB on its own meanwhile
                _logger.Warn($"[AI] Could not ask about '{releaseName}': {ex.Message}");
                return null;
            }
            _answers[key] = guess;
            return guess;
        }

        private async Task<Guess?> AskAsync(string releaseName, List<string> files, CancellationToken ct)
        {
            var platforms = PlatformDefinitions.AllPlatforms.Where(p => p.Enabled && p.FolderName != "unknown")
                .Select(p => $"{p.FolderName}: {p.Name}");
            var prompt = $@"You identify video game releases for a game library.

Release name:
{releaseName}

Files in it:
{string.Join("\n", files)}

Valid platforms (folder: name):
{string.Join("\n", platforms)}

Return JSON only:
{{""platform"": ""exact folder from the list or unknown"", ""title"": ""the game's official title, no group, version, region or format tags"", ""confidence"": 0.0, ""reason"": ""brief reason""}}

Use the release name, the file names and extensions together. A title ID (PPSA, CUSA, BCUS, a 16-digit Switch ID) names the platform. If it is genuinely ambiguous, use unknown and a low confidence.";

            using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/responses")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { model = _model, input = prompt }), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"OpenAI answered {(int)response.StatusCode}");
            return Parse(OutputText(body));
        }

        // The Responses API puts the text under output[].content[] as "output_text"
        internal static string OutputText(string body)
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("output_text", out var direct) && direct.ValueKind == JsonValueKind.String)
                return direct.GetString() ?? string.Empty;
            var text = new StringBuilder();
            if (doc.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                foreach (var item in output.EnumerateArray())
                    if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        foreach (var part in content.EnumerateArray())
                            if (part.TryGetProperty("type", out var type) && type.GetString() == "output_text"
                                && part.TryGetProperty("text", out var t))
                                text.Append(t.GetString());
            return text.ToString();
        }

        internal Guess? Parse(string text)
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            using var doc = JsonDocument.Parse(text.Substring(start, end - start + 1));
            var root = doc.RootElement;
            string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : string.Empty;
            var platform = Str("platform").ToLowerInvariant();
            var title = Str("title");
            var confidence = root.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var d) ? d : 0;
            var known = PlatformDefinitions.AllPlatforms.FirstOrDefault(p => p.FolderName != "unknown" && p.MatchesFolderName(platform));
            if (known == null || string.IsNullOrWhiteSpace(title) || confidence < MinConfidence) return null;
            return new Guess(known.FolderName, title, confidence, Str("reason"));
        }
    }
}
