// The managed-mod manifest approach of Proteus's PenumbraModMeta / CompositorService.ManagedMod
// (https://github.com/solona-m/proteus, AGPL-3.0, by Solona), reduced to what Cummy needs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Cummy.ThirdParty.Proteus;

/// Writes the managed mod's Penumbra <c>meta.json</c> (FileVersion 4: the redirects live in <c>DefaultData</c>), with
/// the Proteus overlay pack's multi-select option group when it has options (Cummy's one mod carries both).
/// The <c>Identifier</c> is kept across rewrites so Penumbra keeps treating it as the same mod.
public static class ManagedModManifest
{
    public const string MetaFile = "meta.json";
    private const int FileVersion = 4;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static bool Exists(string modRoot) => File.Exists(Path.Combine(modRoot, MetaFile));

    /// The no-op swap Proteus-made overlay packs carry; kept whenever the mod carries the pack's group.
    private const string NoOpSwap = "chara/monster/m8030/obj/body/b0001/material/v0001/mt_m8030b0001_a.mtrl";

    /// Replace the manifest: <paramref name="name"/> as shown in Penumbra, the redirect set (<paramref name="files"/>:
    /// game path → path relative to the mod folder), and the options of the multi-select group
    /// <paramref name="groupName"/> (none = no group).
    public static void Write(string modRoot, string name, IReadOnlyDictionary<string, string> files, string groupName,
                             IReadOnlyList<string> options, IReadOnlyList<object>? manipulations = null,
                             string? credit = null)
    {
        Directory.CreateDirectory(modRoot);
        var path = Path.Combine(modRoot, MetaFile);

        var manifest = new Dictionary<string, object?>
        {
            ["FileVersion"] = FileVersion,
            ["Identifier"] = ReadIdentifier(path) ?? Guid.NewGuid().ToString(),
            ["Name"] = name,
            ["Author"] = "Cummy plugin",
            ["Description"] = "Managed by the Cummy plugin: your current layers, composited onto your own skin (and, "
                            + "with Proteus, the \"Cummy layers\" options Proteus composites). Rewritten automatically - "
                            + "edits here are overwritten." + (credit != null ? "\n\n" + credit : ""),
            ["Version"] = "",
            ["Website"] = "",
            ["ModTags"] = Array.Empty<string>(),
            ["DefaultData"] = new Dictionary<string, object>
            {
                ["Files"] = files,
                ["FileSwaps"] = options.Count == 0
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { [NoOpSwap] = NoOpSwap },
                ["Manipulations"] = manipulations ?? Array.Empty<object>(),
            },
            ["Groups"] = options.Count == 0 ? Array.Empty<object>() : new object[]
            {
                new Dictionary<string, object>
                {
                    ["Type"] = "Multi",
                    ["Name"] = groupName,
                    ["Description"] = "Set by Cummy to your layered body parts (and other plugins' overlays).",
                    ["Priority"] = 0,
                    ["DefaultSettings"] = 0,
                    ["Options"] = options.Select(option => new Dictionary<string, object>
                    {
                        ["Name"] = option,
                        ["Description"] = "",
                        ["Priority"] = 0,
                        ["Files"] = new Dictionary<string, string>(),
                        ["FileSwaps"] = new Dictionary<string, string>(),
                        ["Manipulations"] = Array.Empty<object>(),
                    }).ToArray(),
                },
            },
        };

        // Atomic: a crash mid-write must not leave Penumbra an unparseable manifest.
        var tmp = path + "." + Path.GetRandomFileName() + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, WriteOptions));
        File.Move(tmp, path, overwrite: true);
    }

    /// The name the manifest at <paramref name="modRoot"/> shows, or null.
    public static string? ReadName(string modRoot)
    {
        try
        {
            var path = Path.Combine(modRoot, MetaFile);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        }
        catch
        {
            return null;
        }
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
