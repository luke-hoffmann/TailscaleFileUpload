using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace Taildrop.Core.Tests;

/// <summary>Runs the real server on a loopback port with a throwaway inbox.</summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public string Inbox { get; } = Path.Combine(Path.GetTempPath(), "Taildrop-test-" + Guid.NewGuid().ToString("N"));
    public TaildropServer Server { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    public byte[] PagePhoto { get; private set; } = null!;
    public (int Wide, int High) PageTexels { get; private set; }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Inbox);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Server = new TaildropServer(Inbox);
        await Server.StartAsync(IPAddress.Loopback, port);
        Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };

        using var scene = new SyntheticScene(new SceneOptions { Width = 2400, Height = 1800, TiltDeg = 16, YawDeg = 8, RollDeg = 5, PillowXPx = 24, Backdrop = Backdrop.BlueCloth });
        PageTexels = (scene.PageTexelsWide, scene.PageTexelsHigh);
        Cv2.ImEncode(".jpg", scene.Photo, out var bytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, 90));
        PagePhoto = bytes;
    }

    public async Task DisposeAsync()
    {
        Http.Dispose();
        await Server.DisposeAsync();
        try { Directory.Delete(Inbox, recursive: true); } catch { /* best effort */ }
    }
}

public class ServerTests : IClassFixture<ServerFixture>
{
    readonly ServerFixture _f;
    readonly ITestOutputHelper _output;
    public ServerTests(ServerFixture fixture, ITestOutputHelper output) { _f = fixture; _output = output; }

