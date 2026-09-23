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

namespace TaildropApp;

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

sealed class TaildropServer : IAsyncDisposable
{
    const long MaxFileSize = 5L * 1024 * 1024 * 1024; // 5 GB
    static readonly Regex InvalidChars = new(@"[<>:""/\\|?*\u0000-\u001F]", RegexOptions.Compiled);
    static readonly Regex DotRun = new(@"\.\.+", RegexOptions.Compiled);
    static readonly Regex TrailingDotsSpaces = new(@"[. ]+$", RegexOptions.Compiled);

    readonly string _inboxDir;
    readonly HashSet<string> _reservedNames = new(StringComparer.Ordinal);
    readonly HashSet<string> _activeParts = new(StringComparer.Ordinal);
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
        _app.Use(HandleRequestAsync);

        await _app.StartAsync();
    }

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
        SetSecurityHeaders(res);
        try
        {
            if (req.Method == "GET" && req.Path == "/api/health")
            {
                await JsonAsync(res, 200, new { ready = true });
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
        var address = $"http://{(string.IsNullOrEmpty(hostHeader) ? req.Host.ToString() : hostHeader)}";
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
            _reservedNames.Remove(name);
            await JsonAsync(res, 400, new { error = "Invalid file name" });
            return;
        }

        var tempPath = Path.Combine(_inboxDir, $".taildrop-{Guid.NewGuid():N}.part");
        _activeParts.Add(tempPath);
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
            _activeParts.Remove(tempPath);
            await JsonAsync(res, 201, new { name, size = received });
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
            _activeParts.Remove(tempPath);
            throw;
        }
        finally
        {
            _reservedNames.Remove(name);
        }
    }

    async Task ServeStaticAsync(HttpContext context)
    {
        var req = context.Request;
        var res = context.Response;
        var requested = req.Path == "/" ? "index.html" : req.Path.ToString().TrimStart('/');
        if (!StaticAssets.TryGet(requested, out var bytes, out var contentType))
        {
            await JsonAsync(res, 404, new { error = "Not found" });
            return;
        }
        res.StatusCode = 200;
        res.ContentType = contentType;
        res.ContentLength = bytes.Length;
        res.Headers.CacheControl = "no-cache";
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

    static void SetSecurityHeaders(HttpResponse res)
    {
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.Headers["X-Frame-Options"] = "DENY";
        res.Headers["Referrer-Policy"] = "no-referrer";
        res.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'";
    }

    static async Task JsonAsync(HttpResponse res, int status, object body)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(body);
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
