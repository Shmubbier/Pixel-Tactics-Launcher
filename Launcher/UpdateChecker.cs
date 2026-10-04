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

    // Download the release zip, extract it, then hand off to a PowerShell updater that swaps the files
    // after this process exits and relaunches. On any pre-exit failure we show a message and keep running.
    public static async Task DownloadAndApplyAsync(UpdateInfo info, System.Windows.Window owner)
    {
        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ptoc-update");
        string zip = System.IO.Path.Combine(tmp, "update.zip");
        string ext = System.IO.Path.Combine(tmp, "extracted");
        try
        {
            if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true);
            System.IO.Directory.CreateDirectory(tmp);

            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ptolaunch");
                var bytes = await http.GetByteArrayAsync(info.ZipUrl);
                await System.IO.File.WriteAllBytesAsync(zip, bytes);
            }
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, ext);

            string srcRoot = FindBundleRoot(ext);
            // We run from <root>/launcher/; the bundle root is its parent.
            string bundleRoot = System.IO.Directory.GetParent(AppContext.BaseDirectory)!.FullName;
            int pid = Environment.ProcessId;
            string ps1 = WriteUpdaterScript(tmp, pid, srcRoot, bundleRoot);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"",
                UseShellExecute = true,
            });
            Program.Discord.Shutdown();
            Environment.Exit(0); // unlock our files; the updater waits for this pid, swaps, relaunches
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(owner,
                "Update failed to download or extract:\n" + ex.Message + "\n\nThe current version still works.",
                "Update", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    // The zip may contain the bundle folder at its root or one level down; find the dir with a launcher/.
    static string FindBundleRoot(string ext)
    {
        if (System.IO.Directory.Exists(System.IO.Path.Combine(ext, "launcher"))) return ext;
        foreach (var d in System.IO.Directory.GetDirectories(ext))
            if (System.IO.Directory.Exists(System.IO.Path.Combine(d, "launcher"))) return d;
        return ext;
    }

    // Write the updater: wait for the launcher to exit, back up, mirror game/server/launcher over the
    // bundle (NEVER server/data), relaunch, and roll back from the backup on failure.
    static string WriteUpdaterScript(string tmp, int pid, string srcRoot, string bundleRoot)
    {
        string path = System.IO.Path.Combine(tmp, "apply-update.ps1");
        string src = srcRoot.Replace("'", "''");
        string root = bundleRoot.Replace("'", "''");
        string script =
$@"$ErrorActionPreference = 'Stop'
$src = '{src}'
$root = '{root}'
$backup = Join-Path $root '.update-backup'
try {{ Wait-Process -Id {pid} -Timeout 30 -ErrorAction SilentlyContinue }} catch {{}}
Start-Sleep -Milliseconds 700
if (Test-Path $backup) {{ Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue }}
New-Item -ItemType Directory -Force -Path $backup | Out-Null
function Swap($name) {{
  $s = Join-Path $src $name; $d = Join-Path $root $name
  if (-not (Test-Path $s)) {{ return }}
  if (Test-Path $d) {{ Copy-Item $d (Join-Path $backup $name) -Recurse -Force }}
  if ($name -eq 'server') {{ robocopy $s $d /E /NFL /NDL /NJH /NJS /NP /XD (Join-Path $d 'data') | Out-Null }}
  else {{ robocopy $s $d /MIR /NFL /NDL /NJH /NJS /NP | Out-Null }}
  if ($LASTEXITCODE -ge 8) {{ throw ('robocopy failed for ' + $name + ' (code ' + $LASTEXITCODE + ')') }}
  $global:LASTEXITCODE = 0
}}
try {{
  Swap 'game'; Swap 'server'; Swap 'launcher'
}} catch {{
  foreach ($n in 'game','server','launcher') {{
    $b = Join-Path $backup $n
    if (Test-Path $b) {{ robocopy $b (Join-Path $root $n) /E /NFL /NDL /NJH /NJS /NP | Out-Null }}
  }}
}}
Start-Process (Join-Path $root 'launcher\ptolaunch.exe') -WorkingDirectory (Join-Path $root 'launcher')
";
        System.IO.File.WriteAllText(path, script);
        return path;
    }
}
