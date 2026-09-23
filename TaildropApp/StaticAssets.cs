using System.Reflection;

namespace TaildropApp;

static class StaticAssets
{
    static readonly Dictionary<string, string> ContentTypes = new()
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".svg"] = "image/svg+xml"
    };

    static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "index.html", "app.js", "styles.css", "favicon.svg"
    };

    static readonly Dictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    public static bool TryGet(string requestedName, out byte[] bytes, out string contentType)
    {
        bytes = Array.Empty<byte>();
        contentType = "application/octet-stream";
        if (!Allowed.Contains(requestedName)) return false;

        if (!Cache.TryGetValue(requestedName, out var cached))
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream($"TaildropApp.Assets.{requestedName}");
            if (stream is null) return false;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            cached = memory.ToArray();
            Cache[requestedName] = cached;
        }

        bytes = cached;
        var extension = Path.GetExtension(requestedName);
        contentType = ContentTypes.TryGetValue(extension, out var type) ? type : contentType;
        return true;
    }
}
