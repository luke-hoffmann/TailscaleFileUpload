using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;

namespace Taildrop.Core;

/// <summary>
/// The phone-facing web page, embedded in the assembly as a flat "Assets.&lt;file&gt;" resource set.
/// Only embedded files can ever be served, so the allowlist is simply the embedded resource list.
/// </summary>
static class StaticAssets
{
    const string Prefix = "Assets.";

    static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png"
    };

    static readonly Assembly Source = typeof(StaticAssets).Assembly;

    static readonly HashSet<string> Allowed = new(
        Source.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(name => name[Prefix.Length..]),
        StringComparer.Ordinal);

    sealed record Asset(byte[] Bytes, string ETag);

    static readonly ConcurrentDictionary<string, Asset?> Cache = new(StringComparer.Ordinal);

    public static bool TryGet(string requestedName, out byte[] bytes, out string contentType, out string etag)
    {
        bytes = Array.Empty<byte>();
        contentType = "application/octet-stream";
        etag = "";
        if (!Allowed.Contains(requestedName)) return false;

        var asset = Cache.GetOrAdd(requestedName, Load);
        if (asset is null) return false;

        bytes = asset.Bytes;
        etag = asset.ETag;
        var extension = Path.GetExtension(requestedName);
        contentType = ContentTypes.TryGetValue(extension, out var type) ? type : contentType;
        return true;
    }

    static Asset? Load(string name)
    {
        using var stream = Source.GetManifestResourceStream(Prefix + name);
        if (stream is null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var data = memory.ToArray();
        var tag = "\"" + Convert.ToHexString(SHA256.HashData(data))[..16].ToLowerInvariant() + "\"";
        return new Asset(data, tag);
    }
}
