using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace PtoLauncher;

sealed class UpdateInfo
{
    public bool Available;
    public string Version = "";
    public string Changelog = "";
    public string ZipUrl = "";
}

// Auto-update check against the public release repo. Best-effort: any failure (offline, rate limit,
// non-semver tag, not newer, no zip asset) yields Available=false so the launcher runs unchanged.
static class UpdateChecker
{
    const string LatestApi = "https://api.github.com/repos/Shmubbier/Pixel-Tactics-Launcher/releases/latest";

    public static async Task<UpdateInfo> CheckAsync()
    {
        var info = new UpdateInfo();
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ptolaunch");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json = await http.GetStringAsync(LatestApi);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            if (!SemVer.TryParse(tag, out var latest)) return info;
            if (!SemVer.TryParse(LauncherInfo.LauncherVersion, out var cur)) return info;
            if (latest.CompareTo(cur) <= 0) return info; // not newer

            string zip = "";
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        && a.TryGetProperty("browser_download_url", out var u))
                    { zip = u.GetString() ?? ""; break; }
                }
            if (zip.Length == 0) return info; // nothing downloadable

            info.Available = true;
            info.Version = tag.TrimStart('v', 'V');
            info.Changelog = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
            info.ZipUrl = zip;
        }
        catch { info.Available = false; }
        return info;
    }
}