    HttpRequestMessage Scan(HttpMethod method, string url, byte[]? body = null, string contentType = "image/jpeg", bool withHeader = true)
    {
        var request = new HttpRequestMessage(method, url);
        if (withHeader) request.Headers.Add("X-Taildrop", "1");
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }
        return request;
    }

    HttpRequestMessage Json(HttpMethod method, string url, object body) =>
        Scan(method, url, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)), "application/json");

    static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();

    async Task<JsonElement> NewScan(byte[]? photo = null)
    {
        var response = await _f.Http.SendAsync(Scan(HttpMethod.Post, "/api/scan", photo ?? _f.PagePhoto));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return Parse(text);
    }

    string InboxPath(JsonElement scan) => Path.Combine(_f.Inbox, scan.GetProperty("name").GetString()!);

    async Task<JsonElement> NewPdfPage(string pdf, int? index = null)
    {
        var request = Scan(HttpMethod.Post, "/api/scan", _f.PagePhoto);
        request.Headers.Add("X-Scan-Pdf", pdf);
        if (index is not null) request.Headers.Add("X-Scan-Pdf-Index", index.Value.ToString());
        var response = await _f.Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return Parse(text);
    }

    static byte[] Read(string path)
    {
        // The server may still be finishing a move; give it a moment.
        for (var i = 0; i < 20; i++)
        {
            try { return File.ReadAllBytes(path); }
            catch (IOException) { Thread.Sleep(50); }
        }
        return File.ReadAllBytes(path);
    }

    [Fact]
    public async Task HealthReportsScannerAndStaticAssetsRevalidate()
    {
        var health = Parse(await _f.Http.GetStringAsync("/api/health"));
        Assert.True(health.GetProperty("ready").GetBoolean());
        Assert.True(health.GetProperty("scan").GetBoolean());

        var first = await _f.Http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag!.Tag;
        var second = new HttpRequestMessage(HttpMethod.Get, "/");
        second.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await _f.Http.SendAsync(second)).StatusCode);
        foreach (var asset in new[] { "/app.js", "/transfers.js", "/scan.js", "/editor.js", "/styles.css", "/favicon.svg" })
            Assert.Equal(HttpStatusCode.OK, (await _f.Http.GetAsync(asset)).StatusCode);
        var icon = await _f.Http.GetAsync("/apple-touch-icon.png");
        Assert.Equal("image/png", icon.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Http.GetAsync("/Taildrop.Core.dll")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Http.GetAsync("/..%2f..%2fetc/passwd")).StatusCode);
    }

    [Fact]
    public async Task ScanLifecycle_Create_Adjust_Rotate_Retake()
    {
        // ---- create
        var scan = await NewScan();
        var id = scan.GetProperty("id").GetString()!;
        var name = scan.GetProperty("name").GetString()!;
        Assert.Matches(@"^Scan \d{4}-\d{2}-\d{2} at \d{2}\.\d{2}\.\d{2}( \(\d+\))?\.jpg$", name);
        Assert.True(scan.GetProperty("confident").GetBoolean());
        Assert.Equal("auto", scan.GetProperty("filter").GetString());
        Assert.Equal(33, scan.GetProperty("outline").GetProperty("top").GetArrayLength());

        var path = InboxPath(scan);
        Assert.True(File.Exists(path));
        Assert.True(Directory.Exists(Path.Combine(_f.Inbox, ".scans", id))); // hidden working folder, never a top-level file
        Assert.DoesNotContain(Directory.GetFiles(_f.Inbox), f => f.EndsWith(".part"));

        using (var page = Cv2.ImDecode(Read(path), ImreadModes.Color))
        {
            Assert.Equal(scan.GetProperty("width").GetInt32(), page.Width);
            Assert.Equal(4, PageAnalysis.FindMarkers(page, _f.PageTexels).Count);
        }

        // ---- preview + source images (never cached by the phone)
        var preview = await _f.Http.GetAsync(scan.GetProperty("previewUrl").GetString());
        Assert.Equal("image/jpeg", preview.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-store", preview.Headers.CacheControl!.ToString());
        var source = await _f.Http.GetByteArrayAsync(scan.GetProperty("sourceUrl").GetString());
        using (var sourceImage = Cv2.ImDecode(source, ImreadModes.Color))
            Assert.Equal(1600, Math.Max(sourceImage.Width, sourceImage.Height));

        // ---- change filter + rotate: same file, replaced in place
        var bw = await _f.Http.SendAsync(Json(HttpMethod.Put, $"/api/scan/{id}", new { filter = "bw", rotate = 90 }));
        var bwBody = Parse(await bw.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, bw.StatusCode);
        Assert.Equal(name, bwBody.GetProperty("name").GetString());
        Assert.Equal(1, bwBody.GetProperty("version").GetInt32());
        Assert.Equal(scan.GetProperty("height").GetInt32(), bwBody.GetProperty("width").GetInt32()); // rotated a quarter turn
        using (var bwPage = Cv2.ImDecode(Read(path), ImreadModes.Unchanged))
        {
            Assert.Equal(1, bwPage.Channels()); // black & white is a single-channel JPEG
            Assert.Equal(bwBody.GetProperty("width").GetInt32(), bwPage.Width);
        }
        Assert.Single(Directory.GetFiles(_f.Inbox, "*.jpg"), f => Path.GetFileName(f) == name);

        // ---- user drags a corner: a smaller straight-edged outline crops the page
        var crop = new
        {
            outline = new
            {
                corners = new[] { new[] { 0.3, 0.3 }, new[] { 0.6, 0.3 }, new[] { 0.6, 0.6 }, new[] { 0.3, 0.6 } },
                top = Array.Empty<double[]>(), right = Array.Empty<double[]>(), bottom = Array.Empty<double[]>(), left = Array.Empty<double[]>()
            },
            rotate = 0,
            filter = "original"
        };
        var cropped = await _f.Http.SendAsync(Json(HttpMethod.Put, $"/api/scan/{id}", crop));
        var croppedBody = Parse(await cropped.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, cropped.StatusCode);
        Assert.InRange(croppedBody.GetProperty("width").GetInt32(), 600, 900); // 30% of a 2400 px photo is ~720
        Assert.Equal("original", croppedBody.GetProperty("filter").GetString());

        // ---- retake: gone from the inbox and from the server
        var delete = await _f.Http.SendAsync(Scan(HttpMethod.Delete, $"/api/scan/{id}"));
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.Combine(_f.Inbox, ".scans", id)));
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Http.GetAsync($"/api/scan/{id}/preview")).StatusCode);
    }

    [Fact]
    public async Task PdfLifecycle_AddPages_Edit_Retake_Remove()
    {
        // ---- first page starts a PDF; no JPEG appears
        var jpegsBefore = Directory.GetFiles(_f.Inbox, "*.jpg").Length;
        var first = await NewPdfPage("new");
        var pdf = first.GetProperty("pdf");
        var pdfId = pdf.GetProperty("id").GetString()!;
        var name = first.GetProperty("name").GetString()!;
        Assert.Matches(@"^Scan \d{4}-\d{2}-\d{2} at \d{2}\.\d{2}\.\d{2}( \(\d+\))?\.pdf$", name);
        Assert.Equal(name, pdf.GetProperty("name").GetString());
        Assert.Equal((1, 1), (pdf.GetProperty("page").GetInt32(), pdf.GetProperty("pages").GetInt32()));
        Assert.Equal(jpegsBefore, Directory.GetFiles(_f.Inbox, "*.jpg").Length);
        Assert.True(first.GetProperty("confident").GetBoolean());

        var path = InboxPath(first);
        var document = new PdfInspector(Read(path));
        document.AssertWellFormed();
        Assert.Equal(1, document.PageCount);
        Assert.Equal((595.28, 841.89), document.MediaBoxes[0]); // the synthetic page is A4
        Assert.Equal("DeviceRGB", document.Images[0].ColorSpace);
        Assert.Equal(pdf.GetProperty("size").GetInt64(), new FileInfo(path).Length);

        // ---- more pages: appended, or put in place with an index
        var second = await NewPdfPage(pdfId);
        Assert.Equal((2, 2), (second.GetProperty("pdf").GetProperty("page").GetInt32(), second.GetProperty("pdf").GetProperty("pages").GetInt32()));
        var front = await NewPdfPage(pdfId, index: 0);
        Assert.Equal((1, 3), (front.GetProperty("pdf").GetProperty("page").GetInt32(), front.GetProperty("pdf").GetProperty("pages").GetInt32()));
        Assert.Equal(name, front.GetProperty("name").GetString());
        Assert.Equal(3, new PdfInspector(Read(path)).PageCount);
        Assert.Single(Directory.GetFiles(_f.Inbox, "*.pdf"), f => Path.GetFileName(f) == name);

        // ---- edit a page: B&W and a quarter turn rewrite that page only
        var secondId = second.GetProperty("id").GetString()!;
        var edit = await _f.Http.SendAsync(Json(HttpMethod.Put, $"/api/scan/{secondId}", new { filter = "bw", rotate = 90 }));
        var edited = Parse(await edit.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(3, edited.GetProperty("pdf").GetProperty("page").GetInt32());
        document = new PdfInspector(Read(path));
        document.AssertWellFormed();
        Assert.Equal(new[] { (595.28, 841.89), (595.28, 841.89), (841.89, 595.28) }, document.MediaBoxes);
        Assert.Equal(new[] { "DeviceRGB", "DeviceRGB", "DeviceGray" }, document.Images.Select(i => i.ColorSpace));
        Assert.Equal(1, document.Images[2].Bits);
        var preview = await _f.Http.GetByteArrayAsync(edited.GetProperty("previewUrl").GetString());
        using (var previewImage = Cv2.ImDecode(preview, ImreadModes.Color))
            Assert.True(previewImage.Width > previewImage.Height); // the phone sees the turned page

        // ---- retake the middle page: it leaves the PDF, the new photo goes back in its place
        var firstId = first.GetProperty("id").GetString()!;
        var retake = await _f.Http.SendAsync(Scan(HttpMethod.Delete, $"/api/scan/{firstId}"));
        var retaken = Parse(await retake.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, retake.StatusCode);
        Assert.Equal(2, retaken.GetProperty("pdf").GetProperty("pages").GetInt32());
        Assert.False(Directory.Exists(Path.Combine(_f.Inbox, ".scans", firstId)));
        var replacement = await NewPdfPage(pdfId, index: 1);
        Assert.Equal((2, 3), (replacement.GetProperty("pdf").GetProperty("page").GetInt32(), replacement.GetProperty("pdf").GetProperty("pages").GetInt32()));

        // ---- renamed on the PC: later pages keep going into the renamed file
        var renamed = "Lease agreement.pdf";
        File.Move(path, Path.Combine(_f.Inbox, renamed));
        _f.Server.OnInboxFileRenamed(name, renamed);
        var fourth = await NewPdfPage(pdfId);
        Assert.Equal(renamed, fourth.GetProperty("name").GetString());
        document = new PdfInspector(Read(Path.Combine(_f.Inbox, renamed)));
        Assert.Equal(4, document.PageCount);
        Assert.Contains("/Title <FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("Lease agreement")) + ">", document.Text);

        // ---- every page retaken away: the file goes with the last one
        foreach (var page in new[] { front, replacement, second, fourth })
            Assert.Equal(HttpStatusCode.OK, (await _f.Http.SendAsync(Scan(HttpMethod.Delete, $"/api/scan/{page.GetProperty("id").GetString()}"))).StatusCode);
        Assert.False(File.Exists(Path.Combine(_f.Inbox, renamed)));

        // ---- a PDF removed in the desktop window can't take more pages
        var again = await NewPdfPage(pdfId);
        var againName = again.GetProperty("name").GetString()!;
        Assert.EndsWith(".pdf", againName);
        _f.Server.OnInboxFileRemoved(againName);
        File.Delete(InboxPath(again));
        var request = Scan(HttpMethod.Post, "/api/scan", _f.PagePhoto);
        request.Headers.Add("X-Scan-Pdf", pdfId);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Http.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Http.SendAsync(Json(HttpMethod.Put, $"/api/scan/{again.GetProperty("id").GetString()}", new { filter = "gray" }))).StatusCode);

        // ---- bad targets
        foreach (var (target, position) in new[] { ("../x", (string?)null), (new string('a', 32), null), ("new", "-1"), ("new", "two") })
        {
            var bad = Scan(HttpMethod.Post, "/api/scan", _f.PagePhoto);
            bad.Headers.Add("X-Scan-Pdf", target);
            if (position is not null) bad.Headers.Add("X-Scan-Pdf-Index", position);
            Assert.True((await _f.Http.SendAsync(bad)).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest);
        }
        Assert.DoesNotContain(Directory.GetFiles(_f.Inbox), f => f.EndsWith(".part"));
    }

    [Fact]
    public async Task ScanFollowsRenamesAndRecoversFromRemovals()
    {
        var scan = await NewScan();
        var id = scan.GetProperty("id").GetString()!;
        var oldName = scan.GetProperty("name").GetString()!;

        // PC user renames the file in the desktop window.
        var renamed = "Receipt - groceries.jpg";
        File.Move(InboxPath(scan), Path.Combine(_f.Inbox, renamed));
        _f.Server.OnInboxFileRenamed(oldName, renamed);

        var update = await _f.Http.SendAsync(Json(HttpMethod.Put, $"/api/scan/{id}", new { filter = "gray" }));
        var body = Parse(await update.Content.ReadAsStringAsync());
        Assert.Equal(renamed, body.GetProperty("name").GetString());
        Assert.False(File.Exists(Path.Combine(_f.Inbox, oldName)));
        using (var gray = Cv2.ImDecode(Read(Path.Combine(_f.Inbox, renamed)), ImreadModes.Unchanged))
            Assert.Equal(1, gray.Channels());

        // File removed on the PC without telling the server (e.g. in Explorer): the next edit brings the scan back.
        File.Delete(Path.Combine(_f.Inbox, renamed));
        var again = await _f.Http.SendAsync(Json(HttpMethod.Put, $"/api/scan/{id}", new { filter = "auto" }));
        var againBody = Parse(await again.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True(File.Exists(InboxPath(againBody)));

        // Removed through the desktop window: the server forgets the scan entirely.
        _f.Server.OnInboxFileRemoved(againBody.GetProperty("name").GetString()!);
        File.Delete(InboxPath(againBody));
        var gone = await _f.Http.SendAsync(Json(HttpMethod.Put, $"/api/scan/{id}", new { filter = "auto" }));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_f.Inbox, ".scans", id)));
    }

    [Fact]
    public async Task ScanRejectsBadRequests()
    {
        var scan = await NewScan();
        var id = scan.GetProperty("id").GetString()!;

        // State-changing calls need the custom header (blocks cross-site simple requests).
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Http.SendAsync(Scan(HttpMethod.Post, "/api/scan", _f.PagePhoto, withHeader: false))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Http.SendAsync(Scan(HttpMethod.Delete, $"/api/scan/{id}", withHeader: false))).StatusCode);

        // Malformed ids can't reach the file system.
        foreach (var bad in new[] { "..", "zz", "../x", new string('a', 31), new string('A', 32), id + "0" })
            Assert.Equal(HttpStatusCode.NotFound, (await _f.Http.SendAsync(Scan(HttpMethod.Put, $"/api/scan/{Uri.EscapeDataString(bad)}", Encoding.UTF8.GetBytes("{}"), "application/json"))).StatusCode);

        // Bad updates
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Http.SendAsync(Json(HttpMethod.Put, $"/api/scan/{id}", new { rotate = 45 }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Http.SendAsync(Json(HttpMethod.Put, $"/api/scan/{id}", new { filter = "sepia" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Http.SendAsync(Scan(HttpMethod.Put, $"/api/scan/{id}", Encoding.UTF8.GetBytes("not json"), "application/json"))).StatusCode);
        var nan = new { outline = new { corners = new[] { new[] { 9.0, 9.0 }, new[] { 9.0, 9.0 }, new[] { 9.0, 9.0 }, new[] { 9.0, 9.0 } } } };
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Http.SendAsync(Json(HttpMethod.Put, $"/api/scan/{id}", nan))).StatusCode);

        // Unusable photos
        var garbage = await _f.Http.SendAsync(Scan(HttpMethod.Post, "/api/scan", Enumerable.Range(0, 4000).Select(i => (byte)(i * 31)).ToArray()));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, garbage.StatusCode);
        Assert.Contains("error", await garbage.Content.ReadAsStringAsync());
        var heic = new byte[64];
        Encoding.ASCII.GetBytes("ftypheic").CopyTo(heic, 4);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _f.Http.SendAsync(Scan(HttpMethod.Post, "/api/scan", heic))).StatusCode);

        // No cross-origin permissions are ever granted.
        var probe = await _f.Http.GetAsync("/api/health");
        Assert.False(probe.Headers.Contains("Access-Control-Allow-Origin"));

        await _f.Http.SendAsync(Scan(HttpMethod.Delete, $"/api/scan/{id}"));
    }

    [Fact]
    public async Task ParallelScansAndUploadsGetDistinctNames()
    {
        var scans = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => NewScan()));
        var scanNames = scans.Select(s => s.GetProperty("name").GetString()).ToList();
        Assert.Equal(3, scanNames.Distinct().Count());

        // Original upload path keeps working, and concurrent same-name uploads never collide.
        var uploads = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/upload") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello " + i)) };
            request.Headers.Add("X-File-Name", Uri.EscapeDataString("notes.txt"));
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
            var response = await _f.Http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return Parse(await response.Content.ReadAsStringAsync()).GetProperty("name").GetString()!;
        }));
        Assert.Equal(8, uploads.Distinct().Count());
        foreach (var name in uploads) Assert.True(File.Exists(Path.Combine(_f.Inbox, name)));
        Assert.DoesNotContain(Directory.GetFiles(_f.Inbox), f => f.EndsWith(".part"));

        // Uploads without the header are still refused.
        var plain = new HttpRequestMessage(HttpMethod.Post, "/api/upload") { Content = new ByteArrayContent(new byte[] { 1 }) };
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Http.SendAsync(plain)).StatusCode);

        foreach (var s in scans) await _f.Http.SendAsync(Scan(HttpMethod.Delete, $"/api/scan/{s.GetProperty("id").GetString()}"));
    }
}
