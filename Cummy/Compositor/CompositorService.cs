using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cummy.Ipc;
using Cummy.Proteus;
using Cummy.State;
using Cummy.ThirdParty.Proteus;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;

namespace Cummy.Compositor;

/// Renders the layer state onto the local player's Bibo skin and publishes it through a managed Penumbra mod
/// ("Cummy", in Penumbra's mod folder, enabled in the player's collection) - Proteus's approach. Planning
/// (Penumbra/game queries) runs on the framework thread; decoding, blending and encoding run on a background task.
/// A newer state cancels a run in flight. After every manifest change we wait until Penumbra actually resolves the
/// skin to the new file before redrawing, or the redraw can load the old skin.
public sealed class CompositorService : IDisposable
{
    private const int ModPriority = 9999;

    /// Bump when compositing or the shipped art changes, so cached outputs from an older build are not reused.
    private const int CompositeVersion = 5;

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// How long to wait for Penumbra to serve a rewritten manifest before redrawing anyway.
    private static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(3);

    /// While our overlay is published, Penumbra answers every skin path with our own file, so a skin mod the wearer
    /// switched to is invisible. On a mod-setting change we empty the mod, let Penumbra rebuild, and read the real
    /// upstream (Proteus's "prime"). How long to wait for Penumbra to stop answering with our file.
    private static readonly TimeSpan ProbeSettle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);


    /// Cached skins and overlays are released after this long without a composite (they hold a few hundred MB).
    private static readonly TimeSpan CacheIdle = TimeSpan.FromMinutes(10);

    /// Vanilla face textures are 256×512: too small for facial art, so face bases are upscaled to at least this width.
    private const int MinFaceWidth = 1024;

    /// The body-skin materials (Bibo, gen3, gen2/vanilla, Eve): Proteus' own BodySuffixes. Other materials under the body
    /// folder (pubes, piercings, nails) have no diffuse or another texture layout, and Proteus warns on each one.
    private static readonly string[] BodySkinSuffixes = ["_bibo.mtrl", "_b.mtrl", "_a.mtrl", "_eve.mtrl"];

    private static readonly Regex BodyMaterial = new(@"^chara/human/c\d{4}/obj/body/b\d{4}/material/.*\.mtrl$",
                                                     RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FaceMaterial = new(@"^chara/human/(c\d{4})/obj/face/(f\d{4})/material/[^/]*_fac_[^/]*\.mtrl$",
                                                     RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Gloss tuning (layer-look design D7). Mask red = specular, green = roughness (lower is shinier); a Bibo skin sits
    // at about 232 / 58, so the target is well past it. Its strength and the skin-tone suppression come from the
    // wearer's layer look. Blue is the skin's own (153 on Bibo), kept under layers.
    internal const byte GlossSpecular = 255;
    internal const byte GlossRoughness = 16;
    internal const byte SkinMaskBlue = 153;

    private enum SlotKind { Diffuse, Normal, Mask }

    /// <see cref="Stamp"/> identifies the upstream file's content (path, size, write time): the skin cache key.
    private sealed record Slot(SlotKind Kind, string GamePath, string? Upstream, string Stamp, string OutFile);

    /// A face material's model: maps face-paint-layout art onto that face's texture (FaceBaker).
    private sealed record FacePlan(string ModelPath, string? ModelDisk, string Stamp);

    /// One material to composite: its textures, the layered parts that go onto it, and for a face its model.
    private sealed record MaterialPlan(Slot? Diffuse, Slot? Normal, Slot? Mask, IReadOnlyList<BodyPart> Parts, FacePlan? Face)
    {
        public IEnumerable<Slot> Slots => new[] { Diffuse, Normal, Mask }.Where(s => s != null)!;
    }

    private sealed record Plan(
        Guid Collection, IReadOnlyList<BodyPart> Parts, IReadOnlyDictionary<BodyPart, OverlaySource> Sources,
        IReadOnlyDictionary<BodyPart, LayerLook> Looks, IReadOnlyList<MaterialPlan> Materials, string Signature);

    /// Waiting for Penumbra to serve (or stop serving) our file for <see cref="GamePath"/>, then redraw.
    private sealed record PendingLive(string GamePath, string? ExpectedFile, DateTime Deadline);

    private readonly PenumbraIpc penumbra;
    private readonly LayerState layers;
    private readonly IDataManager data;
    private readonly OverlaySources overlaySources;
    private readonly CompositeCache cache;
    /// Art changes whose cached overlays could not be freed at once because a composite held the cache.
    private readonly ConcurrentQueue<OverlayTarget> staleArt = new();

    // Known once Penumbra reports its mod root.
    private string? modDir;
    private string? outDir;
    private UpstreamResolver? upstream;
    private bool modRegistered;
    private bool legacyCleanupDone;
    private readonly HashSet<Guid> enabledIn = new();

    private DateTime? dueAt;
    private CancellationTokenSource? running;
    private PendingLive? pendingLive;

    /// Collection the managed mod currently carries redirects for (null = manifest empty).
    private Guid? publishedCollection;
    private Dictionary<string, string> publishedFiles = new(StringComparer.OrdinalIgnoreCase);
    private string? lastUnpublishedPath;
    private string? lastSignature;

    private bool warnedBibo;
    private bool warnedPenumbra;
    private bool warnedStacked;

    private bool probeRequested;
    private DateTime? probeStartedAt;

    /// Last run's outcome for the main window.
    public string Status { get; private set; } = "Idle";

    /// Who composites body layers: Proteus (as an overlay pack) or Cummy itself. Null until first planned.
    public bool? BodyViaProteus { get; private set; }

    /// The overlay pack's state in Proteus mode, for the main window.
    public string? ProteusStatus { get; private set; }

    private readonly Configuration configuration;
    private readonly ProteusIpc proteus;
    private readonly OverlayPackService pack;

    public CompositorService(PenumbraIpc penumbra, LayerState layers, IDataManager data, OverlaySources overlaySources,
                             Configuration configuration, ProteusIpc proteus)
    {
        this.penumbra = penumbra;
        this.layers = layers;
        this.data = data;
        this.overlaySources = overlaySources;
        this.configuration = configuration;
        this.proteus = proteus;
        cache = new CompositeCache(overlaySources.RegionMapPath, overlaySources.FaceRegionMapPath);
        pack = new OverlayPackService(penumbra, proteus, overlaySources, cache, configuration, layers);

        // Older builds wrote their textures here and published a temporary mod; both are superseded.
        try
        {
            var legacy = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "out");
            if (Directory.Exists(legacy)) Directory.Delete(legacy, recursive: true);
        }
        catch { /* best effort */ }

        layers.Changed += OnLayersChanged;
        overlaySources.Changed += OnOverlayChanged;
        proteus.AvailabilityChanged += Schedule;
        penumbra.Ready += Schedule;
        penumbra.ModSettingsChanged += OnModSettingsChanged;
        penumbra.PlayerRedrawn += Schedule;
        Plugin.ClientState.Logout += OnLogout;
        Plugin.Framework.Update += OnUpdate;

        Schedule();
    }

    public void Dispose()
    {
        layers.Changed -= OnLayersChanged;
        overlaySources.Changed -= OnOverlayChanged;
        proteus.AvailabilityChanged -= Schedule;
        penumbra.Ready -= Schedule;
        penumbra.ModSettingsChanged -= OnModSettingsChanged;
        penumbra.PlayerRedrawn -= Schedule;
        Plugin.ClientState.Logout -= OnLogout;
        Plugin.Framework.Update -= OnUpdate;

        running?.Cancel();
        pendingLive = null;
        // Leave the managed mod empty so the skin is bare while the plugin is off.
        if (Unpublish()) penumbra.RedrawPlayer();
    }

    private void Note(string text)
    {
        Plugin.Log.Information(text);
    }

    private void OnLayersChanged()
    {
        warnedBibo = false;
        warnedPenumbra = false;
        Schedule();
    }

    public void Schedule() => dueAt = DateTime.UtcNow + Debounce;

    /// The wearer changed some custom art. Recomposite; the plan signature carries each layered part's art, so this
    /// republishes only when a layered part's art changed.
    private void OnOverlayChanged(OverlayTarget target)
    {
        Note($"{target.DisplayName} overlay art changed.");
        if (Monitor.TryEnter(cache.Lock))
        {
            try
            {
                cache.Forget(target);
            }
            finally
            {
                Monitor.Exit(cache.Lock);
            }
        }
        else
        {
            staleArt.Enqueue(target);   // freed by the next composite
        }
        Schedule();
    }

    private void ScheduleRetry() => dueAt = DateTime.UtcNow + RetryDelay;

    private void OnModSettingsChanged(Guid collection)
    {
        // Only the collection we publish into (or, before the first publish, any) can change the wearer's skin.
        if (publishedCollection is { } published && published != collection) return;
        probeRequested = true;
        Schedule();
    }

    private void OnLogout(int type, int code)
    {
        running?.Cancel();
        pendingLive = null;
        Unpublish();
        lastSignature = null;
        ReleaseCache("logged out");
    }

    /// Drops cached skins and overlays unless a composite is using them right now.
    private void ReleaseCache(string why)
    {
        if (cache.IsEmpty || !Monitor.TryEnter(cache.Lock)) return;
        try
        {
            cache.Clear();
            Note($"Released cached skin and overlays ({why}).");
        }
        finally
        {
            Monitor.Exit(cache.Lock);
        }
    }

    private void OnUpdate(IFramework framework)
    {
        if (!cache.IsEmpty && DateTime.UtcNow - cache.LastUsedUtc > CacheIdle)
            ReleaseCache($"idle for {CacheIdle.TotalMinutes:0} minutes");

        if (pendingLive != null)
        {
            PollLive();
            return;   // nothing else runs until Penumbra has caught up with the last manifest
        }
        if (dueAt is not { } due || DateTime.UtcNow < due) return;
        dueAt = null;
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
        {
            if (Plugin.ClientState.IsLoggedIn) ScheduleRetry();
            return;
        }
        try
        {
            Start();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Composite planning failed.");
            Status = "Failed: " + ex.Message;
            Note(Status);
        }
    }

    // ── Planning (framework thread) ──────────────────────────────────────────

    private void Start()
    {
        var parts = layers.Snapshot();
        var bodyParts = parts.Where(p => !p.IsFacial()).ToList();
        var faceParts = parts.Where(p => p.IsFacial()).ToList();

        if (!penumbra.IsAvailable)
        {
            Status = "Waiting for Penumbra";
            if (parts.Count > 0 && !warnedPenumbra)
            {
                warnedPenumbra = true;
                Notifier.Error("Penumbra isn't available - your layers will show once it loads.");
            }
            return;
        }

        if (!EnsureManagedMod())
        {
            Status = "Can't set up the Cummy mod in Penumbra";
            ScheduleRetry();
            return;
        }

        if (penumbra.GetPlayerCollectionId() is not { } collection)
        {
            ScheduleRetry();
            return;
        }
        if (!legacyCleanupDone)
        {
            legacyCleanupDone = true;
            penumbra.RemoveLegacyTemporaryMod(collection);
        }

        // With Proteus loaded, body layers go through it as an overlay pack so the two never compete for the skin
        // textures; facial layers stay with Cummy either way.
        var viaProteus = proteus.Refresh() && !configuration.UseOwnCompositorWithProteus;
        if (viaProteus != BodyViaProteus)
        {
            if (BodyViaProteus == true) pack.Clear(collection);
            if (BodyViaProteus != null) Note(viaProteus ? "Body layers now go through Proteus." : "Body layers now use Cummy's own compositor.");
            BodyViaProteus = viaProteus;
            ProteusStatus = null;
            lastSignature = null;   // re-plan: body layers move between paths
        }
        if (viaProteus)
        {
            var active = penumbra.GetActivePlayerMaterialPaths();
            var bodyMaterials = active?.Where(m => BodyMaterial.IsMatch(m)
                                                   && BodySkinSuffixes.Any(s => m.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                                      .OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
            if (bodyParts.Count > 0 && bodyMaterials.Count == 0)
            {
                ProteusStatus = "Waiting for your character to finish loading";
                ScheduleRetry();
            }
            else
            {
                ProteusStatus = pack.Apply(collection, bodyParts, bodyMaterials);
            }
            bodyParts = [];   // Cummy composites only the face
            parts = faceParts;
        }

        if (parts.Count == 0)
        {
            running?.Cancel();
            lastSignature = "empty";
            Status = "Clean";
            if (Unpublish())
            {
                Note("All clean - emptied the Cummy mod.");
                RedrawWhenLive(lastUnpublishedPath, null);
            }
            Prune();
            return;
        }

        if (probeRequested && publishedCollection != null)
        {
            // Empty the mod (no redraw: the loaded textures stay on screen) so the next plan sees the wearer's
            // real skin; Publish puts it back.
            probeRequested = false;
            probeStartedAt = DateTime.UtcNow;
            running?.Cancel();
            Unpublish();
            dueAt = DateTime.UtcNow + ProbeSettle;
            Status = "Checking your skin...";
            Note("Mod settings changed - re-reading the skin underneath.");
            return;
        }
        probeRequested = false;

        var materials = penumbra.GetActivePlayerMaterialPaths();
        if (materials == null || !materials.Any(m => m.Contains("chara/human/", StringComparison.OrdinalIgnoreCase)))
        {
            // Mid-redraw or still loading: no body materials yet. Not the same as "not on Bibo".
            Status = "Waiting for your character to finish loading";
            ScheduleRetry();
            return;
        }

        // Body parts go onto the Bibo body; facial parts onto the face, whatever the body.
        var bibo = bodyParts.Count == 0 ? [] : materials.Where(m => m.EndsWith("_bibo.mtrl", StringComparison.OrdinalIgnoreCase))
                                                       .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                                                       .ToList();
        var faces = faceParts.Count == 0 ? [] : materials.Where(m => FaceMaterial.IsMatch(m))
                                                        .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                                                        .ToList();
        var noBibo = bodyParts.Count > 0 && bibo.Count == 0;
        if (noBibo)
        {
            Note($"No *_bibo.mtrl among {materials.Count} loaded materials - not showing body layers.");
            if (!warnedBibo)
            {
                warnedBibo = true;
                Notifier.Error(faceParts.Count > 0
                    ? "A Bibo body is required to show body layers - your facial layers still show."
                    : "A Bibo body is required to show layers - they'll appear when you're on one.");
            }
        }
        if (faceParts.Count > 0 && faces.Count == 0)
            Note($"No face material among {materials.Count} loaded materials - not showing facial layers.");
        if (bibo.Count == 0 && faces.Count == 0)
        {
            running?.Cancel();
            if (Unpublish()) RedrawWhenLive(lastUnpublishedPath, null);
            lastSignature = noBibo ? "no-bibo" : "no-face";
            Status = noBibo ? "No Bibo body" : "No face found";
            return;
        }

        upstream!.UsedFallback = false;
        upstream.HiddenByStacked = false;
        upstream.StackedAbove = penumbra.GetEnabledModPriority(collection, "Proteus") is >= ModPriority;
        var plan = BuildPlan(collection, parts, [(bibo, bodyParts, false), (faces, faceParts, true)]);
        if (probeStartedAt is { } probing)
        {
            if (upstream.UsedFallback && DateTime.UtcNow - probing < ProbeTimeout)
            {
                dueAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);   // Penumbra is still rebuilding
                return;
            }
            probeStartedAt = null;
        }
        if (upstream.HiddenByStacked && !warnedStacked)
        {
            warnedStacked = true;
            Notifier.Error($"Proteus has a higher Penumbra priority than Cummy ({ModPriority}), so Cummy can't see your skin "
                         + "underneath it and is using the game's skin. Set Proteus below Cummy and turn off its "
                         + "\"Auto-raise mod priority\" so your skin and tattoos show under your layers.");
        }
        if (plan.Materials.Count == 0)
        {
            Status = "No usable skin textures";
            Note($"{string.Join(", ", bibo.Concat(faces))}: no diffuse/normal/mask texture could be read.");
            Plugin.Log.Warning("No body or face material with usable diffuse/normal/mask textures was found.");
            return;
        }
        if (plan.Signature == lastSignature && publishedCollection == collection)
            return;   // nothing that feeds the composite has changed (also stops redraw echo loops)

        running?.Cancel();
        var cts = new CancellationTokenSource();
        running = cts;
        Status = "Compositing...";
        Note($"Compositing {string.Join(", ", parts.Select(p => p.DisplayName()))} onto {string.Join(", ", bibo.Concat(faces).Select(Path.GetFileName))}.");
        Task.Run(() => Execute(plan, cts.Token), cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested) return;
            if (t.IsFaulted)
            {
                Plugin.Log.Error(t.Exception!, "Composite failed.");
                Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    Status = "Failed: " + t.Exception!.InnerException?.Message;
                    Note(Status);
                });
                return;
            }
            Plugin.Framework.RunOnFrameworkThread(() => Publish(plan, t.Result, cts));
        });
    }

    /// <paramref name="groups"/>: materials with the parts that go onto them (body parts onto Bibo materials, facial
    /// parts onto face materials).
    private Plan BuildPlan(Guid collection, IReadOnlyList<BodyPart> parts,
                           IEnumerable<(List<string> Materials, List<BodyPart> Parts, bool Face)> groups)
    {
        // Each part's art and look are part of what the output depends on; shipped art with the default look keeps the
        // plain id (and file names).
        var sources = parts.ToDictionary(p => p, p => overlaySources.Resolve(p, layers.Stage(p)));
        var looks = parts.ToDictionary(p => p, configuration.ResolveLook);
        string KeyOf(IEnumerable<BodyPart> ps) => string.Join(",", ps.Select(p =>
            (sources[p].Origin == OverlayOrigin.Shipped ? p.Id() : $"{p.Id()}={sources[p].Stamp}")
            + (sources[p].Stage is > 0 and < BodyParts.MaxStages ? $"@{sources[p].Stage}" : "")
            + (looks[p].IsDefault ? "" : $"~{looks[p].Key}")));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<MaterialPlan>();
        var signature = new StringBuilder($"v{CompositeVersion}|{KeyOf(parts)}");

        foreach (var (materials, groupParts, isFace) in groups)
        foreach (var mtrl in materials)
        {
            // A material's outputs depend only on the parts that go onto it, so toggling a facial part never
            // rebuilds (or renames) the body textures, and the other way round.
            var partsKey = KeyOf(groupParts);
            FacePlan? face = null;
            if (isFace)
            {
                if (FaceMaterial.Match(mtrl) is not { Success: true } m) continue;
                var model = $"chara/human/{m.Groups[1].Value}/obj/face/{m.Groups[2].Value}/model/{m.Groups[1].Value}{m.Groups[2].Value}_fac.mdl";
                var modelDisk = penumbra.ResolvePlayer(model);
                var modelStamp = modelDisk != null && File.Exists(modelDisk)
                    ? $"{modelDisk}|{new FileInfo(modelDisk).Length}|{File.GetLastWriteTimeUtc(modelDisk).Ticks}"
                    : "game";
                face = new FacePlan(model, modelDisk, modelStamp);
                partsKey += $"|face:{model}|{modelStamp}";
            }

            var mtrlDisk = penumbra.ResolvePlayer(mtrl);
            var bytes = TexIo.LoadRawFile(data, mtrlDisk, mtrl);
            if (bytes == null) continue;
            var paths = TexIo.ParseMtrlTextures(bytes);

            Slot? Make(SlotKind kind, string? gamePath)
            {
                if (gamePath == null || !seen.Add(gamePath)) return null;
                if (gamePath.StartsWith("chara/common/", StringComparison.OrdinalIgnoreCase))
                {
                    // Shared by every character using it; redirecting it would paint them all.
                    Note($"Skipping shared texture {gamePath} ({kind}).");
                    return null;
                }
                var up = upstream!.Resolve(gamePath);
                var stamp = up != null && File.Exists(up)
                    ? $"{up}|{new FileInfo(up).Length}|{File.GetLastWriteTimeUtc(up).Ticks}"
                    : "game";
                var key = $"v{CompositeVersion}|{partsKey}|{kind}|{gamePath}|{stamp}";
                signature.Append('|').Append(key);
                var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
                return new Slot(kind, gamePath, up, stamp, Path.Combine(outDir!, hash + ".tex"));
            }

            var plan = new MaterialPlan(
                Make(SlotKind.Diffuse, paths.Diffuse),
                Make(SlotKind.Normal, paths.Normal),
                Make(SlotKind.Mask, paths.Mask),
                groupParts, face);
            if (plan.Slots.Any()) plans.Add(plan);
        }

        return new Plan(collection, parts, sources, looks, plans, signature.ToString());
    }

    // ── Compositing (background) ─────────────────────────────────────────────

    private Dictionary<string, string> Execute(Plan plan, CancellationToken ct)
    {
        // One composite at a time: a cancelled run may still be finishing when the next starts, and they share the cache.
        lock (cache.Lock)
        {
            while (staleArt.TryDequeue(out var stale))
                cache.Forget(stale);

            var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var material in plan.Materials)
            {
                ct.ThrowIfCancellationRequested();
                if (!material.Slots.All(s => File.Exists(s.OutFile)))
                    CompositeMaterial(material, plan.Sources, plan.Looks, ct);
                foreach (var slot in material.Slots)
                    if (File.Exists(slot.OutFile))
                        redirects[slot.GamePath] = slot.OutFile;
            }
            return redirects;
        }
    }

    private void CompositeMaterial(MaterialPlan material, IReadOnlyDictionary<BodyPart, OverlaySource> sources,
                                   IReadOnlyDictionary<BodyPart, LayerLook> looks, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var parts = material.Parts;

        // A face: its model maps the facial art (face-paint layout) onto its texture.
        FaceMapping? face = null;
        if (material.Face is { } fp)
        {
            var model = cache.Face(fp.ModelPath, fp.Stamp, () =>
            {
                var bytes = TexIo.LoadRawFile(data, fp.ModelDisk, fp.ModelPath);
                if (bytes == null) return null;
                var read = FaceModel.Read(bytes, out var error);
                if (read == null) Plugin.Log.Warning($"Can't map facial layers onto {fp.ModelDisk ?? fp.ModelPath}: {error}.");
                else Plugin.Log.Information($"Face model {fp.ModelDisk ?? fp.ModelPath}: {read.MeshCount} skin mesh(es), {read.VertexCount} vertices, {read.TriangleCount} triangles.");
                return read;
            });
            if (model == null) return;
            face = new FaceMapping(model, fp.Stamp);
        }

        (RgbaImage Image, byte[][]? Encoded)? Base(Slot? slot)
        {
            if (slot == null) return null;
            ct.ThrowIfCancellationRequested();
            var hit = cache.Base(slot.GamePath, slot.Stamp, () =>
            {
                var img = TexIo.LoadTex(data, slot.Upstream, slot.GamePath);
                // Faces are composited (and published) at a size the facial art holds up at.
                if (img != null && face != null && img.Width < MinFaceWidth)
                    img = Resample.To(img, MinFaceWidth, img.Height * MinFaceWidth / img.Width);
                return img;
            });
            if (hit == null) Plugin.Log.Warning($"Could not decode base {slot.Upstream ?? slot.GamePath}; skipping its {slot.Kind}.");
            return hit;
        }
        static RgbaImage Copy(RgbaImage img) => new((byte[])img.Rgba.Clone(), img.Width, img.Height);

        var diffuseBase = Base(material.Diffuse);
        var normalBase = Base(material.Normal);
        var maskBase = Base(material.Mask);

        // Diffuse: the parts painted over a copy of the cached skin, inside their own rectangles only.
        RgbaImage? diffuse = null;
        var diffuseRegions = new List<OverlayCrop>();
        if (diffuseBase is { } db)
        {
            diffuse = Copy(db.Image);
            foreach (var part in parts)
            {
                ct.ThrowIfCancellationRequested();
                if (cache.Overlay(part, sources[part], OverlaySources.DiffuseFile, diffuse.Width, diffuse.Height, face) is not { } art) continue;
                var ov = Tinted(art, looks[part]);
                OverlayBlend.AlphaCompositeRegion(diffuse.Rgba, diffuse.Width, ov.Rgba, ov.X, ov.Y, ov.W, ov.H);
                diffuseRegions.Add(ov);
            }
        }

        // Normal: added relief, then skin-tone suppression under the combined coverage.
        RgbaImage? normal = null;
        var normalRegions = new List<OverlayCrop>();
        if (normalBase is { } nb)
        {
            normal = Copy(nb.Image);
            var coverage = new byte[normal.Width * normal.Height];
            foreach (var part in parts)
            {
                ct.ThrowIfCancellationRequested();
                if (cache.Overlay(part, sources[part], OverlaySources.NormalFile, normal.Width, normal.Height, face) is not { } art) continue;
                var ov = Faded(art, looks[part].EffectiveOpacity);
                OverlayBlend.CompoundNormalRegion(normal.Rgba, normal.Width, ov.Rgba, ov.X, ov.Y, ov.W, ov.H);
                OverlayBlend.AccumulateCoverageRegion(coverage, normal.Width, ov.Rgba, ov.X, ov.Y, ov.W, ov.H);
                normalRegions.Add(ov);
            }
            if (diffuse != null && normalRegions.Count > 0)
            {
                // One suppression over the union, so one strength per material: the look is global, so every part on
                // the material shares it.
                var suppression = 1f - looks[parts[0]].SkinTone / 100f;
                if (diffuse.Width == normal.Width && diffuse.Height == normal.Height)
                {
                    // Once over the union, so overlapping parts are not suppressed twice.
                    var u = Union(normalRegions);
                    OverlayBlend.SuppressSkinColorInfluenceRegion(normal.Rgba, coverage, diffuse.Rgba, normal.Width,
                                                                  u.X, u.Y, u.W, u.H, suppression);
                }
                else
                {
                    var lum = Resample.To(diffuse, normal.Width, normal.Height);
                    OverlayBlend.SuppressSkinColorInfluence(normal.Rgba, coverage, lum.Rgba, normal.Width, normal.Height, suppression);
                }
            }
        }

        // Mask: gloss where the diffuse art covers.
        RgbaImage? mask = null;
        var maskRegions = new List<OverlayCrop>();
        if (maskBase is { } mb)
        {
            mask = Copy(mb.Image);
            foreach (var part in parts)
            {
                ct.ThrowIfCancellationRequested();
                if (cache.Overlay(part, sources[part], OverlaySources.DiffuseFile, mask.Width, mask.Height, face) is not { } art) continue;
                var ov = GlossWeight(art, looks[part]);
                OverlayBlend.GlossRegion(mask.Rgba, mask.Width, ov.Rgba, ov.X, ov.Y, ov.W, ov.H, GlossSpecular, GlossRoughness);
                maskRegions.Add(ov);
            }
        }

        void Write(Slot? slot, RgbaImage? img, byte[][]? baseEncoded, List<OverlayCrop> regions)
        {
            if (slot == null || img == null) return;
            ct.ThrowIfCancellationRequested();
            if (baseEncoded != null)
                TexIo.WriteBc3(img.Width, img.Height, CompositeCache.EncodeChanged(img, baseEncoded, regions), slot.OutFile);
            else
                TexIo.WriteTex(img, slot.OutFile);
        }
        Write(material.Diffuse, diffuse, diffuseBase?.Encoded, diffuseRegions);
        Write(material.Normal, normal, normalBase?.Encoded, normalRegions);
        Write(material.Mask, mask, maskBase?.Encoded, maskRegions);

        var seconds = (DateTime.UtcNow - started).TotalSeconds;
        Plugin.Framework.RunOnFrameworkThread(() => Note($"Built textures for {string.Join(", ", parts.Select(p => p.DisplayName()))} in {seconds:0.0}s."));
    }

    // ── Layer look (design: add-layer-look-settings D4) ──────────────────────
    // Cached crops are shared across composites, so a look works on a copy; the default look uses the crop as is.

    /// Diffuse art with the look's tint multiplied in and its opacity applied to the alpha (Proteus' row 16).
    private static OverlayCrop Tinted(OverlayCrop ov, LayerLook look)
    {
        var opacity = look.EffectiveOpacity;
        if (look.IsWhite && opacity == 0) return ov;
        var (tr, tg, tb) = look.TintRgb();
        var src = ov.Rgba;
        var dst = new byte[src.Length];
        OverlayBlend.ParallelPixels(0, src.Length, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                dst[i] = (byte)(src[i] * tr / 255);
                dst[i + 1] = (byte)(src[i + 1] * tg / 255);
                dst[i + 2] = (byte)(src[i + 2] * tb / 255);
                dst[i + 3] = OverlayBlend.ScaleAlpha(src[i + 3], opacity);
            }
        });
        return ov with { Rgba = dst };
    }

    /// Art whose alpha follows the look's opacity (normal relief and coverage fade with the layer).
    private static OverlayCrop Faded(OverlayCrop ov, int opacity)
    {
        if (opacity == 0) return ov;
        var dst = (byte[])ov.Rgba.Clone();
        OverlayBlend.ParallelPixels(3, dst.Length, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4) dst[i] = OverlayBlend.ScaleAlpha(dst[i], opacity);
        });
        return ov with { Rgba = dst };
    }

    /// The gloss weight (alpha) as Proteus mixes a mask overlay: min(art alpha × shine, opacity-adjusted coverage).
    private static OverlayCrop GlossWeight(OverlayCrop ov, LayerLook look)
    {
        var (shine, opacity) = (look.Shine, look.EffectiveOpacity);
        if (shine == 100 && opacity == 0) return ov;
        var dst = (byte[])ov.Rgba.Clone();
        OverlayBlend.ParallelPixels(3, dst.Length, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                var a = dst[i];
                dst[i] = Math.Min((byte)(a * shine / 100), OverlayBlend.ScaleAlpha(a, opacity));
            }
        });
        return ov with { Rgba = dst };
    }

    private static (int X, int Y, int W, int H) Union(List<OverlayCrop> regions)
    {
        int x0 = regions.Min(r => r.X), y0 = regions.Min(r => r.Y);
        int x1 = regions.Max(r => r.X + r.W), y1 = regions.Max(r => r.Y + r.H);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    // ── Publishing (framework thread) ────────────────────────────────────────

    /// The managed mod folder exists and is registered with Penumbra, and our texture folder is known.
    private bool EnsureManagedMod()
    {
        if (modDir == null)
        {
            if (penumbra.GetModDirectory() is not { } root) return false;
            modDir = Path.Combine(root, PenumbraIpc.ManagedModName);
            outDir = Path.Combine(modDir, "textures");
            upstream = new UpstreamResolver(penumbra, modDir,
                Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "upstream.json"));
        }
        try
        {
            if (!ManagedModManifest.Exists(modDir))
            {
                ManagedModManifest.Write(modDir, new Dictionary<string, string>());
                modRegistered = false;
                Note($"Created the Cummy mod at {modDir}.");
            }
            else if (!modRegistered)
            {
                // First use this session: start empty, so redirects left by a crash can't show stale layers.
                ManagedModManifest.Write(modDir, new Dictionary<string, string>());
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Could not write the managed mod at {modDir}");
            Note($"Could not write the Cummy mod: {ex.Message}");
            return false;
        }
        if (!modRegistered)
        {
            if (!penumbra.AddMod(PenumbraIpc.ManagedModName))
            {
                Note("Penumbra refused to add the Cummy mod.");
                return false;
            }
            modRegistered = true;
        }
        return true;
    }

    private void Publish(Plan plan, Dictionary<string, string> redirects, CancellationTokenSource cts)
    {
        if (cts.IsCancellationRequested || !ReferenceEquals(running, cts)) return;
        if (redirects.Count == 0)
        {
            Status = "Nothing to publish";
            Note("Composite produced no textures.");
            return;
        }

        var files = redirects.ToDictionary(kv => kv.Key, kv => Path.GetRelativePath(modDir!, kv.Value),
                                           StringComparer.OrdinalIgnoreCase);
        // A path whose file changed proves the reload landed once it resolves to the new file.
        var changedPath = files.Keys.FirstOrDefault(k => !publishedFiles.TryGetValue(k, out var was)
                                                         || !string.Equals(was, files[k], StringComparison.OrdinalIgnoreCase));
        try
        {
            ManagedModManifest.Write(modDir!, files);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Could not write the managed mod manifest");
            Status = "Couldn't write the Cummy mod";
            Note($"{Status}: {ex.Message}");
            return;
        }

        var reload = penumbra.ReloadMod(PenumbraIpc.ManagedModName);
        if (reload is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
        {
            // Penumbra may have dropped it (e.g. after rediscovering mods): register again and retry.
            Note($"Reloading the Cummy mod failed ({reload}); registering it again.");
            modRegistered = false;
            Schedule();
            return;
        }
        if (!enabledIn.Contains(plan.Collection))
        {
            if (penumbra.EnableMod(plan.Collection, PenumbraIpc.ManagedModName, ModPriority))
                enabledIn.Add(plan.Collection);
            else
                Note("Couldn't enable the Cummy mod in your collection - enable it in Penumbra.");
        }

        publishedCollection = plan.Collection;
        publishedFiles = files;
        var changed = plan.Signature != lastSignature;
        lastSignature = plan.Signature;
        Status = $"Showing {plan.Parts.Count} layered part(s)";
        Note($"Published {files.Count} texture(s) for {string.Join(", ", plan.Parts.Select(p => p.DisplayName()))}.");
        Prune();

        // Re-publishing identical content (after a probe) needs no redraw: those textures are already on screen.
        if (!changed) return;
        if (changedPath != null) RedrawWhenLive(changedPath, redirects[changedPath]);
        else penumbra.RedrawPlayer();
    }

    /// Empties the managed mod. True when it carried redirects.
    private bool Unpublish()
    {
        if (publishedCollection == null || modDir == null) return false;
        try
        {
            ManagedModManifest.Write(modDir, new Dictionary<string, string>());
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Could not empty the managed mod manifest");
            return false;
        }
        penumbra.ReloadMod(PenumbraIpc.ManagedModName);
        lastUnpublishedPath = publishedFiles.Keys.FirstOrDefault();
        publishedCollection = null;
        publishedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return true;
    }

    /// Redraw once Penumbra resolves <paramref name="gamePath"/> to <paramref name="expectedFile"/>, or - when
    /// <paramref name="expectedFile"/> is null (we just emptied the mod) - to anything but our own files.
    private void RedrawWhenLive(string? gamePath, string? expectedFile)
    {
        if (gamePath == null)
        {
            penumbra.RedrawPlayer();
            return;
        }
        pendingLive = new PendingLive(gamePath, expectedFile, DateTime.UtcNow + LiveTimeout);
    }

    private void PollLive()
    {
        var pending = pendingLive!;
        var resolved = penumbra.ResolvePlayer(pending.GamePath);
        var live = pending.ExpectedFile != null
            ? resolved != null && SamePath(resolved, pending.ExpectedFile)
            : upstream == null || !upstream.IsOwnOutput(resolved);
        // A compositor stacked above us (Proteus) keeps answering with its own file, built on ours: that is live.
        if (!live && upstream != null && upstream.IsStackedOutput(resolved)) live = true;
        if (!live && DateTime.UtcNow < pending.Deadline) return;

        pendingLive = null;
        if (!live)
        {
            Note($"Penumbra still resolves {Path.GetFileName(pending.GamePath)} to {resolved ?? "game data"} after "
               + $"{LiveTimeout.TotalSeconds:0}s - redrawing anyway. Another mod may outrank Cummy for this texture.");
            Plugin.Log.Warning($"Cummy mod not live for {pending.GamePath}: resolves to {resolved ?? "game data"}.");
        }
        penumbra.RedrawPlayer();
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// Deletes textures the manifest no longer names.
    private void Prune()
    {
        try
        {
            if (outDir == null || !Directory.Exists(outDir)) return;
            var keep = new HashSet<string>(
                publishedFiles.Values.Select(rel => Path.GetFullPath(Path.Combine(modDir!, rel))),
                StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(outDir, "*.tex"))
            {
                if (keep.Contains(Path.GetFullPath(file))) continue;
                try { File.Delete(file); } catch { /* in use; next prune */ }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to prune old outputs.");
        }
    }
}
