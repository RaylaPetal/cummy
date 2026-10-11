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
    public static readonly IReadOnlyList<BodyPart> Parts = BodyParts.DisplayOrder.Where(p => !p.IsFacial()).ToList();

    private readonly PenumbraIpc penumbra;
    private readonly ProteusIpc proteus;
    private readonly OverlaySources overlaySources;
    private readonly CompositeCache cache;
    private readonly Configuration configuration;
    private readonly LayerState layers;

    private string? modDir;
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

    public OverlayPackService(PenumbraIpc penumbra, ProteusIpc proteus, OverlaySources overlaySources, CompositeCache cache,
                              Configuration configuration, LayerState layers)
    {
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
        if (!EnsureMod()) return "Can't set up the Cummy Layers mod in Penumbra";
        wantedCollection = collection;
        wantedLayers = layered;

        // Each option carries its part's current stage; a clean part's carries stage 1, what its first layer shows.
        var sources = Parts.ToDictionary(p => p, p => overlaySources.Resolve(p, Math.Max(1, layers.Stage(p))));
        var looks = Parts.ToDictionary(p => p, configuration.ResolveLook);
        var signature = string.Join("|", bodyMaterials) + "#"
                      + string.Join("|", Parts.Select(p => $"{p.Id()}={sources[p].Key}~{looks[p].Key}"));
        if (signature != writtenSignature)
        {
            if (renderingSignature != signature)
            {
                renderingSignature = signature;
                var dir = modDir!;
                Task.Run(() => WritePack(dir, sources, looks, bodyMaterials)).ContinueWith(t => Plugin.Framework.RunOnFrameworkThread(() =>
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
                    Select(wantedCollection, wantedLayers);
                }));
            }
            return "Preparing layers for Proteus...";
        }

        Select(collection, layered);
        return layered.Count == 0 ? "Clean" : $"Showing {layered.Count} body layer(s) via Proteus";
    }

    /// Leaving Proteus mode: nothing selected (and Proteus asked to recomposite, if it is still there).
    public void Clear(Guid collection)
    {
        wantedCollection = collection;
        wantedLayers = [];
        if (!EnsureMod()) return;
        Select(collection, []);
    }

    private void Select(Guid collection, IReadOnlyList<BodyPart> layered)
    {
        var names = Parts.Where(layered.Contains).Select(p => p.DisplayName()).ToList();
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

    private bool EnsureMod()
    {
        if (modDir == null)
        {
            if (penumbra.GetModDirectory() is not { } root) return false;
            modDir = Path.Combine(root, ModFolder);
        }
        try
        {
            if (!File.Exists(Path.Combine(modDir, "meta.json")) || !registered)
                WriteMeta(modDir);
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
        return true;
    }

    /// Penumbra's meta.json: no redirects of its own (one no-op swap, as Proteus-made packs have), and the
    /// multi-select group whose options Proteus reads.
    private static void WriteMeta(string dir)
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
                    ["Options"] = Parts.Select(p => new Dictionary<string, object>
                    {
                        ["Name"] = p.DisplayName(),
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
                           IReadOnlyDictionary<BodyPart, LayerLook> looks, IReadOnlyList<string> bodyMaterials)
    {
        lock (packLock)
            WritePackLocked(dir, sources, looks, bodyMaterials);
    }

    private void WritePackLocked(string dir, IReadOnlyDictionary<BodyPart, OverlaySource> sources,
                                 IReadOnlyDictionary<BodyPart, LayerLook> looks, IReadOnlyList<string> bodyMaterials)
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

    /// One sidecar option: the part's descriptor, plus the look's Row 16 (what Proteus uses for an overlay without an
    /// index map) and skin-tone strength. A default look adds nothing, so the sidecar is the same as without looks.
    private static Dictionary<string, object> Option(BodyPart part, string diffuse, string? normal,
                                                     IReadOnlyList<string> bodyMaterials, LayerLook look)
    {
        var descriptor = new Dictionary<string, object>
        {
            ["Layer"] = "Skin",
            ["ManualShaderLock"] = false,
            ["MaterialGamePath"] = bodyMaterials,
            ["Diffuse"] = $"art/{part.Id()}/{diffuse}",
            ["GenerateDiffuse"] = false,
            ["SourceBodyType"] = "bibo",
            ["ToeCapDensity"] = 0,
        };
        if (normal != null)
        {
            descriptor["Normal"] = $"art/{part.Id()}/{normal}";
            descriptor["NormalMode"] = "Compound";
        }
        descriptor["Mask"] = $"art/{part.Id()}/mask.png";
        if (look.SkinTone > 0) descriptor["SkinToneMask"] = 1f - look.SkinTone / 100f;

        var option = new Dictionary<string, object> { ["Name"] = part.DisplayName(), ["Overlays"] = new[] { descriptor } };
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
