using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Taildrop.Core;

/// <summary>What this computer looks like on the tailnet.</summary>
/// <param name="DnsName">MagicDNS name, e.g. computer.tail1234.ts.net (no trailing dot).</param>
/// <param name="IPv4">Tailscale IPv4 address (100.x.y.z).</param>
/// <param name="HttpsEnabled">The tailnet has HTTPS certificates turned on, so <c>tailscale serve</c> can give this name a real certificate.</param>
public sealed record TailscaleStatus(string? DnsName, string? IPv4, bool HttpsEnabled);

/// <summary>The Tailscale command-line tool.</summary>
public static class Tailscale
{
    public static string? FindExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "tailscale.exe" : "tailscale";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                if (directory.Length == 0) continue;
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }

        var fallbacks = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", name),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tailscale", name)
            }
            : new[] { "/Applications/Tailscale.app/Contents/MacOS/Tailscale", "/usr/bin/tailscale", "/usr/local/bin/tailscale" };
        return fallbacks.FirstOrDefault(File.Exists);
    }

    /// <summary><c>tailscale status --json</c>, or null when Tailscale isn't running or logged in.</summary>
    public static async Task<TailscaleStatus?> GetStatusAsync(string executable, CancellationToken cancellationToken = default)
    {
        try
        {
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("status");
            start.ArgumentList.Add("--json");
            using var process = Process.Start(start);
            if (process is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return ParseStatus(output);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static TailscaleStatus? ParseStatus(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("BackendState", out var state) && state.GetString() != "Running") return null;
            if (!root.TryGetProperty("Self", out var self)) return null;
            var dns = self.TryGetProperty("DNSName", out var name) ? name.GetString()?.TrimEnd('.') : null;
            string? ipv4 = null;
            if (self.TryGetProperty("TailscaleIPs", out var addresses) && addresses.ValueKind == JsonValueKind.Array)
                ipv4 = addresses.EnumerateArray().Select(a => a.GetString()).FirstOrDefault(a => a is not null && a.Contains('.') && !a.Contains(':'));
            var https = root.TryGetProperty("CertDomains", out var domains) && domains.ValueKind == JsonValueKind.Array && domains.GetArrayLength() > 0;
            return new TailscaleStatus(string.IsNullOrEmpty(dns) ? null : dns, ipv4, https && !string.IsNullOrEmpty(dns));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Publishes a local server at https://&lt;this computer&gt;.ts.net, tailnet only, with a real certificate, by running
/// <c>tailscale serve</c> in the foreground for as long as this object lives. Tailscale removes a foreground serve
/// the moment its process ends, and on Windows that process is tied to ours (a kill-on-close job), so nothing is
/// left behind even if Taildrop crashes. Phones need HTTPS for the live camera; the receiver itself stays on
/// 127.0.0.1 and is never reachable any other way.
/// </summary>
public sealed class TailscaleServe : IAsyncDisposable
{
    /// <summary>The ports <c>tailscale serve</c> accepts for HTTPS, cleanest URL first.</summary>
    static readonly int[] Ports = { 443, 8443, 10000 };
    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    readonly Process _process;
    bool _disposed;

    /// <summary>The address phones open, e.g. https://computer.tail1234.ts.net/</summary>
    public string Url { get; }

    /// <summary>Raised (on a background thread) if <c>tailscale serve</c> stops by itself.</summary>
    public event Action? Stopped;

    TailscaleServe(Process process, string url)
    {
        _process = process;
        Url = url;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => { if (!_disposed) Stopped?.Invoke(); };
    }

    /// <summary>Serves http://127.0.0.1:<paramref name="localPort"/> over HTTPS on the first free serve port, or returns null.</summary>
    public static async Task<TailscaleServe?> StartAsync(string executable, string dnsName, int localPort, CancellationToken cancellationToken = default)
    {
        foreach (var port in Ports)
        {
            var serve = await TryStartAsync(executable, dnsName, port, localPort, cancellationToken);
            if (serve is not null) return serve;
        }
        return null;
    }

    static async Task<TailscaleServe?> TryStartAsync(string executable, string dnsName, int port, int localPort, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("serve");
        start.ArgumentList.Add($"--https={port}");
        start.ArgumentList.Add($"http://127.0.0.1:{localPort}");

        Process? process;
        try { process = Process.Start(start); }
        catch { return null; }
        if (process is null) return null;
        ChildProcessGuard.Attach(process);

        // Ready once it prints the address it serves; a prompt to enable HTTPS or Serve (it would wait for an
        // answer forever), an error, or an early exit means this port, or serve altogether, is unavailable.
        var outcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Watch(string? line)
        {
            if (line is null) return;
            if (line.Contains("https://", StringComparison.OrdinalIgnoreCase) && line.Contains(dnsName, StringComparison.OrdinalIgnoreCase)) outcome.TrySetResult(true);
            else if (line.Contains("login.tailscale.com", StringComparison.OrdinalIgnoreCase) || line.Contains("error", StringComparison.OrdinalIgnoreCase)) outcome.TrySetResult(false);
        }
        process.OutputDataReceived += (_, e) => Watch(e.Data);
        process.ErrorDataReceived += (_, e) => Watch(e.Data);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => outcome.TrySetResult(false);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (process.HasExited) outcome.TrySetResult(false);

        bool ready;
        try { ready = await outcome.Task.WaitAsync(StartTimeout, cancellationToken); }
        catch (TimeoutException) { ready = false; }
        catch (OperationCanceledException) { Kill(process); throw; }

        if (!ready || process.HasExited)
        {
            Kill(process);
            return null;
        }
        return new TailscaleServe(process, port == 443 ? $"https://{dnsName}/" : $"https://{dnsName}:{port}/");
    }

    /// <summary>
    /// Asks for the page once, so Tailscale fetches the certificate now (that takes up to half a minute the first
    /// time) instead of while a phone waits. True once the secure link answers.
    /// </summary>
    public async Task<bool> WarmUpAsync(CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        try
        {
            using var response = await http.GetAsync(Url + "api/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        process.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        Kill(_process);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Windows: child processes put in this job die with Taildrop, however Taildrop ends (closed, killed, crashed).
/// Elsewhere (the development host) children are stopped on a normal exit only.
/// </summary>
static class ChildProcessGuard
{
    static readonly Lazy<IntPtr> Job = new(CreateJob);

    public static void Attach(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var job = Job.Value;
            if (job != IntPtr.Zero) AssignProcessToJobObject(job, process.Handle);
        }
        catch { /* best effort: Dispose still stops it on a normal exit */ }
    }

    static IntPtr CreateJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE } };
        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, pointer, (uint)length)) return IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
        return job;   // deliberately never closed: the handle closes when Taildrop ends, and that ends the children
    }

    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
