using System.Diagnostics;
using Xunit;

namespace Taildrop.Core.Tests;

/// <summary>Reading `tailscale status`, and driving `tailscale serve` (against a stand-in CLI script).</summary>
public sealed class TailscaleTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "Taildrop-tailscale-" + Guid.NewGuid().ToString("N"));

    public TailscaleTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Status_TellsWhetherHttpsCertificatesAreOn()
    {
        const string withHttps = """
            { "BackendState": "Running", "CertDomains": ["box.tail1234.ts.net"],
              "Self": { "DNSName": "box.tail1234.ts.net.", "TailscaleIPs": ["100.101.102.103", "fd7a:115c:a1e0::1"] } }
            """;
        var status = Tailscale.ParseStatus(withHttps)!;
        Assert.Equal("box.tail1234.ts.net", status.DnsName);
        Assert.Equal("100.101.102.103", status.IPv4);
        Assert.True(status.HttpsEnabled);

        var withoutHttps = Tailscale.ParseStatus("""{ "BackendState": "Running", "CertDomains": null, "Self": { "DNSName": "box.tail1234.ts.net.", "TailscaleIPs": ["100.1.2.3"] } }""")!;
        Assert.False(withoutHttps.HttpsEnabled);
        Assert.Equal("100.1.2.3", withoutHttps.IPv4);

        Assert.Null(Tailscale.ParseStatus("""{ "BackendState": "NeedsLogin", "Self": {} }"""));
        Assert.Null(Tailscale.ParseStatus("not json"));
    }

    [Fact]
    public async Task Serve_TakesTheFirstFreePort_AndStopsWithUs()
    {
        if (OperatingSystem.IsWindows()) return;
        var cli = Script("busy-443", """
            if [ "$2" = "--https=443" ]; then echo "error: listener already exists for port 443" >&2; exit 1; fi
            port="${2#--https=}"
            echo "Available within your tailnet:"; echo; echo "https://box.tail1234.ts.net:$port/"; echo "|-- / proxy $3"; echo; echo "Press Ctrl+C to exit."
            exec sleep 600
            """);
        var serve = await TailscaleServe.StartAsync(cli, "box.tail1234.ts.net", 5555);
        Assert.NotNull(serve);
        Assert.Equal("https://box.tail1234.ts.net:8443/", serve!.Url);
        var stopped = false;
        serve.Stopped += () => stopped = true;
        Assert.Single(Sleepers(cli));
        await serve.DisposeAsync();
        await WaitUntil(() => Sleepers(cli).Count == 0);
        await Task.Delay(200);
        Assert.False(stopped);   // a stop we asked for is not news
    }

    [Fact]
    public async Task Serve_OnTheDefaultPort_HasACleanUrl()
    {
        if (OperatingSystem.IsWindows()) return;
        var cli = Script("ok", """
            echo "Available within your tailnet:"; echo "https://box.tail1234.ts.net/"; exec sleep 600
            """);
        await using var serve = await TailscaleServe.StartAsync(cli, "box.tail1234.ts.net", 5555);
        Assert.Equal("https://box.tail1234.ts.net/", serve!.Url);
    }

    [Fact]
    public async Task Serve_NotAllowedOnTheTailnet_GivesUpQuickly()
    {
        if (OperatingSystem.IsWindows()) return;
        // Tailscale asks for approval in a browser and would wait forever.
        var cli = Script("prompt", """
            echo "Serve is not enabled on your tailnet."; echo "To enable, visit:"; echo "  https://login.tailscale.com/f/serve?node=abc123"; exec sleep 600
            """);
        var clock = Stopwatch.StartNew();
        Assert.Null(await TailscaleServe.StartAsync(cli, "box.tail1234.ts.net", 5555));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        await WaitUntil(() => Sleepers(cli).Count == 0);
    }

    [Fact]
    public async Task Serve_EndingByItself_IsReported()
    {
        if (OperatingSystem.IsWindows()) return;
        var cli = Script("dies", """
            echo "https://box.tail1234.ts.net/"; sleep 1; exit 0
            """);
        await using var serve = await TailscaleServe.StartAsync(cli, "box.tail1234.ts.net", 5555);
        var stopped = new TaskCompletionSource();
        serve!.Stopped += () => stopped.TrySetResult();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    readonly Dictionary<string, string> _sleeps = new();

    /// <summary>A stand-in `tailscale`. Its "exec sleep 600" becomes a sleep of a length unique to it, so its process can be found.</summary>
    string Script(string name, string body)
    {
        var path = Path.Combine(_folder, name);
        var seconds = (600 + Random.Shared.Next(1, 99999)).ToString();
        _sleeps[path] = seconds;
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r", "").Replace("sleep 600", "sleep " + seconds) + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    List<int> Sleepers(string script)
    {
        var wanted = "sleep\0" + _sleeps[script] + "\0";
        var found = new List<int>();
        foreach (var process in Process.GetProcessesByName("sleep"))
        {
            try { if (File.ReadAllText($"/proc/{process.Id}/cmdline") == wanted) found.Add(process.Id); }
            catch { /* already gone */ }
        }
        return found;
    }

    static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(50);
        }
    }
}
