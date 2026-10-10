// The managed-mod manifest approach of Proteus's PenumbraModMeta / CompositorService.ManagedMod
// (https://github.com/solona-m/proteus, AGPL-3.0, by Solona), reduced to what Cummy needs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Cummy.ThirdParty.Proteus;

/// Writes the managed mod's Penumbra <c>meta.json</c> (FileVersion 4: the redirects live in <c>DefaultData</c>).
/// The <c>Identifier</c> is kept across rewrites so Penumbra keeps treating it as the same mod.
public static class ManagedModManifest
{
    public const string MetaFile = "meta.json";
    private const int FileVersion = 4;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static bool Exists(string modRoot) => File.Exists(Path.Combine(modRoot, MetaFile));

    /// Replace the redirect set (<paramref name="files"/>: game path → path relative to the mod folder).
    public static void Write(string modRoot, IReadOnlyDictionary<string, string> files)
    {
        Directory.CreateDirectory(modRoot);
        var path = Path.Combine(modRoot, MetaFile);

        var manifest = new Dictionary<string, object?>
        {
            ["FileVersion"] = FileVersion,
            ["Identifier"] = ReadIdentifier(path) ?? Guid.NewGuid().ToString(),
            ["Name"] = "Cummy",
            ["Author"] = "Cummy plugin",
            ["Description"] = "Managed by the Cummy plugin: your current layers, composited onto your own skin. "
                            + "Rewritten automatically - edits here are overwritten.",
            ["Version"] = "",
            ["Website"] = "",
            ["ModTags"] = Array.Empty<string>(),
            ["DefaultData"] = new Dictionary<string, object>
            {
                ["Files"] = files,
                ["FileSwaps"] = new Dictionary<string, string>(),
                ["Manipulations"] = Array.Empty<object>(),
            },
            ["Groups"] = Array.Empty<object>(),
        };

        // Atomic: a crash mid-write must not leave Penumbra an unparseable manifest.
        var tmp = path + "." + Path.GetRandomFileName() + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, WriteOptions));
        File.Move(tmp, path, overwrite: true);
    }

    private static string? ReadIdentifier(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("Identifier", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
