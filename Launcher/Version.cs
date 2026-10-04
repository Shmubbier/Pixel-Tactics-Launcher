namespace PtoLauncher;

static class LauncherInfo
{
    // Bump on every release; keep Launcher.csproj <Version> identical (the publish stage enforces it).
    // Semver: MAJOR.MINOR.PATCH -> N.0.0 big release, 0.N.0 major patch, 0.0.N bug fix.
    public const string LauncherVersion = "1.1.0";
}

// Minimal MAJOR.MINOR.PATCH value, tolerant of a leading "v". Pre-release/build suffixes are not used.
readonly struct SemVer
{
    public readonly int Major, Minor, Patch;
    SemVer(int a, int b, int c) { Major = a; Minor = b; Patch = c; }

    public static bool TryParse(string? s, out SemVer v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);
        var p = s.Split('.');
        if (p.Length != 3) return false;
        if (!int.TryParse(p[0], out int a) || !int.TryParse(p[1], out int b) || !int.TryParse(p[2], out int c)) return false;
        if (a < 0 || b < 0 || c < 0) return false;
        v = new SemVer(a, b, c);
        return true;
    }

    public int CompareTo(SemVer o)
    {
        if (Major != o.Major) return Major.CompareTo(o.Major);
        if (Minor != o.Minor) return Minor.CompareTo(o.Minor);
        return Patch.CompareTo(o.Patch);
    }
}
