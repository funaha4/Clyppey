using System.Diagnostics;
using System.Globalization;

namespace Clyppey.Core.Monitoring;

/// <summary>
/// Measures the real cost of recording: the encoder process's own CPU share and the GPU
/// encoder utilisation reported by nvidia-smi. These numbers are what the UI shows so the
/// "it does not cost you FPS" claim is backed by measurement, not assertion.
/// </summary>
public sealed class PerfSampler : IDisposable
{
    private readonly int _processId;
    private readonly int _coreCount = Environment.ProcessorCount;
    private readonly Timer _timer;
    private TimeSpan _lastCpu;
    private double _lastSampleClock;
    private string? _nvidiaSmi;
    private bool _nvidiaSmiResolved;
    private int _nvidiaFailures;
    private bool _disposed;

    public PerfSampler(int processId)
    {
        _processId = processId;
        _timer = new Timer(_ => Sample(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public double CpuPercent { get; private set; }

    public double GpuEncoderPercent { get; private set; } = -1;

    public double GpuPercent { get; private set; } = -1;

    public string GpuName { get; private set; } = string.Empty;

    public void Start()
    {
        _lastSampleClock = Clock.Now;
        _timer.Change(500, 2000);
    }

    private void Sample()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            SampleCpu();
            SampleGpu();
        }
        catch (Exception ex)
        {
            Log.Debug($"パフォーマンス計測エラー: {ex.Message}");
        }
    }

    private void SampleCpu()
    {
        Process process;
        try
        {
            process = Process.GetProcessById(_processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            TimeSpan cpu;
            try
            {
                cpu = process.TotalProcessorTime;
            }
            catch (Exception)
            {
                return;
            }

            var now = Clock.Now;
            var elapsed = now - _lastSampleClock;
            if (elapsed > 0.05)
            {
                var delta = (cpu - _lastCpu).TotalSeconds;
                CpuPercent = Math.Clamp(delta / elapsed / _coreCount * 100.0, 0, 100);
                _lastCpu = cpu;
                _lastSampleClock = now;
            }
        }
    }

    private void SampleGpu()
    {
        if (_nvidiaFailures > 3)
        {
            return;
        }

        if (!_nvidiaSmiResolved)
        {
            _nvidiaSmiResolved = true;
            _nvidiaSmi = ResolveNvidiaSmi();
        }

        if (string.IsNullOrEmpty(_nvidiaSmi))
        {
            _nvidiaFailures = 99;
            return;
        }

        var output = RunQuery(_nvidiaSmi,
            "--query-gpu=utilization.encoder,utilization.gpu,name", "--format=csv,noheader,nounits");
        if (output is null)
        {
            _nvidiaFailures++;
            return;
        }

        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(line))
        {
            _nvidiaFailures++;
            return;
        }

        var parts = line.Split(',');
        if (parts.Length >= 3)
        {
            GpuEncoderPercent = Parse(parts[0]);
            GpuPercent = Parse(parts[1]);
            GpuName = parts[2].Trim();
        }
    }

    private static double Parse(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : -1;

    private static string? ResolveNvidiaSmi()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        };

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.Combine(p.Trim().Trim('"'), "nvidia-smi.exe")));

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed path.
            }
        }

        return null;
    }

    private static string? RunQuery(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(4000))
            {
                try { process.Kill(); } catch (Exception) { }
                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
