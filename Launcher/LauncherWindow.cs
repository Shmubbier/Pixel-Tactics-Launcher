using System.Diagnostics;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PtoLauncher;

// Minimal native launcher window (replaces the WebView2/Figma draft). One PLAY button that
// runs the offline+online play loop in-process (Program.PlayAsync) and launches the game exe;
// a settings gear that toggles Online/Offline; a status line. Online by default, auto-falling
// back to Offline when there is no network or Steam is not installed/running. The game still
// launches and plays (offline/campaign/bot) in Offline mode; only matchmaking is skipped.
// Update-check is a stub (see UpdateAvailable): the GitHub self-update is deferred pending the
// release-repo/versioning decision (docs/superpowers/specs/2026-09-28-pillar-f-launcher-findings.md).
sealed class LauncherWindow : Form
{
    const int GamePort = 51338; // the port PlayAsync's proxy binds; the game dials it

    readonly OnlineState _online = new();
    SteamRelay? _relay;         // warmed on Play when Online; null in Offline
    bool _playLoopStarted;
    Process? _game;

    bool _onlineMode;           // effective state (what Play will do)
    string _reason = "";        // why Offline, shown in the status line

    Button _play = null!;
    Button _gear = null!;
    Label _status = null!;
    TextBox _log = null!;
    Button _logToggle = null!;
    bool _logShown;
    const int CollapsedH = 320, ExpandedH = 540;
    readonly StringBuilder _logBuffer = new(); // holds Console output until the handle exists

