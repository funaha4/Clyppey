using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace Clyppey.Core.Update;

/// <summary>A newer release published on GitHub.</summary>
public sealed record UpdateInfo(string Version, string DownloadUrl, long SizeBytes, string Notes);

/// <summary>
/// Update checks against the GitHub release feed, plus the file swap.
///
/// Windows refuses to overwrite a running executable, so the swap is delegated to a tiny cmd
/// script: it waits for this process to exit, copies the new files over the install folder and
/// starts the app again.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = CreateHttpClient(TimeSpan.FromSeconds(20));
    private static readonly HttpClient DownloadHttp = CreateHttpClient(Timeout.InfiniteTimeSpan);

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Clyppey-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Returns the release when it is newer than <paramref name="currentVersion"/>.</summary>
    public static async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(AppInfo.ApiLatestRelease, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub に接続できません ({(int)response.StatusCode})");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var version = tag.TrimStart('v', 'V');
        if (!IsNewer(version, currentVersion))
        {
            return null;
        }

        string? url = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            // Prefer the documented -win-x64.zip (AGENTS.md §13); fall back to the first zip.
            string? fallbackUrl = null;
            long fallbackSize = 0;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candidateUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                var candidateSize = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                if (string.IsNullOrWhiteSpace(candidateUrl))
                {
                    continue;
                }

                if (fallbackUrl is null)
                {
                    fallbackUrl = candidateUrl;
                    fallbackSize = candidateSize;
                }

                if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                {
                    url = candidateUrl;
                    size = candidateSize;
                    break;
                }
            }

            url ??= fallbackUrl;
            if (fallbackUrl is not null && url == fallbackUrl)
            {
                size = fallbackSize;
            }
        }

        if (string.IsNullOrWhiteSpace(url) ||
            !url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty;
        return new UpdateInfo(version, url, size, notes);
    }

    /// <summary>Downloads the release zip into the app's own data folder.</summary>
    public static async Task<string> DownloadAsync(
        UpdateInfo update,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        // NOTE: HttpClient.Timeout covers the whole request (headers + body), so the 20 s
        // check client must not be reused here: a 90 MB zip needs minutes on slow links.
        // DownloadHttp has no timeout; the caller's CancellationToken still aborts it.
        var safeVersion = SanitizeVersion(update.Version);
        var directory = Path.Combine(AppPaths.AppDataRoot, "update", safeVersion);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "Clyppey.zip");

        try
        {
        using var response = await DownloadHttp
            .GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? update.SizeBytes;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var destination = File.Create(target))
        {
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                if (total > 0)
                {
                    progress?.Report(copied / (double)total);
                }
            }
        }

        return target;
        }
        catch (Exception)
        {
            // Never leave a truncated zip behind: Extract would fail on it next launch.
            try { if (File.Exists(target)) File.Delete(target); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Extracts the package and returns the folder that holds the new executable.</summary>
    public static string Extract(string zipPath)
    {
        var directory = Path.Combine(Path.GetDirectoryName(zipPath)!, "files");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        try
        {
            ZipFile.ExtractToDirectory(zipPath, directory);

            // The archive keeps everything under one top-level folder.
            var inner = Directory.GetDirectories(directory);
            var candidate = inner.Length == 1 && File.Exists(Path.Combine(inner[0], "Clyppey.exe"))
                ? inner[0]
                : directory;

            if (!File.Exists(Path.Combine(candidate, "Clyppey.exe")))
            {
                throw new InvalidOperationException("更新zipに Clyppey.exe がありません");
            }

            return candidate;
        }
        catch (Exception)
        {
            // Never leave a half-extracted tree: the next launch would pick it up as success.
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch { /* best effort */ }

            throw;
        }
    }

    /// <summary>
    /// Writes the swap script and starts it. The caller must quit right after this returns.
    /// </summary>
    public static void StartSwap(string sourceDirectory, string installDirectory, int processId)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(installDirectory))
        {
            throw new ArgumentException("更新フォルダが空です");
        }

        foreach (var c in new[] { '"', '\r', '\n', '%', '&', '|', '<', '>' })
        {
            if (sourceDirectory.Contains(c) || installDirectory.Contains(c))
            {
                throw new ArgumentException("更新パスに使えない文字があります");
            }
        }

        var scriptPath = Path.Combine(Path.GetDirectoryName(sourceDirectory)!, "apply-update.cmd");
        var staging = Path.GetDirectoryName(Path.GetDirectoryName(sourceDirectory))!;

        var script = $"""
            @echo off
            setlocal
            set "SRC={sourceDirectory}"
            set "DST={installDirectory}"
            :wait
            tasklist /FI "PID eq {processId}" 2>nul | find "{processId}" >nul
            if not errorlevel 1 (
              timeout /t 1 /nobreak >nul
              goto wait
            )
            robocopy "%SRC%" "%DST%" /E /NFL /NDL /NJH /NJS /R:3 /W:1 >nul
            if errorlevel 8 (
              exit /b 1
            )
            start "" "%DST%\Clyppey.exe"
            timeout /t 2 /nobreak >nul
            rmdir /s /q "{staging}" >nul 2>&1
            del "%~f0"
            """;

        File.WriteAllText(scriptPath, script.Replace("\r\n", "\n").Replace("\n", "\r\n"));

        var startInfo = new ProcessStartInfo("cmd.exe", $"/c \"\"{scriptPath}\"\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process.Start(startInfo);
    }

    private static bool IsNewer(string candidate, string current)
    {
        var left = Parse(candidate);
        var right = Parse(current);
        return left.CompareTo(right) > 0;
    }

    /// <summary>Version text safe for paths and scripts (digits, letters, dot, dash, underscore).</summary>
    internal static string SanitizeVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "unknown";
        }

        var cleaned = new string(version.Trim().Select(c =>
            char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_').ToArray());
        cleaned = cleaned.Trim('.', '-', '_');
        return string.IsNullOrEmpty(cleaned) ? "unknown" : cleaned[..Math.Min(cleaned.Length, 64)];
    }

    private static Version Parse(string text)
    {
        var cleaned = new string(text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        return Version.TryParse(cleaned, out var version) ? version : new Version(0, 0);
    }
}
