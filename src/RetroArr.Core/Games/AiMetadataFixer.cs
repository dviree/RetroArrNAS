using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using RetroArr.Core.MetadataSource;

namespace RetroArr.Core.Games
{
    /// <summary>
    /// Library entries without metadata that still carry a release name ("SAROS.PS5-DUPLEX", "PPSA07631-app0") get
    /// their title from the AI first, then IGDB is searched with it; an IGDB game whose title matches exactly gives the
    /// entry its metadata. Without such a match the entry keeps the AI's clean title. Entries a person confirmed are
    /// left alone, as is an IGDB id another entry on the same platform holds. Every hour, a batch at a time.
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    public class AiMetadataFixer : BackgroundService
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetLogger(Logging.AppLoggerService.General);
        private const int Batch = 40;

        private readonly IGameRepository _games;
        private readonly AiTitleService? _ai;
        private readonly HashSet<int> _tried = new();

        // internal for tests: the IGDB search (variants, platform folder) and the full record of one IGDB game
        internal Func<List<string>, string?, Task<List<(int Id, string Name)>>> Search;
        internal Func<int, string?, Task<Game?>> Details;

        public AiMetadataFixer(IGameRepository games, IGameMetadataServiceFactory metadata, AiTitleService? ai = null)
        {
            _games = games;
            _ai = ai;
            Search = async (variants, platform) =>
                (await metadata.CreateService().SearchWithVariantsAsync(variants, platform)).Select(r => (r.Id, r.Name ?? string.Empty)).ToList();
            Details = (id, platform) => metadata.CreateService().GetGameMetadataAsync(id, null, platform);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); }
            catch (TaskCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.Warn($"[AiMetadata] Run failed: {ex.Message}"); }
                try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        // A release name rather than a title: scene dots, a group, a title ID, format or source tags
        private static readonly Regex ReleaseLike = new(
            @"(\.\w+\.|-(DUPLEX|BlaZe|CUSA|PPSA|VENOM|LiGHTFORCE|SUXXORS|DarKmooN|NSW2U|BigBlueBox)\b|\b(PPSA|CUSA|ECAS)[-_]?\d{5}|\b(PS[345]|NSP|XCI|FPKG|app0|lz4|exfat|ffpfsc)\b|\[[^\]]*\]|_)",
            RegexOptions.IgnoreCase);

        // A scene release, not a ROM set name: no spaces, words joined by dots, dashes or underscores, or a PS title ID
        private static readonly Regex SceneRelease = new(@"^[^\s]+[._-][^\s]+$|\b(PPSA|CUSA|ECAS)[-_]?\d{5}", RegexOptions.IgnoreCase);

        /// <summary>A file or folder name the scanner should let the AI name before IGDB is searched.</summary>
        internal static bool LooksLikeRelease(string name) =>
            SceneRelease.IsMatch(name) && ReleaseLike.IsMatch(name);

        internal static bool NeedsFixing(Game g) =>
            !g.MetadataConfirmedByUser && (g.IgdbId is null or 0) && !string.IsNullOrWhiteSpace(g.Title) && ReleaseLike.IsMatch(g.Title);

        /// <summary>Fixes one batch. Returns how many entries got a new title or metadata.</summary>
        public async Task<int> RunOnceAsync(CancellationToken ct = default)
        {
            var all = await _games.GetAllLightAsync();
            var fixedCount = 0;
            foreach (var game in all.Where(g => NeedsFixing(g) && !_tried.Contains(g.Id)).Take(Batch))
            {
                ct.ThrowIfCancellationRequested();
                _tried.Add(game.Id);
                var platform = PlatformDefinitions.AllPlatforms.FirstOrDefault(p => p.Id == game.PlatformId);
                var files = FilesOf(game);

                string? title = null;
                if (_ai is { IsEnabled: true })
                {
                    var guess = await _ai.IdentifyAsync(game.Title, files, ct);
                    if (guess != null) title = guess.Title;
                }
                title ??= new TitleCleanerService().CleanGameTitle(game.Title).Title;
                if (string.IsNullOrWhiteSpace(title) || title == game.Title) continue;

                var variants = new TitleCleanerService().GenerateSearchVariants(title);
                if (variants.Count == 0) variants.Add(title);
                List<(int Id, string Name)> results;
                try { results = await Search(variants, platform?.FolderName); }
                catch (Exception ex) { _logger.Warn($"[AiMetadata] IGDB search for '{title}' failed: {ex.Message}"); results = new(); }
                var match = results.FirstOrDefault(r => Key(r.Name) == Key(title));
                var holder = match.Id > 0 ? all.FirstOrDefault(g => g.Id != game.Id && g.PlatformId == game.PlatformId && g.IgdbId == match.Id) : null;

                Game? meta = null;
                if (match.Id > 0 && holder == null)
                {
                    try { meta = await Details(match.Id, platform?.FolderName); }
                    catch (Exception ex) { _logger.Warn($"[AiMetadata] IGDB details for {match.Id} failed: {ex.Message}"); }
                }
                var newTitle = meta?.Title ?? title;
                // Another entry already has this title on this platform: leave both for a person to merge
                if (all.Any(g => g.Id != game.Id && g.PlatformId == game.PlatformId && g.Title.Equals(newTitle, StringComparison.OrdinalIgnoreCase))) continue;

                await _games.UpdateFieldsAsync(game.Id, g =>
                {
                    g.Title = newTitle;
                    if (meta == null) return;
                    g.IgdbId = meta.IgdbId;
                    g.Overview = meta.Overview;
                    g.Storyline = meta.Storyline;
                    g.Developer = meta.Developer;
                    g.Publisher = meta.Publisher;
                    g.Rating = meta.Rating;
                    g.RatingCount = meta.RatingCount;
                    g.Year = meta.Year;
                    g.ReleaseDate = meta.ReleaseDate;
                    g.Genres = meta.Genres;
                    g.Images = meta.Images;
                    g.MetadataSource = "IGDB";
                });
                _logger.Info($"[AiMetadata] '{game.Title}' -> '{newTitle}'{(meta != null ? $" (IGDB {meta.IgdbId})" : " (title only)")}");
                fixedCount++;
            }
            return fixedCount;
        }

        private static List<string> FilesOf(Game game)
        {
            var path = game.ExecutablePath ?? game.Path;
            try
            {
                if (path != null && Directory.Exists(path))
                    return Directory.EnumerateFileSystemEntries(path).Take(30).Select(Path.GetFileName).OfType<string>().ToList();
                if (path != null) return new List<string> { Path.GetFileName(path) };
            }
            catch (Exception) { }
            return new List<string>();
        }

        private static string Key(string? s) => new string((s ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
