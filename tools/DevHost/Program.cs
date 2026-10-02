using System.Net;
using Taildrop.Core;

// Usage: dotnet run --project tools/DevHost -- [--port 8787] [--inbox <folder>]
var port = 8787;
var inbox = Path.Combine(Path.GetTempPath(), "Taildrop-dev-" + Guid.NewGuid().ToString("N"));
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port") port = int.Parse(args[i + 1]);
    if (args[i] == "--inbox") inbox = Path.GetFullPath(args[i + 1]);
}
Directory.CreateDirectory(inbox);

await using var server = new TaildropServer(inbox);
await server.StartAsync(IPAddress.Loopback, port);
Console.WriteLine($"Taildrop dev host: http://127.0.0.1:{port}");
Console.WriteLine($"Inbox: {inbox}");
Console.WriteLine("Press Ctrl+C to stop.");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;
await server.StopAsync();
