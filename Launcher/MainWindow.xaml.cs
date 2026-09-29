using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace PtoLauncher;

// WPF launcher window (design MainWindow.xaml in Visual Studio / Blend). One PLAY button that
// runs the offline+online play loop in-process (Program.PlayAsync) and launches the game exe;
// a settings gear that toggles Online/Offline; a collapsible log panel fed by the backend's
// Console output (so there is no separate console window). Online by default, auto-falling back
// to Offline when there is no network or Steam is not installed/running. Offline still launches
// the game (campaign/bot); only matchmaking is skipped. Update-check is a stub (UpdateAvailable).
public partial class MainWindow : Window
{
    const int GamePort = 15338;   // the port PlayAsync's proxy binds; the game dials it (client patch 24)
    const double CollapsedH = 320, ExpandedH = 540;

    readonly OnlineState _online = new();
    SteamRelay? _relay;           // warmed on Play when Online; null in Offline
    bool _playLoopStarted;
    Process? _game;

    bool _onlineMode;             // effective state (what Play will do)
    string _reason = "";          // why Offline, shown in the status line
    bool _logShown;

    // Run the window on a dedicated STA thread (WPF requires STA). Console verbs
    // (play/check/demo/...) stay on the normal path; only the windowed launcher opens here.
    public static int Run()
    {
        int code = 0;
        var t = new Thread(() =>
        {
            try
            {
                var app = new Application();
                app.Run(new MainWindow());
            }
            catch (Exception ex) { Console.WriteLine($"launcher window failed: {ex.Message}"); code = 1; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return code;
    }

    public MainWindow()
    {
        InitializeComponent();
        // Force a full process exit when the window closes, so the launcher never lingers in the
        // background holding the game port (background accept loop / relay threads could otherwise
        // keep it alive). The spawned PtoServer is a separate process and survives for reuse.
        Closed += (_, _) => Environment.Exit(0);
        // Pipe Console (and stderr) into the log panel: no separate console window.
        var writer = new ConsoleLogWriter(this);
        Console.SetOut(writer);
        Console.SetError(writer);
        // Absorbs what play.bat did: expose the Firebase URL to this process (MetaClient) and
        // the spawned server. Then initial online/offline state from detection + saved preference.
        EnsureFirebaseEnv();
        RecomputeState(LoadOnlinePref());
    }

    // ---- online/offline state -------------------------------------------------------------
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
        PlayButton.Content = UpdateAvailable() ? "UPDATE" : "PLAY";
        string line = _onlineMode ? "Online" : "Offline" + (string.IsNullOrEmpty(_reason) ? " (by choice)" : " - " + _reason);
        StatusText.Text = line + "     (gear to switch)";
    }

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

    // ---- preference persistence (best-effort) ---------------------------------------------
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

    // ---- firebase.txt -> env (so no wrapper script is needed) -----------------------------
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

    // ---- event handlers (wired from MainWindow.xaml) --------------------------------------
    void OnGear(object sender, RoutedEventArgs e)
    {
        bool newPref = !_onlineMode; // flip relative to current effective state
        SaveOnlinePref(newPref);
        RecomputeState(newPref);
    }

    void OnToggleLog(object sender, RoutedEventArgs e)
    {
        _logShown = !_logShown;
        LogBox.Visibility = _logShown ? Visibility.Visible : Visibility.Collapsed;
        LogToggle.Content = _logShown ? "▴ Hide log" : "▾ Show log";
        Height = _logShown ? ExpandedH : CollapsedH;
    }

    void OnPlay(object sender, RoutedEventArgs e)
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
        PlayButton.IsEnabled = false;
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
                _game.Exited += (_, _) => { SetStatus("Game closed. Ready to play again."); SetPlayEnabled(true); };
            }
            SetStatus("In game.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not launch the game: {ex.Message}");
            SetPlayEnabled(true);
        }
    }

    // Game exe: PTO_GAME_EXE if set, else ptoc.exe beside the launcher (deployment layout), a
    // sibling game/ folder (playtest package), else the repo's dev layout.
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
    // a versioning/publish scheme, and the in-place-replace behavior decided first (see
    // docs/superpowers/specs/2026-09-28-pillar-f-launcher-findings.md, section 4). Until then this
    // is always false and the button reads PLAY. Seam: a future check compares an embedded version
    // to the latest release tag from the GitHub Releases API here.
    static bool UpdateAvailable() => false;

    // ---- ui + log helpers -----------------------------------------------------------------
    void SetStatus(string text)
    {
        if (Dispatcher.CheckAccess()) StatusText.Text = text;
        else Dispatcher.BeginInvoke(new Action(() => StatusText.Text = text));
    }

    void SetPlayEnabled(bool enabled)
    {
        if (Dispatcher.CheckAccess()) PlayButton.IsEnabled = enabled;
        else Dispatcher.BeginInvoke(new Action(() => PlayButton.IsEnabled = enabled));
    }

    // Append backend Console output to the log panel. Thread-safe (called from the play loop and
    // the Steam pump thread): marshals to the UI thread and autoscrolls.
    public void AppendLog(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Dispatcher.CheckAccess()) { LogBox.AppendText(text); LogBox.ScrollToEnd(); }
        else Dispatcher.BeginInvoke(new Action(() => { LogBox.AppendText(text); LogBox.ScrollToEnd(); }));
    }

    // Routes Console.Out/Error into the log panel (see ctor).
    sealed class ConsoleLogWriter : TextWriter
    {
        readonly MainWindow _win;
        public ConsoleLogWriter(MainWindow win) { _win = win; }
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => _win.AppendLog(value.ToString());
        public override void Write(string? value) { if (value != null) _win.AppendLog(value); }
        public override void WriteLine(string? value) => _win.AppendLog((value ?? "") + Environment.NewLine);
        public override void WriteLine() => _win.AppendLog(Environment.NewLine);
    }
}
