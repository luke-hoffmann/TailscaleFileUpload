using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenCvSharp;
using Taildrop.Core.Scanning;

namespace Taildrop.Core;

/// <summary>
/// Live scanning over a WebSocket at /api/live. The phone shows its own camera preview; the PC only answers where
/// the page is, then builds a high-resolution page out of close-ups (<see cref="DetailCanvas"/>). Every frame gets
/// exactly one reply and the phone keeps at most a frame or two in flight, so nothing ever queues up and every
/// answer is about a fresh frame.
///
/// Phone to PC, binary: [header length, u16 little-endian][JSON header, UTF-8][JPEG]
///   {"type":"aim","seq":1}                                   small frame: where is the page?
///   {"type":"start","seq":2,"filter":"auto","corners":[...]} full-resolution frame of the whole page
///   {"type":"detail","seq":3}                                full-resolution close-up
/// Phone to PC, text: {"type":"finish"} | {"type":"cancel"} | {"type":"forget"} | {"type":"ping"}
/// PC to phone, text: ready, aim, busy, started, detail, finishing, done, error (see the Send calls below).
///
/// A browser cannot add the X-Taildrop header to a WebSocket, so the handshake's Origin must be this server.
/// </summary>
public sealed partial class TaildropServer
{
    const int MaxLiveConnections = 4;
    const int MaxAimFrame = 2 * 1024 * 1024;
    const int MaxFullFrame = 24 * 1024 * 1024;
    static readonly TimeSpan LiveIdleTimeout = TimeSpan.FromSeconds(60);
    /// <summary>Phone-side detail maps are refreshed at most this often (they are a few dozen KB each).</summary>
    static readonly TimeSpan MapInterval = TimeSpan.FromSeconds(1.2);
    /// <summary>Live frames are CPU work: a couple at a time, so they never starve a final render.</summary>
    static readonly SemaphoreSlim LiveGate = new(2, 2);

    int _liveConnections;

    sealed class LiveHeader
    {
        public string? Type { get; set; }
        public long Seq { get; set; }
        public string? Filter { get; set; }
        public double[][]? Corners { get; set; }
    }

    /// <summary>One phone's live scanner: the page being built (if any) and the last page finished.</summary>
    sealed class LiveSession : IDisposable
    {
        public DetailCanvas? Canvas;
        public Mat? Reference;
        public ScanOutline? Outline;
        public ScanFilter Filter;
        public float[]? LastPage;
        public DateTime LastMap;

        public void EndPage()
        {
            Canvas?.Dispose();
            Reference?.Dispose();
            Canvas = null;
            Reference = null;
            Outline = null;
        }

        public void Dispose() => EndPage();
    }

