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
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
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
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                break;
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
        var directory = Path.Combine(AppPaths.AppDataRoot, "update", update.Version);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "Clyppey.zip");

        using var response = await Http
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

    /// <summary>Extracts the package and returns the folder that holds the new executable.</summary>
    public static string Extract(string zipPath)
    {
        var directory = Path.Combine(Path.GetDirectoryName(zipPath)!, "files");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        ZipFile.ExtractToDirectory(zipPath, directory);

        // The archive keeps everything under one top-level folder.
        var inner = Directory.GetDirectories(directory);
        return inner.Length == 1 && File.Exists(Path.Combine(inner[0], "Clyppey.exe"))
            ? inner[0]
            : directory;
    }

    /// <summary>
    /// Writes the swap script and starts it. The caller must quit right after this returns.
    /// </summary>
    public static void StartSwap(string sourceDirectory, string installDirectory, int processId)
    {
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

    private static Version Parse(string text)
    {
        var cleaned = new string(text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(cleaned, out var version) ? version : new Version(0, 0);
    }
}
