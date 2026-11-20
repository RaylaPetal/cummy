using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Cummy.Compositor;

/// Which game paths each Penumbra mod can redirect, across all of its options: the keys of `Files` and `FileSwaps` in
/// `default_mod.json` and in every option (and combining-group container) of its `group_*.json` files. Read from the
/// mod's folder and cached until its JSON files change or <see cref="Invalidate"/> is called. Framework thread.
public sealed class ModPathIndex
{
    private sealed record Entry(string Stamp, HashSet<string> Paths);

    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> warned = new(StringComparer.OrdinalIgnoreCase);

    /// Forget what was read for <paramref name="modDir"/> (its files were edited).
    public void Invalidate(string modDir) => entries.Remove(modDir);

    /// The game paths (lower case, forward slashes) the mod in <paramref name="folder"/> can redirect, or null when its
    /// files can't be read.
    public IReadOnlySet<string>? Paths(string modDir, string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return Unknown(modDir, $"no folder at {folder}");
            var jsons = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly);
            var stamp = $"{jsons.Length}|{(jsons.Length == 0 ? 0 : jsons.Max(f => File.GetLastWriteTimeUtc(f).Ticks))}";
            if (entries.TryGetValue(modDir, out var cached) && cached.Stamp == stamp) return cached.Paths;

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in jsons)
            {
                var name = Path.GetFileName(file);
                if (name.Equals("default_mod.json", StringComparison.OrdinalIgnoreCase))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
                    AddContainer(doc.RootElement, paths);
                }
                else if (name.StartsWith("group_", StringComparison.OrdinalIgnoreCase))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
                    foreach (var list in new[] { "Options", "Containers" })
                        if (doc.RootElement.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array)
                            foreach (var item in items.EnumerateArray())
                                AddContainer(item, paths);
                }
            }
            entries[modDir] = new Entry(stamp, paths);
            Plugin.Log.Debug($"Mod {modDir} can redirect {paths.Count} game path(s).");
            return paths;
        }
        catch (Exception ex)
        {
            return Unknown(modDir, ex.Message);
        }
    }

    private HashSet<string>? Unknown(string modDir, string why)
    {
        entries.Remove(modDir);
        if (warned.Add(modDir)) Plugin.Log.Information($"Can't read which files mod {modDir} redirects ({why}); treating its changes as skin changes.");
        return null;
    }

    private static void AddContainer(JsonElement container, HashSet<string> paths)
    {
        if (container.ValueKind != JsonValueKind.Object) return;
        foreach (var key in new[] { "Files", "FileSwaps" })
            if (container.TryGetProperty(key, out var map) && map.ValueKind == JsonValueKind.Object)
                foreach (var entry in map.EnumerateObject())
                    paths.Add(Normalize(entry.Name));
    }

    /// Game paths as Penumbra and the plan compare them: lower case, forward slashes.
    public static string Normalize(string gamePath) => gamePath.Replace('\\', '/').ToLowerInvariant();
}
