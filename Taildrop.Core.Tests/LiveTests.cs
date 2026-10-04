using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace Taildrop.Core.Tests;

/// <summary>The live scanner end to end over a real WebSocket, the way the phone page drives it.</summary>
public class LiveTests : IClassFixture<ServerFixture>
{
    readonly ServerFixture _f;
    readonly ITestOutputHelper _output;
    static readonly OpenCvSharp.Size Portrait = new(1080, 1920);

    public LiveTests(ServerFixture fixture, ITestOutputHelper output) { _f = fixture; _output = output; }

    Uri LiveUri => new($"ws://127.0.0.1:{_f.Http.BaseAddress!.Port}/api/live");

    async Task<ClientWebSocket> ConnectAsync(string? origin = null)
    {
        var socket = new ClientWebSocket();
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
        await socket.ConnectAsync(LiveUri, CancellationToken.None);
        var ready = await ReceiveAsync(socket);
        Assert.Equal("ready", ready.GetProperty("type").GetString());
        return socket;
    }

    static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket, int timeoutSeconds = 60)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var buffer = new byte[256 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException($"closed: {result.CloseStatus} {result.CloseStatusDescription}");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonDocument.Parse(message.ToArray()).RootElement.Clone();
    }

    static Task SendFrameAsync(ClientWebSocket socket, object header, Mat image, int quality = 90)
    {
        Cv2.ImEncode(".jpg", image, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, quality));
        return socket.SendAsync(TaildropServer.LiveFrame(header, jpeg), WebSocketMessageType.Binary, true, CancellationToken.None);
    }

    static Task SendCommandAsync(ClientWebSocket socket, string type) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type })), WebSocketMessageType.Text, true, CancellationToken.None);

    static Mat Small(Mat frame, int longEdge = 640)
    {
        var scale = longEdge / (double)Math.Max(frame.Width, frame.Height);
        var small = new Mat();
        Cv2.Resize(frame, small, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Area);
        return small;
    }

    [Fact]
    public async Task AimStartCloseUpsFinish_SavesOneSharpPageToTheInbox()
    {
        using var document = SyntheticDocument.Make(2480, 3508, seed: 21);
        var (reference, outline) = SyntheticDocument.WholePage(document, Portrait);
        using var _ = reference;
        using var socket = await ConnectAsync();
        var before = Directory.GetFiles(_f.Inbox).Length;

        // ---- aim: where is the page?
        using (var preview = Small(reference))
            await SendFrameAsync(socket, new { type = "aim", seq = 1 }, preview, 70);
        var aim = await ReceiveAsync(socket);
        Assert.Equal("aim", aim.GetProperty("type").GetString());
        Assert.Equal(1, aim.GetProperty("seq").GetInt64());
        var page = aim.GetProperty("page");
        Assert.True(page.GetProperty("confident").GetBoolean());
        var corners = page.GetProperty("corners").Deserialize<double[][]>()!;
        _output.WriteLine("found " + string.Join(" ", corners.Select(c => $"({c[0]:F3},{c[1]:F3})")) + " truth " + string.Join(" ", outline.Corners.Select(c => $"({c[0]:F3},{c[1]:F3})")));
        for (var i = 0; i < 4; i++)
        {
            Assert.InRange(corners[i][0], outline.Corners[i][0] - 0.02, outline.Corners[i][0] + 0.02);
            Assert.InRange(corners[i][1], outline.Corners[i][1] - 0.02, outline.Corners[i][1] + 0.02);
        }
        Assert.False(aim.GetProperty("same").GetBoolean());

        // ---- start: the whole page at full resolution
        await SendFrameAsync(socket, new { type = "start", seq = 2, filter = "gray", corners }, reference);
        var started = await ReceiveAsync(socket);
        Assert.Equal("started", started.GetProperty("type").GetString());
        var columns = started.GetProperty("columns").GetInt32();
        var rows = started.GetProperty("rows").GetInt32();
        Assert.Equal(columns * rows, started.GetProperty("levels").GetArrayLength());
        Assert.InRange(started.GetProperty("aspect").GetDouble(), 0.69, 0.72);
        Assert.StartsWith("data:image/jpeg;base64,", started.GetProperty("map").GetString());
        var progress = started.GetProperty("progress").GetDouble();
        Assert.InRange(progress, 0.2, 0.9);

        // While a page is being built, aim frames are not wanted.
        using (var preview = Small(reference))
            await SendFrameAsync(socket, new { type = "aim", seq = 3 }, preview, 70);
        Assert.Equal("busy", (await ReceiveAsync(socket)).GetProperty("type").GetString());

        // ---- close-ups until the PC says the page is done
        JsonElement? done = null;
        var seq = 10;
        foreach (var frame in SyntheticDocument.CloseUps(document, Portrait, columns: 4, rows: 5, density: 1.2, seed: 8))
        {
            using (frame)
            {
                if (done is not null) continue;
                await SendFrameAsync(socket, new { type = "detail", seq }, frame);
                var reply = await ReceiveAsync(socket);
                Assert.Equal("detail", reply.GetProperty("type").GetString());
                Assert.Equal(seq, reply.GetProperty("seq").GetInt64());
                Assert.True(reply.GetProperty("accepted").GetBoolean(), reply.ToString());
                Assert.Equal(4, reply.GetProperty("footprint").GetArrayLength());
                var now = reply.GetProperty("progress").GetDouble();
                Assert.True(now >= progress);
                progress = now;
                seq++;
                if (progress >= 0.97)
                {
                    Assert.Equal("finishing", (await ReceiveAsync(socket)).GetProperty("type").GetString());
                    done = await ReceiveAsync(socket, 120);
                }
            }
        }
        Assert.NotNull(done);
        Assert.Equal("done", done!.Value.GetProperty("type").GetString());
        var scan = done.Value.GetProperty("scan");
        _output.WriteLine(scan.ToString());
        Assert.True(scan.GetProperty("live").GetBoolean());
        Assert.False(scan.GetProperty("adjustable").GetBoolean());
        Assert.Equal("gray", scan.GetProperty("filter").GetString());
        Assert.InRange(scan.GetProperty("width").GetInt32(), 2400, 2560);

        // ---- the page is in the inbox, like any scan
        var name = scan.GetProperty("name").GetString()!;
        var path = Path.Combine(_f.Inbox, name);
        Assert.True(File.Exists(path));
        Assert.Equal(before + 1, Directory.GetFiles(_f.Inbox).Length);
        using (var saved = Cv2.ImRead(path, ImreadModes.Unchanged)) Assert.Equal(scan.GetProperty("height").GetInt32(), saved.Height);
        var preview2 = await _f.Http.GetAsync(scan.GetProperty("previewUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, preview2.StatusCode);

        // ---- the same page in view again is recognised; after a retake it may be scanned again
        using (var preview = Small(reference))
            await SendFrameAsync(socket, new { type = "aim", seq = 100 }, preview, 70);
        Assert.True((await ReceiveAsync(socket)).GetProperty("same").GetBoolean());
        await SendCommandAsync(socket, "forget");
        using (var preview = Small(reference))
            await SendFrameAsync(socket, new { type = "aim", seq = 101 }, preview, 70);
        Assert.False((await ReceiveAsync(socket)).GetProperty("same").GetBoolean());

        // ---- look and rotation re-render from the stored flat page; edges can't be adjusted
        var id = scan.GetProperty("id").GetString()!;
        var bw = await _f.Http.SendAsync(Json(HttpMethod.Put, $"/api/scan/{id}", new { filter = "bw", rotate = 90 }));
        Assert.Equal(HttpStatusCode.OK, bw.StatusCode);
        var rerendered = JsonDocument.Parse(await bw.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(scan.GetProperty("height").GetInt32(), rerendered.GetProperty("width").GetInt32());
        var edges = await _f.Http.SendAsync(Json(HttpMethod.Put, $"/api/scan/{id}", new { outline = new { corners = new[] { new[] { 0.1, 0.1 }, new[] { 0.9, 0.1 }, new[] { 0.9, 0.9 }, new[] { 0.1, 0.9 } } } }));
        Assert.Equal(HttpStatusCode.BadRequest, edges.StatusCode);

        // ---- retake deletes it
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/scan/{id}");
        delete.Headers.Add("X-Taildrop", "1");
        Assert.Equal(HttpStatusCode.OK, (await _f.Http.SendAsync(delete)).StatusCode);
        Assert.False(File.Exists(path));

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    [Fact]
    public async Task FinishEarly_SavesThePageAsItIs()
    {
        using var document = SyntheticDocument.Make(2480, 3508, seed: 22);
        var (reference, outline) = SyntheticDocument.WholePage(document, Portrait);
        using var _ = reference;
        using var socket = await ConnectAsync();

        await SendFrameAsync(socket, new { type = "start", seq = 1, corners = outline.Corners }, reference);
        Assert.Equal("started", (await ReceiveAsync(socket)).GetProperty("type").GetString());
        await SendCommandAsync(socket, "finish");
        Assert.Equal("finishing", (await ReceiveAsync(socket)).GetProperty("type").GetString());
        var done = await ReceiveAsync(socket, 120);
        Assert.Equal("done", done.GetProperty("type").GetString());
        Assert.Equal("auto", done.GetProperty("scan").GetProperty("filter").GetString());
        Assert.True(File.Exists(Path.Combine(_f.Inbox, done.GetProperty("scan").GetProperty("name").GetString()!)));

        // A close-up that arrives after the page was finished is simply not wanted.
        using var stray = SyntheticDocument.CloseUps(document, Portrait, 1, 1, 1.2, 3).Single();
        await SendFrameAsync(socket, new { type = "detail", seq = 2 }, stray);
        Assert.Equal("busy", (await ReceiveAsync(socket)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task NoPage_ReportsAnErrorAndKeepsTheConnection()
    {
        using var socket = await ConnectAsync();
        using var empty = new Mat(1920, 1080, MatType.CV_8UC3, new Scalar(40, 60, 90));
        Cv2.Randn(empty, new Scalar(40, 60, 90), new Scalar(6, 6, 6));

        using (var preview = Small(empty))
            await SendFrameAsync(socket, new { type = "aim", seq = 1 }, preview, 70);
        var aim = await ReceiveAsync(socket);
        Assert.Equal(JsonValueKind.Null, aim.GetProperty("page").ValueKind);

        await SendFrameAsync(socket, new { type = "start", seq = 2 }, empty);
        var error = await ReceiveAsync(socket);
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal(2, error.GetProperty("seq").GetInt64());
        Assert.Contains("page", error.GetProperty("message").GetString());

        // Garbage of every kind: answered, never fatal.
        await socket.SendAsync(new byte[] { 9, 0, 1, 2 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal("error", (await ReceiveAsync(socket)).GetProperty("type").GetString());
        await socket.SendAsync(TaildropServer.LiveFrame(new { type = "aim", seq = 3 }, Encoding.UTF8.GetBytes("not a jpeg at all")), WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal("error", (await ReceiveAsync(socket)).GetProperty("type").GetString());
        await socket.SendAsync(Encoding.UTF8.GetBytes("{nope"), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Equal("error", (await ReceiveAsync(socket)).GetProperty("type").GetString());

        await SendCommandAsync(socket, "ping");
        using (var preview = Small(empty))
            await SendFrameAsync(socket, new { type = "aim", seq = 4 }, preview, 70);
        Assert.Equal(4, (await ReceiveAsync(socket)).GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task OnlyThisServersOwnPageMayConnect()
    {
        var port = _f.Http.BaseAddress!.Port;
        using (var own = await ConnectAsync($"http://127.0.0.1:{port}")) Assert.Equal(WebSocketState.Open, own.State);

        foreach (var origin in new[] { "https://evil.example", $"http://127.0.0.1:{port + 1}", "null" })
        {
            using var foreign = new ClientWebSocket();
            foreign.Options.SetRequestHeader("Origin", origin);
            await Assert.ThrowsAnyAsync<WebSocketException>(() => foreign.ConnectAsync(LiveUri, CancellationToken.None));
        }

        // The published address (behind tailscale serve) is this server's page too, whatever Host the proxy sends.
        _f.Server.PublicUrl = "https://box.tail1234.ts.net:8443/";
        try
        {
            using (var published = await ConnectAsync("https://box.tail1234.ts.net:8443")) Assert.Equal(WebSocketState.Open, published.State);
            using var otherPort = new ClientWebSocket();
            otherPort.Options.SetRequestHeader("Origin", "https://box.tail1234.ts.net");
            await Assert.ThrowsAnyAsync<WebSocketException>(() => otherPort.ConnectAsync(LiveUri, CancellationToken.None));
            var csp = (await _f.Http.GetAsync("/")).Headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains("wss://box.tail1234.ts.net:8443", csp);
        }
        finally
        {
            _f.Server.PublicUrl = null;
        }

        // Plain HTTP on the endpoint is refused too.
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Http.GetAsync("/api/live")).StatusCode);
    }

    [Fact]
    public async Task OversizedFrame_ClosesTheSocket()
    {
        using var socket = await ConnectAsync();
        var huge = new byte[25 * 1024 * 1024];
        await socket.SendAsync(huge, WebSocketMessageType.Binary, true, CancellationToken.None);
        var buffer = new byte[1024];
        var result = await socket.ReceiveAsync(buffer, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, result.CloseStatus);
    }

    [Fact]
    public async Task PagesAllowTheirOwnSocketAndCamera()
    {
        var response = await _f.Http.GetAsync("/");
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        var port = _f.Http.BaseAddress!.Port;
        Assert.Contains($"ws://127.0.0.1:{port}", csp);
        Assert.Contains($"wss://127.0.0.1:{port}", csp);
        Assert.Contains("camera=(self)", response.Headers.GetValues("Permissions-Policy").Single());
        Assert.Equal(port, _f.Server.Port);
    }

    static HttpRequestMessage Json(HttpMethod method, string url, object body)
    {
        var request = new HttpRequestMessage(method, url) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Taildrop", "1");
        return request;
    }
}
