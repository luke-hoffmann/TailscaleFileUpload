using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Taildrop.Core.Scanning;

namespace Taildrop.Core;

sealed class UploadException : Exception
{
    public int StatusCode { get; }
    public string PublicMessage { get; }

    public UploadException(int statusCode, string publicMessage) : base(publicMessage)
    {
        StatusCode = statusCode;
        PublicMessage = publicMessage;
    }
}

public sealed partial class TaildropServer : IAsyncDisposable
{
    const long MaxFileSize = 5L * 1024 * 1024 * 1024; // 5 GB
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    static readonly Regex InvalidChars = new(@"[<>:""/\\|?*\u0000-\u001F]", RegexOptions.Compiled);
    static readonly Regex DotRun = new(@"\.\.+", RegexOptions.Compiled);
    static readonly Regex TrailingDotsSpaces = new(@"[. ]+$", RegexOptions.Compiled);

    readonly string _inboxDir;
    readonly HashSet<string> _reservedNames = new(StringComparer.Ordinal);
    readonly SemaphoreSlim _reservationLock = new(1, 1);
    WebApplication? _app;

    public TaildropServer(string inboxDir)
    {
        _inboxDir = inboxDir;
    }

    public async Task StartAsync(IPAddress address, int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(address, port);
            options.Limits.MaxRequestBodySize = null;
        });

        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        _app.Use(HandleRequestAsync);

        await _app.StartAsync();
        var bound = _app.Urls.Select(url => new Uri(url.Replace("*", "localhost").Replace("+", "localhost"))).FirstOrDefault();
        Port = bound?.Port ?? port;
    }

    /// <summary>The port the server listens on (useful after starting on port 0, "any free port").</summary>
    public int Port { get; private set; }

    /// <summary>
    /// The address phones open when it differs from where the server listens: behind <c>tailscale serve</c>,
    /// https://computer.tail1234.ts.net/. Pages from there may open the live scanner's WebSocket.
    /// </summary>
    public string? PublicUrl { get; set; }

    public async Task StopAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    async Task HandleRequestAsync(HttpContext context, Func<Task> next)
    {
        var req = context.Request;
        var res = context.Response;
        SetSecurityHeaders(req, res);
        try
        {
            if (req.Method == "GET" && req.Path == "/api/health")
            {
                await JsonAsync(res, 200, new { ready = true, scan = ScanAvailable });
                return;
            }
            if (req.Method == "GET" && req.Path == "/api/qr")
            {
                await GenerateQrAsync(req, res);
                return;
            }
            if (req.Method == "POST" && req.Path == "/api/upload")
            {
                await UploadAsync(context);
                return;
            }
            if (req.Path.StartsWithSegments("/api/scan", out var scanRest))
            {
                await ScanAsync(context, scanRest.Value ?? "");
                return;
            }
            if (req.Path == "/api/live")
            {
                await LiveAsync(context);
                return;
            }
            if (req.Path.StartsWithSegments("/api"))
            {
                await JsonAsync(res, 404, new { error = "Not found" });
                return;
            }
            if (req.Method != "GET" && req.Method != "HEAD")
            {
                await JsonAsync(res, 405, new { error = "Method not allowed" });
                return;
            }
            await ServeStaticAsync(context);
        }
        catch (UploadException error)
        {
            if (!res.HasStarted) await JsonAsync(res, error.StatusCode, new { error = error.PublicMessage });
            else context.Abort();
        }
        catch (ScanException error)
        {
            if (!res.HasStarted) await JsonAsync(res, error.StatusCode, new { error = error.Message });
            else context.Abort();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine(error);
            if (!res.HasStarted) await JsonAsync(res, 500, new { error = "Upload failed" });
            else context.Abort();
        }
    }

    async Task GenerateQrAsync(HttpRequest req, HttpResponse res)
    {
        var hostHeader = req.Headers.Host.ToString();
        var scheme = req.Headers["X-Forwarded-Proto"].ToString() == "https" ? "https" : "http";
        var address = $"{scheme}://{(string.IsNullOrEmpty(hostHeader) ? req.Host.ToString() : hostHeader)}";
        var qr = QrCode.GenerateDataUrl(address);
        await JsonAsync(res, 200, new { qr, url = address });
    }

    async Task UploadAsync(HttpContext context)
    {
        var req = context.Request;
        var res = context.Response;

        var rawName = DecodeHeader(req.Headers["X-File-Name"]);
        if (string.IsNullOrEmpty(rawName))
        {
            await JsonAsync(res, 400, new { error = "Missing file name" });
            return;
        }

        var length = req.ContentLength;
        if (length is not null && length < 0)
        {
            await JsonAsync(res, 400, new { error = "Invalid file size" });
            return;
        }
        if (length is not null && length > MaxFileSize)
        {
            await JsonAsync(res, 413, new { error = $"File exceeds {FormatBytes(MaxFileSize)} limit" });
            return;
        }

        var name = await ReserveAvailableNameAsync(SanitizeName(rawName));
        string finalPath;
        try
        {
            finalPath = SafeInboxPath(name);
        }
        catch
        {
            await ReleaseNameAsync(name);
            await JsonAsync(res, 400, new { error = "Invalid file name" });
            return;
        }

        var tempPath = Path.Combine(_inboxDir, $".taildrop-{Guid.NewGuid():N}.part");
        long received = 0;
        try
        {
            await using (var fileStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await req.Body.ReadAsync(buffer, 0, buffer.Length, context.RequestAborted)) > 0)
                {
                    received += read;
                    if (received > MaxFileSize)
                        throw new UploadException(413, $"File exceeds {FormatBytes(MaxFileSize)} limit");
                    await fileStream.WriteAsync(buffer, 0, read, context.RequestAborted);
                }
                await fileStream.FlushAsync(context.RequestAborted);
            }
            File.Move(tempPath, finalPath);
            await JsonAsync(res, 201, new { name, size = received });
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
        finally
        {
            await ReleaseNameAsync(name);
        }
    }

    async Task ServeStaticAsync(HttpContext context)
    {
        var req = context.Request;
        var res = context.Response;
        var requested = req.Path == "/" ? "index.html" : req.Path.ToString().TrimStart('/');
        if (!StaticAssets.TryGet(requested, out var bytes, out var contentType, out var etag))
        {
            await JsonAsync(res, 404, new { error = "Not found" });
            return;
        }
        res.ContentType = contentType;
        res.Headers.CacheControl = "no-cache";
        res.Headers.ETag = etag;
        if (req.Headers.IfNoneMatch.ToString().Contains(etag, StringComparison.Ordinal))
        {
            res.StatusCode = 304;
            return;
        }
        res.StatusCode = 200;
        res.ContentLength = bytes.Length;
        if (req.Method == "HEAD") return;
        await res.Body.WriteAsync(bytes, context.RequestAborted);
    }

    string SanitizeName(string name)
    {
        var cleaned = InvalidChars.Replace(name, "_");
        cleaned = DotRun.Replace(cleaned, "_");
        cleaned = TrailingDotsSpaces.Replace(cleaned, "");
        cleaned = cleaned.Trim();
        if (cleaned.Length > 180) cleaned = cleaned[..180];
        return !string.IsNullOrEmpty(cleaned) && cleaned != "." && cleaned != ".."
            ? cleaned
            : $"upload-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    string SafeInboxPath(string name)
    {
        if (name != SanitizeName(name)) throw new UploadException(400, "Invalid file name");
        var path = Path.GetFullPath(Path.Combine(_inboxDir, name));
        var inboxWithSep = _inboxDir + Path.DirectorySeparatorChar;
        if (!path.StartsWith(inboxWithSep, StringComparison.Ordinal)) throw new UploadException(400, "Invalid file name");
        return path;
    }

    async Task<string> ReserveAvailableNameAsync(string name)
    {
        await _reservationLock.WaitAsync();
        try
        {
            return await AvailableNameAsync(name);
        }
        finally
        {
            _reservationLock.Release();
        }
    }

    async Task ReleaseNameAsync(string name)
    {
        await _reservationLock.WaitAsync();
        try { _reservedNames.Remove(name); }
        finally { _reservationLock.Release(); }
    }

    Task<string> AvailableNameAsync(string name)
    {
        var extension = Path.GetExtension(name);
        var stem = name[..(name.Length - extension.Length)];
        for (var i = 0; i < 10000; i++)
        {
            var candidate = i == 0 ? name : $"{stem} ({i}){extension}";
            if (_reservedNames.Contains(candidate)) continue;
            if (!File.Exists(Path.Combine(_inboxDir, candidate)) && !Directory.Exists(Path.Combine(_inboxDir, candidate)))
            {
                _reservedNames.Add(candidate);
                return Task.FromResult(candidate);
            }
        }
        var fallback = $"{stem}-{Guid.NewGuid():N}{extension}";
        _reservedNames.Add(fallback);
        return Task.FromResult(fallback);
    }

    static readonly Regex HostPattern = new(@"^[A-Za-z0-9.\-]+(:\d{1,5})?$", RegexOptions.Compiled);

    void SetSecurityHeaders(HttpRequest req, HttpResponse res)
    {
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.Headers["X-Frame-Options"] = "DENY";
        res.Headers["Referrer-Policy"] = "no-referrer";
        // The live scanner's WebSocket is same-host, but older Safari doesn't count ws:/wss: as 'self'; name it.
        var hosts = new[] { req.Headers.Host.ToString(), PublicUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : "" };
        var socket = string.Concat(hosts.Where(host => HostPattern.IsMatch(host)).Distinct().Select(host => $" ws://{host} wss://{host}"));
        res.Headers["Content-Security-Policy"] = $"default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'{socket}; media-src 'self' blob:";
        // The camera is for this page only (it is off for any other origin by default; say so explicitly).
        res.Headers["Permissions-Policy"] = "camera=(self), microphone=()";
    }

    static async Task JsonAsync(HttpResponse res, int status, object body)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength = data.Length;
        res.Headers.CacheControl = "no-store";
        await res.Body.WriteAsync(data);
    }

    static string DecodeHeader(Microsoft.Extensions.Primitives.StringValues value)
    {
        if (value.Count != 1 || value[0] is null) return "";
        try { return Uri.UnescapeDataString(value[0]!); }
        catch { return ""; }
    }

    static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = Math.Min((int)Math.Floor(Math.Log(bytes) / Math.Log(1024)), units.Length - 1);
        var value = bytes / Math.Pow(1024, i);
        return i == 0 ? $"{value:F0} {units[i]}" : $"{value:F1} {units[i]}";
    }
}