    // Run the window on a dedicated STA thread (WinForms requires STA). Console verbs
    // (play/check/demo/...) stay on the normal path; only the windowed launcher opens here.
    public static int Run()
    {
        int code = 0;
        var t = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new LauncherWindow());
            }
            catch (Exception ex) { Console.WriteLine($"launcher window failed: {ex.Message}"); code = 1; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return code;
    }

    LauncherWindow()
    {
        Text = "Pixel Tactics Online";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 320);
        BackColor = Color.FromArgb(0x14, 0x12, 0x1c);
        Font = new Font("Segoe UI", 9f);

        var title = new Label
        {
            Text = "PIXEL TACTICS ONLINE",
            ForeColor = Color.FromArgb(0xf2, 0xb1, 0x34),
            Font = new Font("Segoe UI", 18f, FontStyle.Bold),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, 60),
            Size = new Size(480, 40),
        };

        _play = new Button
        {
            Text = "PLAY",
            Font = new Font("Segoe UI", 15f, FontStyle.Bold),
            ForeColor = Color.Black,
            BackColor = Color.FromArgb(0xf2, 0xb1, 0x34),
            FlatStyle = FlatStyle.Flat,
            Size = new Size(220, 60),
            Location = new Point((480 - 220) / 2, 150),
        };
        _play.FlatAppearance.BorderSize = 0;
        _play.Click += (_, _) => OnPlay();

        _gear = new Button
        {
            Text = "⚙", // gear glyph
            Font = new Font("Segoe UI", 14f),
            ForeColor = Color.Gainsboro,
            BackColor = Color.FromArgb(0x24, 0x22, 0x30),
            FlatStyle = FlatStyle.Flat,
            Size = new Size(40, 40),
            Location = new Point(480 - 40 - 12, 12),
        };
        _gear.FlatAppearance.BorderSize = 0;
        _gear.Click += (_, _) => OnToggleOnline();

        _status = new Label
        {
            ForeColor = Color.Gainsboro,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, 250),
            Size = new Size(480, 24),
        };

        // Collapsible log panel (Minecraft-style): the backend's Console output is piped here
        // instead of a separate console window. Hidden by default; the toggle grows the window.
        _logToggle = new Button
        {
            Text = "▾ Show log",
            Font = new Font("Segoe UI", 8f),
            ForeColor = Color.Gainsboro,
            BackColor = Color.FromArgb(0x24, 0x22, 0x30),
            FlatStyle = FlatStyle.Flat,
            Size = new Size(110, 24),
            Location = new Point(12, 284),
        };
        _logToggle.FlatAppearance.BorderSize = 0;
        _logToggle.Click += (_, _) => ToggleLog();

        _log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(0x0d, 0x0c, 0x14),
            ForeColor = Color.FromArgb(0xba, 0xd0, 0xa0),
            Font = new Font("Consolas", 8f),
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(12, 316),
            Size = new Size(456, ExpandedH - 316 - 12),
            Visible = false,
            TabStop = false,
        };

        Controls.Add(title);
        Controls.Add(_play);
        Controls.Add(_gear);
        Controls.Add(_status);
        Controls.Add(_logToggle);
        Controls.Add(_log);

        // Pipe Console (and stderr) into the log panel now, so startup messages below are captured.
        var writer = new ConsoleLogWriter(this);
        Console.SetOut(writer);
        Console.SetError(writer);
        // Flush anything logged before the window handle existed (buffered by AppendLog).
        Load += (_, _) => FlushLogBuffer();

        // Absorbs what play.bat used to do: make the Firebase URL available to both this
        // process (MetaClient) and the spawned server, so online matchmaking works with no
        // wrapper script. Then initial state from detection + saved preference.
        EnsureFirebaseEnv();
        bool? pref = LoadOnlinePref();
        RecomputeState(pref);
    }

    void ToggleLog()
    {
        _logShown = !_logShown;
        _log.Visible = _logShown;
        _logToggle.Text = _logShown ? "▴ Hide log" : "▾ Show log";
        ClientSize = new Size(480, _logShown ? ExpandedH : CollapsedH);
    }

    // Append backend Console output to the log panel. Thread-safe: called from the play loop and
    // the Steam pump thread. Buffers until the window handle exists, then appends on the UI thread.
    public void AppendLog(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!IsHandleCreated) { lock (_logBuffer) _logBuffer.Append(text); return; }
        try { BeginInvoke(() => { _log.AppendText(text); }); }
        catch { lock (_logBuffer) _logBuffer.Append(text); }
    }

    void FlushLogBuffer()
    {
        string pending;
        lock (_logBuffer) { pending = _logBuffer.ToString(); _logBuffer.Clear(); }
        if (pending.Length > 0) _log.AppendText(pending);
    }

    // Routes Console.Out/Error into the log panel (see ctor). Line-oriented writes are enough for
    // the backend's Console.WriteLine calls; a per-char Write covers the rest.
    sealed class ConsoleLogWriter : TextWriter
    {
        readonly LauncherWindow _win;
        public ConsoleLogWriter(LauncherWindow win) { _win = win; }
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => _win.AppendLog(value.ToString());
        public override void Write(string? value) { if (value != null) _win.AppendLog(value); }
        public override void WriteLine(string? value) => _win.AppendLog((value ?? "") + Environment.NewLine);
        public override void WriteLine() => _win.AppendLog(Environment.NewLine);
    }

    // If PTO_FIREBASE_URL is not already set, look for a firebase.txt in the bundle (beside the
    // exe, then up toward the package root where testers drop it) and export its URL, so the
    // launcher's MetaClient AND the child PtoServer (which inherits this env) both see it. No
    // firebase.txt just means online is unavailable; campaign/offline are unaffected.
    static void EnsureFirebaseEnv()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PTO_FIREBASE_URL"))) return;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                var cand = Path.Combine(d.FullName, "firebase.txt");
                if (File.Exists(cand))
                {
                    var url = File.ReadAllText(cand).Trim();
                    if (!string.IsNullOrWhiteSpace(url)) Environment.SetEnvironmentVariable("PTO_FIREBASE_URL", url);
                    return;
                }
                if (d.Parent == null || d.Name.StartsWith("PTO_C151_P2P_Playtest")) break; // stop at the bundle root
            }
        }
        catch (Exception ex) { Console.WriteLine($"launcher: firebase.txt lookup skipped ({ex.Message})"); }
    }

    // ---- online/offline state -------------------------------------------------------------
    // Effective Online requires actual capability; a saved "online" preference can only take
    // effect when the machine is capable. Offline is always honored.
    void RecomputeState(bool? pref)
    {
        bool capable = OnlineCapable(out string why);
        bool want = pref ?? true; // default: prefer online
        _onlineMode = want && capable;
        _reason = why;
        UpdateUi();
    }

    void UpdateUi()
    {
        _play.Text = UpdateAvailable() ? "UPDATE" : "PLAY";
        string line = _onlineMode ? "Online" : "Offline" + (string.IsNullOrEmpty(_reason) ? " (by choice)" : " - " + _reason);
        _status.Text = line + "     (gear to switch)";
    }

    // Toggle the user's Online/Offline preference, re-evaluate, persist. Switching to Online is
    // always allowed; if the machine is not capable the state shows Offline with the reason.
    void OnToggleOnline()
    {
        bool newPref = !_onlineMode; // flip relative to current effective state
        SaveOnlinePref(newPref);
        RecomputeState(newPref);
    }

    // Online-capable = a network is up AND Steam is installed AND Steam is running. Sets `why`
    // to the first failing reason (shown when Offline).
    static bool OnlineCapable(out string why)
    {
        if (!NetworkAvailable()) { why = "no network"; return false; }
        if (!SteamInstalled()) { why = "Steam not installed"; return false; }
        if (!SteamRunning()) { why = "Steam not running"; return false; }
        why = "";
        return true;
    }

    static bool NetworkAvailable()
    {
        try { return NetworkInterface.GetIsNetworkAvailable(); }
        catch { return false; }
    }

    static bool SteamInstalled()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                if (k?.GetValue("SteamPath") != null) return true;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                if (k?.GetValue("InstallPath") != null) return true;
        }
        catch { }
        return false;
    }

    static bool SteamRunning()
    {
        try { return Process.GetProcessesByName("steam").Length > 0; }
        catch { return false; }
    }

    // ---- preference persistence (best-effort; never blocks the UI) ------------------------
    static string PrefPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ptoc-launcher");
        return Path.Combine(dir, "settings.ini");
    }

    static bool? LoadOnlinePref()
    {
        try
        {
            string path = PrefPath();
            if (!File.Exists(path)) return null;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("online=", StringComparison.OrdinalIgnoreCase))
                    return line.Substring("online=".Length).Trim() == "1";
            }
        }
        catch { }
        return null;
    }

    static void SaveOnlinePref(bool online)
    {
        try
        {
            string path = PrefPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "online=" + (online ? "1" : "0") + Environment.NewLine);
        }
        catch (Exception ex) { Console.WriteLine($"launcher: could not save preference ({ex.Message})"); }
    }

    // ---- Play ------------------------------------------------------------------------------
    void OnPlay()
    {
        if (_game is { HasExited: false }) { SetStatus("Game is already running."); return; }

        _online.Offline = !_onlineMode;
        if (_onlineMode && _relay == null)
        {
            // Warm the Steam relay once so its route is ready by match time. If Steam vanished
            // since startup, fall back to Offline instead of crashing.
            try { _relay = new SteamRelay(); _relay.Init(); _online.Relay = _relay; }
            catch (Exception ex)
            {
                Console.WriteLine($"steam not available: {ex.Message}");
                _relay = null; _online.Offline = true; _onlineMode = false; _reason = "Steam not available";
                UpdateUi();
            }
        }

        SetStatus(_online.Offline ? "Starting game (offline)..." : "Starting server and going online...");
        _play.Enabled = false;
        if (!_playLoopStarted)
        {
            _playLoopStarted = true;
            _ = Task.Run(async () =>
            {
                try { await Program.PlayAsync(_online); }
                catch (Exception ex) { Console.WriteLine($"play loop ended: {ex.Message}"); }
            });
        }
        _ = LaunchGameWhenReadyAsync();
    }

    // Wait until PlayAsync's proxy is accepting on GamePort, then launch the game so it never
    // dials a closed port on a cold first run (server spawn can take a few seconds).
    async Task LaunchGameWhenReadyAsync()
    {
        for (int i = 0; i < 40; i++) // up to ~20s
        {
            try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", GamePort); break; }
            catch { await Task.Delay(500); }
        }
        try
        {
            string exe = GameExe();
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe)) ?? "." };
            _game = Process.Start(psi);
            if (_game != null)
            {
                _game.EnableRaisingEvents = true;
                _game.Exited += (_, _) => OnGameExited();
            }
            SetStatus("In game.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not launch the game: {ex.Message}");
            SetPlayEnabled(true);
        }
    }

    void OnGameExited()
    {
        SetStatus("Game closed. Ready to play again.");
        SetPlayEnabled(true);
    }

    // Game exe: PTO_GAME_EXE if set, else ptoc.exe beside the launcher (deployment layout),
    // else walk up to the repo's Clients\Steam_Ver-PTO-C\ptoc.exe (dev layout).
    static string GameExe()
    {
        var env = Environment.GetEnvironmentVariable("PTO_GAME_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var beside = Path.Combine(AppContext.BaseDirectory, "ptoc.exe");
        if (File.Exists(beside)) return beside;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var pkg = Path.Combine(d.FullName, "game", "ptoc.exe"); // playtest package: launcher/ sibling game/
            if (File.Exists(pkg)) return pkg;
            var cand = Path.Combine(d.FullName, "Clients", "Steam_Ver-PTO-C", "ptoc.exe");
            if (File.Exists(cand)) return cand;
        }
        return beside; // not found: Process.Start throws and LaunchGameWhenReadyAsync reports it
    }

    // ---- update check (STUB) --------------------------------------------------------------
    // Deferred: the GitHub-release self-update needs the release repo (public vs token-gated),
    // a versioning/publish scheme, and the in-place-replace behavior decided first (see the
    // launcher findings doc, section 4). Until then this is always false and the button reads
    // PLAY. Seam: a future implementation compares an embedded version to the latest release
    // tag from the GitHub Releases API here.
    static bool UpdateAvailable() => false;

    // ---- ui helpers -----------------------------------------------------------------------
    void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => _status.Text = text); }
        else _status.Text = text;
    }

    void SetPlayEnabled(bool enabled)
    {
        if (InvokeRequired) { BeginInvoke(() => _play.Enabled = enabled); }
        else _play.Enabled = enabled;
    }
}
