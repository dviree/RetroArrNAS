using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using RetroArr.Core.Games;

namespace RetroArr.Api.V3.Platforms
{
    [ApiController]
    [Route("api/v3/playstation")]
    public class PlayStationController : ControllerBase
    {
        // Only the game folders RetroArr itself works in
        private static readonly string[] Roots = { "/media", "/downloads", "/temp", "/games", "/staging" };

        /// <summary>What a PS4/PS5 folder or file is: uncompressed, LZ4, exFAT, FFPFSC, PKG or archive, with title ID and version.</summary>
        [HttpGet("inspect")]
        public ActionResult Inspect([FromQuery] string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return BadRequest(new { message = "path is required" });
            var full = Path.GetFullPath(path);
            if (!Roots.Any(r => full == r || full.StartsWith(r + "/", StringComparison.Ordinal)))
                return BadRequest(new { message = $"path must be under {string.Join(", ", Roots)}" });
            if (!System.IO.File.Exists(full) && !Directory.Exists(full)) return NotFound(new { message = $"{full} does not exist" });
            var p = PlayStationPackageInspector.Inspect(full);
            return Ok(new
            {
                path = full,
                format = p.Format.ToString(),
                compressed = p.IsCompressed,
                titleId = p.TitleId,
                version = p.Version,
                title = p.Title,
                gameFolder = p.GameFolder
            });
        }
    }
}
