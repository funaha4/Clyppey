namespace Clyppey.Core;

/// <summary>
/// Where Clyppey keeps its data. Directories are chosen by actually writing a probe file,
/// because "this path should be writable" is not something to assume: locked-down machines,
/// roaming profiles and redirected folders all break the naive answer.
/// </summary>
public static class AppPaths
{
    private static readonly string Root = ResolveAppDataRoot();

    public static string AppDataRoot => Root;

    /// <summary>True when the normal per-user location was unusable and a fallback was chosen.</summary>
    public static bool IsFallbackRoot { get; private set; }

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string ClipLibraryFile => Path.Combine(Root, "clips.json");

    public static string ProbeCacheFile => Path.Combine(Root, "capabilities.json");

    public static string LogFile => Path.Combine(Root, "clyppey.log");

    /// <summary>Scratch space for throwaway files (audio WAVs, in-progress recordings).</summary>
    public static string TempDir => Path.Combine(Root, "temp");

    /// <summary>Last-resort clip location when the user's Videos folder is not writable.</summary>
    public static string FallbackClipsDir => Path.Combine(Root, "clips");

    public static string DefaultOutputDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clyppey");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(TempDir);
    }

    /// <summary>Creates the directory if needed and proves it is writable.</summary>
    public static bool TryEnsureWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".clyppey_write_test_{Guid.NewGuid():N}");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            // Not writable. Not logged on purpose: this runs while AppPaths itself is being
            // initialised, and Log depends on AppPaths.
            return false;
        }
    }

    private static string ResolveAppDataRoot()
    {
        var candidates = CandidateRoots().ToList();

        foreach (var candidate in candidates)
        {
            if (TryEnsureWritable(candidate))
            {
                IsFallbackRoot = candidates.Count > 0 &&
                                 !string.Equals(candidate, candidates[0], StringComparison.OrdinalIgnoreCase);
                return candidate;
            }
        }

        // Nothing worked; stay somewhere deterministic so failures are at least coherent.
        IsFallbackRoot = true;
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Every place the app has ever been able to keep its data, most preferred first.
    /// Exposed so settings can be recovered when a previous run had to use a fallback.
    /// </summary>
    public static IEnumerable<string> CandidateRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Clyppey");
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roaming))
        {
            yield return Path.Combine(roaming, "Clyppey");
        }

        yield return Path.Combine(AppContext.BaseDirectory, "ClyppeyData");
    }

    /// <summary>
    /// If this run cannot see a settings file but a previous run wrote one into another
    /// candidate folder, copy it in. Without this, settings written while the preferred folder
    /// was unavailable look like they were never saved.
    /// </summary>
    public static void MigrateSettingsIfNeeded()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                return;
            }

            foreach (var root in CandidateRoots())
            {
                if (string.Equals(Path.GetFullPath(root).TrimEnd('\\'),
                        Path.GetFullPath(AppDataRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candidate = Path.Combine(root, "settings.json");
                if (!File.Exists(candidate))
                {
                    continue;
                }

                Directory.CreateDirectory(AppDataRoot);
                File.Copy(candidate, SettingsFile, overwrite: false);
                Log.Info($"設定を別のフォルダから引き継ぎました: {candidate}");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"設定の引き継ぎに失敗: {ex.Message}");
        }
    }

    /// <summary>
    /// Locates ffmpeg.exe. Order: CLYPPEY_FFMPEG env var, a "tools/ffmpeg" folder in any
    /// parent of the app directory (covers both dev and published layouts), the app
    /// directory itself, then PATH.
    /// </summary>
    public static string? FindFfmpeg()
    {
        var overridePath = Environment.GetEnvironmentVariable("CLYPPEY_FFMPEG");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        foreach (var dir in SearchRoots())
        {
            var bundled = Path.Combine(dir, "tools", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(bundled))
            {
                return bundled;
            }

            var local = Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(local))
            {
                return local;
            }
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var part in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(part.Trim().Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry - ignore.
            }
        }

        return null;
    }

    private static IEnumerable<string> SearchRoots()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            yield return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;
        }
    }
}
