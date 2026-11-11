using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cummy.Compositor;
using Cummy.Ipc;
using Cummy.State;
using Cummy.ThirdParty.Proteus;

namespace Cummy.Proteus;

/// Cummy's body layers as a Proteus overlay pack: a normal Penumbra mod ("Cummy Layers") whose multi-select option
/// group "Cummy layers" has one option per body part, plus a `Proteus/metadata.json` sidecar mapping each option to
/// that part's art. Selecting the layered parts and asking Proteus to recomposite lets Proteus layer skin, its own
/// overlays and Cummy's layers in one pass, instead of the two plugins competing for the skin textures.
/// Format: github.com/solona-m/proteus (Models/ProteusMetadata.cs, OverlayDescriptor.cs, SidecarDiscoveryService.cs).
/// Framework thread, except the art rendering it starts itself.
public sealed class OverlayPackService
{
    public const string ModFolder = "CummyLayers";
    public const string GroupName = "Cummy layers";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// The parts Proteus composites (facial layers stay with Cummy's own compositor).
    public static readonly IReadOnlyList<BodyPart> Parts = BodyParts.Regional;

    private readonly PenumbraIpc penumbra;
    private readonly ProteusIpc proteus;
    private readonly OverlaySources overlaySources;
    private readonly CompositeCache cache;
    private readonly Configuration configuration;
    private readonly LayerState layers;
    private readonly PluginOverlays pluginOverlays;

    private string? modDir;
    // The plugin options meta.json was last written with (null = not yet this session).
    private string? writtenOptionNames;
    // What each plugin overlay's option was last written with, by overlay id.
    private readonly Dictionary<string, (string Key, Dictionary<string, object>? Option)> writtenPlugins = new();
    private bool registered;
    // What each part's option was last written with, so a change (e.g. one part's stage) rewrites only that part.
    // Pack writes run off-thread and may overlap; they take this lock.
    private readonly object packLock = new();
    private readonly Dictionary<BodyPart, (string Key, Dictionary<string, object>? Option)> writtenParts = new();
    private string? writtenSignature;
    private string? renderingSignature;
    private string? lastSelection;
    private bool artChanged;
    // The latest request, applied when a background art write finishes.
    private Guid wantedCollection;
    private IReadOnlyList<BodyPart> wantedLayers = [];
    private IReadOnlyList<PluginOverlay> wantedPlugins = [];

    public OverlayPackService(PenumbraIpc penumbra, ProteusIpc proteus, OverlaySources overlaySources, CompositeCache cache,
                              Configuration configuration, LayerState layers, PluginOverlays pluginOverlays)
    {
        this.pluginOverlays = pluginOverlays;
        this.configuration = configuration;
        this.layers = layers;
        this.penumbra = penumbra;
        this.proteus = proteus;
        this.overlaySources = overlaySources;
        this.cache = cache;
    }