    async Task LiveAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) throw new UploadException(400, "Expected a WebSocket");
        if (!ScanAvailable) throw new UploadException(503, "Scanning isn't available on this computer. You can still send photos and files.");
        if (!OriginAllowed(context.Request)) throw new UploadException(403, "Not allowed");
        if (Interlocked.Increment(ref _liveConnections) > MaxLiveConnections)
        {
            Interlocked.Decrement(ref _liveConnections);
            throw new UploadException(503, "Too many live scanners are open");
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var session = new LiveSession();
            var stopping = context.RequestAborted;
            await SendAsync(socket, new { type = "ready", target = DetailCanvas.DefaultTargetLongEdge }, stopping);

            while (socket.State == WebSocketState.Open)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                idle.CancelAfter(LiveIdleTimeout);
                (WebSocketMessageType Type, byte[] Data)? message;
                try { message = await ReceiveAsync(socket, MaxFullFrame, idle.Token); }
                catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
                {
                    await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "Idle", stopping);
                    break;
                }
                catch (WebSocketException) { break; }
                if (message is null) break;

                try
                {
                    if (message.Value.Type == WebSocketMessageType.Text)
                        await HandleLiveCommandAsync(socket, session, message.Value.Data, stopping);
                    else
                        await HandleLiveFrameAsync(socket, session, message.Value.Data, stopping);
                }
                catch (LiveFrameException error)
                {
                    await SendAsync(socket, new { type = "error", seq = error.Seq, message = error.Message }, stopping);
                }
                catch (ScanException error)
                {
                    await SendAsync(socket, new { type = "error", message = error.Message }, stopping);
                }
                catch (UploadException error)
                {
                    await SendAsync(socket, new { type = "error", message = error.PublicMessage }, stopping);
                }
            }
        }
        catch (WebSocketException)
        {
            // The phone went away (locked, out of range); it reconnects by itself.
        }
        finally
        {
            Interlocked.Decrement(ref _liveConnections);
        }
    }

    sealed class LiveFrameException : Exception
    {
        public long Seq { get; }
        public LiveFrameException(long seq, string message) : base(message) => Seq = seq;
    }

    async Task HandleLiveCommandAsync(WebSocket socket, LiveSession session, byte[] data, CancellationToken ct)
    {
        LiveHeader? command;
        try { command = JsonSerializer.Deserialize<LiveHeader>(data, JsonOptions); }
        catch (JsonException) { throw new UploadException(400, "That message isn't valid"); }

        switch (command?.Type)
        {
            case "finish" when session.Canvas is not null:
                await FinishLivePageAsync(socket, session, ct);
                break;
            case "cancel":
                session.EndPage();
                break;
            case "forget":
                session.LastPage = null;   // the last page was retaken: the same page may be scanned again
                break;
            case "ping":
            case "finish":
                break;
            default:
                throw new UploadException(400, "Unknown message");
        }
    }

    async Task HandleLiveFrameAsync(WebSocket socket, LiveSession session, byte[] data, CancellationToken ct)
    {
        if (data.Length < 2) throw new UploadException(400, "That frame is empty");
        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (headerLength == 0 || 2 + headerLength > data.Length) throw new UploadException(400, "That frame isn't valid");
        LiveHeader? header;
        try { header = JsonSerializer.Deserialize<LiveHeader>(data.AsSpan(2, headerLength), JsonOptions); }
        catch (JsonException) { throw new UploadException(400, "That frame isn't valid"); }
        if (header?.Type is null) throw new UploadException(400, "That frame isn't valid");
        var jpeg = data.AsMemory(2 + headerLength);

        switch (header.Type)
        {
            case "aim":
                await AimAsync(socket, session, header.Seq, jpeg, ct);
                break;
            case "start":
                await StartLivePageAsync(socket, session, header, jpeg, ct);
                break;
            case "detail":
                await AddDetailAsync(socket, session, header.Seq, jpeg, ct);
                break;
            default:
                throw new UploadException(400, "Unknown frame");
        }
    }

    /// <summary>Where is the page in this small frame, and is it the page that was just scanned?</summary>
    async Task AimAsync(WebSocket socket, LiveSession session, long seq, ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        if (jpeg.Length > MaxAimFrame) throw new LiveFrameException(seq, "That frame is too large");
        // Busy building a page, or the PC is busy: drop the frame rather than queue it. The phone sends the next.
        if (session.Canvas is not null || !LiveGate.Wait(0))
        {
            await SendAsync(socket, new { type = "busy", seq }, ct);
            return;
        }

        object reply;
        try
        {
            reply = await Task.Run(() =>
            {
                using var frame = DecodeFrame(jpeg, seq);
                var found = DocumentDetector.DetectQuick(frame);
                if (found is null) return (object)new { type = "aim", seq, page = (object?)null, same = false };
                var same = session.LastPage is not null && PageFingerprint.Same(session.LastPage, PageFingerprint.FromFrame(frame, found.Value.Corners));
                var corners = found.Value.Corners.Select(c => new[] { Math.Round(c[0], 4), Math.Round(c[1], 4) }).ToArray();
                return new { type = "aim", seq, page = (object?)new { corners, confident = found.Value.Confident }, same };
            }, ct);
        }
        finally
        {
            LiveGate.Release();
        }
        await SendAsync(socket, reply, ct);
    }

    /// <summary>A full-resolution frame of the whole page: find its outline and start building the page.</summary>
    async Task StartLivePageAsync(WebSocket socket, LiveSession session, LiveHeader header, ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        var seq = header.Seq;
        session.EndPage();
        var filter = ParseFilter(header.Filter, ScanFilter.Auto);

        await LiveGate.WaitAsync(ct);
        try
        {
            var frame = DecodeFrame(jpeg, seq);
            try
            {
                var outline = await Task.Run(() => ChooseOutline(frame, header.Corners), ct)
                    ?? throw new LiveFrameException(seq, "Couldn't find the page. Point the camera at it and try again.");
                session.Canvas = await Task.Run(() => DetailCanvas.Start(frame, outline), ct);
                session.Reference = frame;
                session.Outline = outline;
                session.Filter = filter;
                session.LastMap = DateTime.UtcNow;
            }
            catch
            {
                if (session.Reference != frame) frame.Dispose();
                session.EndPage();
                throw;
            }
        }
        finally
        {
            LiveGate.Release();
        }

        var canvas = session.Canvas;
        await SendAsync(socket, new
        {
            type = "started",
            seq,
            aspect = Math.Round(canvas.Width / (double)canvas.Height, 4),
            columns = canvas.Columns,
            rows = canvas.Rows,
            levels = canvas.Levels(),
            progress = Math.Round(canvas.Progress, 3),
            map = MapUrl(canvas)
        }, ct);

        if (canvas.Complete) await FinishLivePageAsync(socket, session, ct);
    }

    /// <summary>
    /// The full detector on the full-resolution frame follows curls and bends; but if it disagrees with the outline the
    /// phone was showing, the phone's outline wins: that is the page the person meant.
    /// </summary>
    static ScanOutline? ChooseOutline(Mat frame, double[][]? shown)
    {
        var detected = ScanPipeline.Detect(frame);
        var hint = shown is { Length: 4 } ? ScanOutline.Sanitize(ScanOutline.FromCorners(shown, confident: true)) : null;
        if (hint is null) return detected.Confident ? detected : null;
        return detected.Confident && Overlap(detected.Corners, hint.Corners) > 0.8 ? detected : hint;
    }

    /// <summary>Intersection over union of two quadrilaterals (normalized corners).</summary>
    static double Overlap(double[][] a, double[][] b)
    {
        static Point2f[] Points(double[][] q) => q.Select(c => new Point2f((float)c[0] * 1000, (float)c[1] * 1000)).ToArray();
        var pa = Points(a);
        var pb = Points(b);
        var intersection = Cv2.IntersectConvexConvex(pa, pb, out _);
        var union = Cv2.ContourArea(pa) + Cv2.ContourArea(pb) - intersection;
        return union > 0 ? intersection / union : 0;
    }

    /// <summary>A close-up: place it on the page and take its detail.</summary>
    async Task AddDetailAsync(WebSocket socket, LiveSession session, long seq, ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        var canvas = session.Canvas;
        if (canvas is null)
        {
            // The page was finished or cancelled while this frame was on its way.
            await SendAsync(socket, new { type = "busy", seq }, ct);
            return;
        }

        DetailStep step;
        await LiveGate.WaitAsync(ct);
        try
        {
            step = await Task.Run(() =>
            {
                using var frame = DecodeFrame(jpeg, seq);
                return canvas.Add(frame);
            }, ct);
        }
        finally
        {
            LiveGate.Release();
        }

        string? map = null;
        if (step.Improved && (canvas.Complete || DateTime.UtcNow - session.LastMap >= MapInterval))
        {
            map = MapUrl(canvas);
            session.LastMap = DateTime.UtcNow;
        }
        await SendAsync(socket, new
        {
            type = "detail",
            seq,
            accepted = step.Accepted,
            problem = step.Problem,
            footprint = step.Footprint,
            levels = step.Improved ? canvas.Levels() : null,
            progress = Math.Round(canvas.Progress, 3),
            map
        }, ct);

        if (canvas.Complete) await FinishLivePageAsync(socket, session, ct);
    }

    /// <summary>Cleans, renders and saves the page to the inbox, like any other scan.</summary>
    async Task FinishLivePageAsync(WebSocket socket, LiveSession session, CancellationToken ct)
    {
        var canvas = session.Canvas!;
        await SendAsync(socket, new { type = "finishing" }, ct);
        object scan;
        using (var flat = canvas.Snapshot())
        {
            // The first frame and its outline describe the same page as the canvas, only smaller: the edge clean-up
            // samples the table around the page there.
            EdgeCleaner.Clean(session.Reference!, session.Outline!, flat);
            scan = await SaveLiveScanAsync(flat, session.Outline!, session.Filter, ct);
            session.LastPage = PageFingerprint.FromPage(flat);
        }
        session.EndPage();
        await SendAsync(socket, new { type = "done", scan }, ct);
    }

    static string MapUrl(DetailCanvas canvas) => "data:image/jpeg;base64," + Convert.ToBase64String(canvas.Thumbnail());

    static Mat DecodeFrame(ReadOnlyMemory<byte> jpeg, long seq)
    {
        try { return ScanPipeline.Decode(jpeg.ToArray()); }
        catch (ScanException error) { throw new LiveFrameException(seq, error.Message); }
    }

    // ---- WebSocket plumbing ---------------------------------------------------------------------------------------

    /// <summary>
    /// The page that opened the socket must be this server's own page. Behind tailscale serve the Host header is the
    /// tailnet name (and X-Forwarded-Host carries it too); non-browser clients send no Origin and can't be tricked.
    /// </summary>
    static bool OriginAllowed(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        foreach (var host in new[] { request.Headers.Host.ToString(), request.Headers["X-Forwarded-Host"].ToString() })
        {
            if (host.Length == 0) continue;
            if (!Uri.TryCreate($"{uri.Scheme}://{host}", UriKind.Absolute, out var expected)) continue;
            if (string.Equals(expected.Host, uri.Host, StringComparison.OrdinalIgnoreCase) && expected.Port == uri.Port) return true;
        }
        return false;
    }

    static async Task<(WebSocketMessageType Type, byte[] Data)?> ReceiveAsync(WebSocket socket, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "Bye", ct);
                return null;
            }
            if (message.Length + result.Count > maxBytes)
            {
                await CloseAsync(socket, WebSocketCloseStatus.MessageTooBig, "Frame too large", ct);
                return null;
            }
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return (result.MessageType, message.ToArray());
        }
    }

    static async Task SendAsync(WebSocket socket, object payload, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason, CancellationToken ct)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(status, reason, ct);
        }
        catch (WebSocketException) { /* already gone */ }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>Encodes a frame the way the phone sends it (for tests and tools).</summary>
    internal static byte[] LiveFrame(object header, byte[] jpeg)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        var frame = new byte[2 + json.Length + jpeg.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)json.Length);
        json.CopyTo(frame, 2);
        jpeg.CopyTo(frame, 2 + json.Length);
        return frame;
    }
}
