using System.Net;
using Taildrop.Core;

// Usage: dotnet run --project tools/DevHost -- [--port 8787] [--inbox <folder>] [--serve]
//   --serve  also publish it at https://<this computer>.ts.net with `tailscale serve`, like the desktop app does,
//            so a phone on the tailnet can use the live camera (which needs HTTPS). In a desktop browser,
//            http://127.0.0.1 already counts as secure.
var port = 8787;
var inbox = Path.Combine(Path.GetTempPath(), "Taildrop-dev-" + Guid.NewGuid().ToString("N"));
var serve = args.Contains("--serve");
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port") port = int.Parse(args[i + 1]);
    if (args[i] == "--inbox") inbox = Path.GetFullPath(args[i + 1]);
}
Directory.CreateDirectory(inbox);

await using var server = new TaildropServer(inbox);
await server.StartAsync(IPAddress.Loopback, port);
Console.WriteLine($"Taildrop dev host: http://127.0.0.1:{server.Port}");
Console.WriteLine($"Inbox: {inbox}");

TailscaleServe? published = null;
if (serve)
{
    var tailscale = Tailscale.FindExecutable();
    var status = tailscale is null ? null : await Tailscale.GetStatusAsync(tailscale);
    if (tailscale is null || status is null) Console.WriteLine("--serve: Tailscale isn't running on this computer.");
    else if (!status.HttpsEnabled || status.DnsName is null) Console.WriteLine("--serve: turn on HTTPS certificates for the tailnet (Tailscale admin console, DNS page) first.");
    else if ((published = await TailscaleServe.StartAsync(tailscale, status.DnsName, server.Port)) is null) Console.WriteLine("--serve: `tailscale serve` didn't start (ports 443, 8443 and 10000 all busy?).");
    else
    {
        server.PublicUrl = published.Url;
        Console.WriteLine($"Secure link for phones: {published.Url}");
    }
}
Console.WriteLine("Press Ctrl+C to stop.");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;
if (published is not null) await published.DisposeAsync();
await server.StopAsync();