    /// Shows exactly <paramref name="layered"/> (body parts) through Proteus on the wearer's
    /// <paramref name="bodyMaterials"/>. Art is (re)written in the background first when it changed.
    /// Returns a short status for the main window.
    public string Apply(Guid collection, IReadOnlyList<BodyPart> layered, IReadOnlyList<string> bodyMaterials)
    {
        // Other plugins' body overlays ride along as options of their own, after the parts (so above Cummy's layers).
        var plugins = pluginOverlays.Showing.Where(e => e.Part is not { } p || !p.IsFacial()).ToList();
        if (!EnsureMod(plugins)) return "Can't set up the Cummy Layers mod in Penumbra";
        wantedCollection = collection;
        wantedLayers = layered;
        wantedPlugins = plugins;

        // Each option carries its part's current stage; a clean part's carries stage 1, what its first layer shows.
        var sources = Parts.ToDictionary(p => p, p => overlaySources.Resolve(p, Math.Max(1, layers.Stage(p))));
        var looks = Parts.ToDictionary(p => p, configuration.ResolveLook);
        var signature = string.Join("|", bodyMaterials) + "#"
                      + string.Join("|", Parts.Select(p => $"{p.Id()}={sources[p].Key}~{looks[p].Key}"))
                      + "#" + string.Join("|", plugins.Select(e => $"{e.Id}={e.Stamp}%{e.Opacity}"));
        if (signature != writtenSignature)
        {
            if (renderingSignature != signature)
            {
                renderingSignature = signature;
                var dir = modDir!;
                Task.Run(() => WritePack(dir, sources, looks, bodyMaterials, plugins)).ContinueWith(t => Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (renderingSignature != signature) return;   // superseded
                    renderingSignature = null;
                    if (t.IsFaulted)
                    {
                        Plugin.Log.Error(t.Exception!, "Writing the Cummy Layers pack failed.");
                        return;
                    }
                    writtenSignature = signature;
                    // Proteus recomposites on its own when the options change, but not when only the sidecar's art did.
                    artChanged = true;
                    Select(wantedCollection, wantedLayers, wantedPlugins);
                }));
            }
            return "Preparing layers for Proteus...";
        }

        Select(collection, layered, plugins);
        var shown = plugins.Count == 0 ? "" : $" and {plugins.Count} plugin overlay(s)";
        return layered.Count == 0 && plugins.Count == 0 ? "Clean" : $"Showing {layered.Count} body layer(s){shown} via Proteus";
    }

    /// Leaving Proteus mode: nothing selected (and Proteus asked to recomposite, if it is still there).
    public void Clear(Guid collection)
    {
        wantedCollection = collection;
        wantedLayers = [];
        wantedPlugins = [];
        if (!EnsureMod([])) return;
        Select(collection, [], []);
    }

    private void Select(Guid collection, IReadOnlyList<BodyPart> layered, IReadOnlyList<PluginOverlay> plugins)
    {
        var names = Parts.Where(layered.Contains).Select(p => p.DisplayName())
                         .Concat(plugins.Where(e => writtenPlugins.TryGetValue(e.Id, out var w) && w.Option != null)
                                        .Select(OptionName))
                         .ToList();
        var selection = $"{collection}:{string.Join(",", names)}";
        if (selection != lastSelection)
        {
            if (!penumbra.EnableMod(collection, ModFolder, 0) || !penumbra.SetModOptions(collection, ModFolder, GroupName, names))
                return;
            lastSelection = selection;
            Plugin.Log.Information($"Cummy Layers: selected [{string.Join(", ", names)}].");
        }
        if (artChanged)
        {
            artChanged = false;
            Plugin.Log.Information("Cummy Layers art changed; asking Proteus to recomposite.");
            proteus.Recomposite();
        }
    }

    // ── The pack on disk ─────────────────────────────────────────────────────

    /// Penumbra's name for a plugin overlay's option.
    private static string OptionName(PluginOverlay e) => $"Plugin: {e.Id}";

    private bool EnsureMod(IReadOnlyList<PluginOverlay> plugins)
    {
        if (modDir == null)
        {
            if (penumbra.GetModDirectory() is not { } root) return false;
            modDir = Path.Combine(root, ModFolder);
        }
        // Plugin options change the option list: rewrite it and have Penumbra re-read it (selection follows by name).
        var pluginNames = plugins.Select(OptionName).ToList();
        var optionNames = string.Join("|", pluginNames);
        var namesChanged = writtenOptionNames != null && writtenOptionNames != optionNames;
        try
        {
            if (!File.Exists(Path.Combine(modDir, "meta.json")) || !registered || namesChanged)
                WriteMeta(modDir, pluginNames);
            writtenOptionNames = optionNames;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Could not write {modDir}");
            return false;
        }
        if (!registered)
        {
            penumbra.AddMod(ModFolder);   // already known after the first session: Penumbra just says so
            penumbra.ReloadMod(ModFolder);
            registered = true;
        }
        else if (namesChanged)
        {
            penumbra.ReloadMod(ModFolder);
            lastSelection = null;   // option indices moved: select again by name
        }
        return true;
    }

    /// Penumbra's meta.json: no redirects of its own (one no-op swap, as Proteus-made packs have), and the
    /// multi-select group whose options Proteus reads.
    private static void WriteMeta(string dir, IReadOnlyList<string> pluginOptions)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "meta.json");
        var meta = new Dictionary<string, object?>
        {
            ["FileVersion"] = 4,
            ["Identifier"] = ReadIdentifier(path) ?? Guid.NewGuid().ToString(),
            ["Name"] = "Cummy Layers",
            ["Author"] = "Cummy plugin",
            ["Description"] = "Managed by the Cummy plugin: your body layers, composited by Proteus. Options are set "
                            + "automatically - edits here are overwritten.",
            ["Version"] = "",
            ["Website"] = "",
            ["ModTags"] = Array.Empty<string>(),
            ["DefaultData"] = new Dictionary<string, object>
            {
                ["Files"] = new Dictionary<string, string>(),
                ["FileSwaps"] = new Dictionary<string, string>
                {
                    ["chara/monster/m8030/obj/body/b0001/material/v0001/mt_m8030b0001_a.mtrl"] =
                        "chara/monster/m8030/obj/body/b0001/material/v0001/mt_m8030b0001_a.mtrl",
                },
                ["Manipulations"] = Array.Empty<object>(),
            },
            ["Groups"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["Type"] = "Multi",
                    ["Name"] = GroupName,
                    ["Description"] = "Set by Cummy to your layered body parts.",
                    ["Priority"] = 0,
                    ["DefaultSettings"] = 0,
                    ["Options"] = Parts.Select(p => p.DisplayName()).Concat(pluginOptions).Select(name => new Dictionary<string, object>
                    {
                        ["Name"] = name,
                        ["Description"] = "",
                        ["Priority"] = 0,
                        ["Files"] = new Dictionary<string, string>(),
                        ["FileSwaps"] = new Dictionary<string, string>(),
                        ["Manipulations"] = Array.Empty<object>(),
                    }).ToArray(),
                },
            },
        };
        WriteAtomic(path, JsonSerializer.Serialize(meta, Json));
    }

    /// Each changed part's art (shipped full art copied as-is; custom, full-body-split and stage-cut art rendered) and
    /// the Proteus sidecar.
    private void WritePack(string dir, IReadOnlyDictionary<BodyPart, OverlaySource> sources,
                           IReadOnlyDictionary<BodyPart, LayerLook> looks, IReadOnlyList<string> bodyMaterials,
                           IReadOnlyList<PluginOverlay> plugins)
    {
        lock (packLock)
            WritePackLocked(dir, sources, looks, bodyMaterials, plugins);
    }

    private void WritePackLocked(string dir, IReadOnlyDictionary<BodyPart, OverlaySource> sources,
                                 IReadOnlyDictionary<BodyPart, LayerLook> looks, IReadOnlyList<string> bodyMaterials,
                                 IReadOnlyList<PluginOverlay> plugins)
    {
        var options = new List<object>();
        var materials = string.Join("|", bodyMaterials);
        foreach (var part in Parts)
        {
            var source = sources[part];
            var artDir = Path.Combine(dir, "Proteus", "art", part.Id());
            var key = $"{dir}#{materials}#{source.Key}~{looks[part].Key}";
            if (writtenParts.TryGetValue(part, out var written) && written.Key == key && Directory.Exists(artDir))
            {
                if (written.Option != null) options.Add(written.Option);
                continue;
            }
            writtenParts.Remove(part);
            Directory.CreateDirectory(artDir);
            foreach (var old in Directory.EnumerateFiles(artDir)) File.Delete(old);

            string? diffuse = null, normal = null;
            RgbaImage? colour;
            lock (cache.Lock)
            {
                colour = cache.Art(part, source, OverlaySources.DiffuseFile);
                if (source.Origin == OverlayOrigin.Shipped && !source.IsStageCut)
                {
                    diffuse = Copy(source.Diffuse, artDir, "diffuse");
                    if (source.Normal != null && File.Exists(source.Normal)) normal = Copy(source.Normal, artDir, "normal");
                }
                else
                {
                    if (colour != null)
                    {
                        PngWriter.Write(colour, Path.Combine(artDir, "diffuse.png"));
                        diffuse = "diffuse.png";
                    }
                    if (cache.Art(part, source, OverlaySources.NormalFile) is { } n)
                    {
                        PngWriter.Write(n, Path.Combine(artDir, "normal.png"));
                        normal = "normal.png";
                    }
                }
            }
            if (diffuse == null || colour == null)
            {
                writtenParts[part] = (key, null);
                continue;
            }

            // The wet look: Cummy's gloss (red = specular, green = roughness) wherever the art covers, as a Proteus
            // mask overlay (the spike showed Proteus applies it as shine), as strong as the look's Shine.
            PngWriter.Write(GlossMask(colour, looks[part].Shine), Path.Combine(artDir, "mask.png"));
            var option = Option(part, diffuse, normal, bodyMaterials, looks[part]);
            options.Add(option);
            writtenParts[part] = (key, option);
            Plugin.Log.Debug($"Cummy Layers: wrote {part.Id()} ({source.Key}).");
        }

        WritePluginOptions(dir, materials, bodyMaterials, plugins, options);

        var sidecar = new Dictionary<string, object>
        {
            ["FormatVersion"] = 1,
            ["Name"] = "Cummy Layers",
            ["Author"] = "Cummy plugin",
            ["OptionGroups"] = new[] { new Dictionary<string, object> { ["PenumbraGroupName"] = GroupName, ["Options"] = options } },
        };
        WriteAtomic(Path.Combine(dir, "Proteus", "metadata.json"), JsonSerializer.Serialize(sidecar, Json));
        Plugin.Log.Information($"Cummy Layers pack written for {string.Join(", ", bodyMaterials)}.");
    }

    /// Other plugins' overlays, one option each after the parts: the whole image (a full-body image is already in the
    /// Bibo layout; a part's image is used whole, like a part override), its opacity in the alpha, full shine, no look.
    private void WritePluginOptions(string dir, string materials, IReadOnlyList<string> bodyMaterials,
                                    IReadOnlyList<PluginOverlay> plugins, List<object> options)
    {
        foreach (var gone in writtenPlugins.Keys.Where(id => plugins.All(e => e.Id != id)).ToList())
            writtenPlugins.Remove(gone);
        foreach (var e in plugins)
        {
            var folder = "plugin-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(e.Id.ToLowerInvariant())))[..12].ToLowerInvariant();
            var artDir = Path.Combine(dir, "Proteus", "art", folder);
            var key = $"{dir}#{materials}#{e.Stamp}%{e.Opacity}";
            if (writtenPlugins.TryGetValue(e.Id, out var written) && written.Key == key && Directory.Exists(artDir))
            {
                if (written.Option != null) options.Add(written.Option);
                continue;
            }
            writtenPlugins.Remove(e.Id);
            Directory.CreateDirectory(artDir);
            foreach (var old in Directory.EnumerateFiles(artDir)) File.Delete(old);

            // Whole image: a Part-origin source is never split by the region map.
            var source = new OverlaySource(OverlayOrigin.Part, e.Diffuse, e.Normal, e.Stamp);
            RgbaImage? colour, relief;
            lock (cache.Lock)
            {
                colour = cache.Art(BodyPart.Chest, source, OverlaySources.DiffuseFile);
                relief = cache.Art(BodyPart.Chest, source, OverlaySources.NormalFile);
            }
            if (colour == null)
            {
                writtenPlugins[e.Id] = (key, null);
                continue;
            }
            Fade(colour, e.Opacity);
            PngWriter.Write(colour, Path.Combine(artDir, "diffuse.png"));
            string? normal = null;
            if (relief != null)
            {
                Fade(relief, e.Opacity);
                PngWriter.Write(relief, Path.Combine(artDir, "normal.png"));
                normal = "normal.png";
            }
            PngWriter.Write(GlossMask(colour, 100), Path.Combine(artDir, "mask.png"));
            var option = Descriptor(OptionName(e), $"art/{folder}", "diffuse.png", normal, bodyMaterials, skinToneMask: null);
            options.Add(option);
            writtenPlugins[e.Id] = (key, option);
            Plugin.Log.Debug($"Cummy Layers: wrote plugin overlay {e.Id}.");
        }
    }

    /// Scales the image's alpha to <paramref name="opacity"/> percent, in place (art from the cache is a fresh copy).
    private static void Fade(RgbaImage image, int opacity)
    {
        if (opacity >= 100) return;
        var rgba = image.Rgba;
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = (byte)(rgba[i] * opacity / 100);
    }

    /// One sidecar option with a single Skin overlay from <paramref name="artDir"/> (relative to Proteus/).
    private static Dictionary<string, object> Descriptor(string name, string artDir, string diffuse, string? normal,
                                                         IReadOnlyList<string> bodyMaterials, float? skinToneMask)
    {
        var descriptor = new Dictionary<string, object>
        {
            ["Layer"] = "Skin",
            ["ManualShaderLock"] = false,
            ["MaterialGamePath"] = bodyMaterials,
            ["Diffuse"] = $"{artDir}/{diffuse}",
            ["GenerateDiffuse"] = false,
            ["SourceBodyType"] = "bibo",
            ["ToeCapDensity"] = 0,
        };
        if (normal != null)
        {
            descriptor["Normal"] = $"{artDir}/{normal}";
            descriptor["NormalMode"] = "Compound";
        }
        descriptor["Mask"] = $"{artDir}/mask.png";
        if (skinToneMask is { } tone) descriptor["SkinToneMask"] = tone;
        return new Dictionary<string, object> { ["Name"] = name, ["Overlays"] = new[] { descriptor } };
    }

    /// One sidecar option: the part's descriptor, plus the look's Row 16 (what Proteus uses for an overlay without an
    /// index map) and skin-tone strength. A default look adds nothing, so the sidecar is the same as without looks.
    private static Dictionary<string, object> Option(BodyPart part, string diffuse, string? normal,
                                                     IReadOnlyList<string> bodyMaterials, LayerLook look)
    {
        var option = Descriptor(part.DisplayName(), $"art/{part.Id()}", diffuse, normal, bodyMaterials,
                                look.SkinTone > 0 ? 1f - look.SkinTone / 100f : null);
        if (!look.IsWhite || look.EffectiveOpacity != 0)
        {
            var (r, g, b) = look.TintRgb();
            option["ColorTableRows"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["Row"] = 16,
                    ["SubRowA"] = new Dictionary<string, object> { ["Diffuse"] = $"#{r:X2}{g:X2}{b:X2}", ["Opacity"] = look.EffectiveOpacity },
                },
            };
        }
        return option;
    }

    private static RgbaImage GlossMask(RgbaImage colour, int shine)
    {
        var src = colour.Rgba;
        var mask = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            if (src[i + 3] == 0) continue;
            mask[i] = CompositorService.GlossSpecular;
            mask[i + 1] = CompositorService.GlossRoughness;
            // Proteus writes every colour channel of a mask overlay: keep the skin's own blue, not 0.
            mask[i + 2] = CompositorService.SkinMaskBlue;
            mask[i + 3] = (byte)(src[i + 3] * shine / 100);
        }
        return new RgbaImage(mask, colour.Width, colour.Height);
    }

    private static string Copy(string from, string artDir, string name)
    {
        var file = name + Path.GetExtension(from).ToLowerInvariant();
        File.Copy(from, Path.Combine(artDir, file), overwrite: true);
        return file;
    }

    private static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Path.GetRandomFileName() + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    private static string? ReadIdentifier(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("Identifier", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
