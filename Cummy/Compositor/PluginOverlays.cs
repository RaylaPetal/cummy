using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin;

namespace Cummy.Compositor;

/// What asking for a plugin overlay did; decode problems come later through <see cref="PluginOverlays.Failed"/>.
public enum SetOverlayOutcome { Accepted, Invalid, UnknownPart, TooMany, Disabled }

/// One plugin's overlay on the wearer. <see cref="Target"/> is a part id, or "full" for a whole Bibo body image split
/// into the body parts. <see cref="Stamp"/> identifies the files' content, so replaced art is never served from the
/// cache. <see cref="Ready"/> once the images were checked in the background.
public sealed record PluginOverlay(string Owner, string Key, string Target, BodyPart? Part, string Diffuse, string? Normal,
                                   int Opacity, long Order, string Stamp)
{
    public bool Ready { get; set; }

    public string Id => $"{Owner}/{Key}";
}

/// Other plugins' overlays (the plugin API's SetOverlay), composited above Cummy's own layers in the order they were
/// first set, in both compositing paths. Memory only: an owner's overlays go when it unloads, and all of them on logout
/// or when Cummy unloads. The wearer can remove any of them, or turn them all off
/// (<see cref="Configuration.ShowPluginOverlays"/>). Framework thread, except the image checks it starts itself.
public sealed class PluginOverlays : IDisposable
{
    public const int MaxOverlays = 16;
    public const int MaxKeyLength = 32;
    public const string FullTarget = "full";

    private readonly Configuration config;
    private readonly IDalamudPluginInterface pi;
    private readonly Dictionary<string, PluginOverlay> entries = new(StringComparer.OrdinalIgnoreCase);
    private long nextOrder;

    /// What is shown changed (an overlay became ready, was replaced or removed, or the setting was switched). Carries
    /// the stamp of art no longer used, if any, so cached crops of it can be freed.
    public event Action<string?>? Changed;

    /// (owner, key, reason): its images could not be used; the overlay was dropped.
    public event Action<string, string, string>? Failed;

    /// (owner, key): removed by the wearer, by its owner, or because its owner unloaded.
    public event Action<string, string>? Removed;

    public PluginOverlays(Configuration config, IDalamudPluginInterface pi)
    {
        this.config = config;
        this.pi = pi;
        pi.ActivePluginsChanged += OnPluginsChanged;
        Plugin.ClientState.Logout += OnLogout;
    }

    public void Dispose()
    {
        pi.ActivePluginsChanged -= OnPluginsChanged;
        Plugin.ClientState.Logout -= OnLogout;
        entries.Clear();
    }

    /// Every overlay set, ready or not, in stacking order (bottom first).
    public IReadOnlyList<PluginOverlay> All => entries.Values.OrderBy(e => e.Order).ToList();

    /// The overlays showing right now: ready ones, while the wearer allows them.
    public IReadOnlyList<PluginOverlay> Showing
        => config.ShowPluginOverlays ? entries.Values.Where(e => e.Ready).OrderBy(e => e.Order).ToList() : [];

    public SetOverlayOutcome Set(string owner, string key, string target, string diffusePath, string? normalPath, int opacity)
    {
        if (!config.ShowPluginOverlays) return SetOverlayOutcome.Disabled;
        owner = owner?.Trim() ?? "";
        key = key?.Trim() ?? "";
        target = target?.Trim() ?? "";
        normalPath = string.IsNullOrWhiteSpace(normalPath) ? null : normalPath;

        BodyPart? part = null;
        if (!string.Equals(target, FullTarget, StringComparison.Ordinal))
        {
            if (!BodyParts.TryParseId(target, out var p) || p == BodyPart.FullBody) return SetOverlayOutcome.UnknownPart;
            part = p;
            target = p.Id();   // an older version's Forehead id is Face
        }
        if (!IsLoadedPlugin(owner) || key.Length is 0 or > MaxKeyLength || opacity is < 1 or > 100)
            return SetOverlayOutcome.Invalid;
        if (!IsUsableFile(diffusePath) || (normalPath != null && !IsUsableFile(normalPath)))
            return SetOverlayOutcome.Invalid;

        var id = $"{owner}/{key}";
        var old = entries.GetValueOrDefault(id);
        if (old == null && entries.Count >= MaxOverlays) return SetOverlayOutcome.TooMany;

        var stamp = $"plugin:{id}:{OverlaySources.FileStamp(diffusePath)}";
        if (normalPath != null) stamp += $"+{OverlaySources.FileStamp(normalPath)}";
        var entry = new PluginOverlay(owner, key, target, part, diffusePath, normalPath, opacity, old?.Order ?? nextOrder++,
                                      $"{stamp}%{target}");
        entries[id] = entry;
        // Until the new images pass, the replaced ones stop showing.
        if (old is { Ready: true }) Changed?.Invoke(old.Stamp);
        Check(entry);
        return SetOverlayOutcome.Accepted;
    }

    /// Checks the images in the background, then shows the overlay or drops it with a reason.
    private void Check(PluginOverlay entry)
    {
        Task.Run(() =>
        {
            var problem = Problem(entry.Diffuse, normal: false);
            return problem ?? (entry.Normal != null ? Problem(entry.Normal, normal: true) : null);
        }).ContinueWith(t => Plugin.Framework.RunOnFrameworkThread(() =>
        {
            // Replaced or removed meanwhile: that one's own check decides.
            if (!entries.TryGetValue(entry.Id, out var current) || !ReferenceEquals(current, entry)) return;
            var problem = t.IsFaulted ? $"couldn't read the image ({t.Exception!.InnerException?.Message})" : t.Result;
            if (problem != null)
            {
                entries.Remove(entry.Id);
                Plugin.Log.Information($"Plugin overlay {entry.Id} not shown: {problem}");
                Failed?.Invoke(entry.Owner, entry.Key, problem);
                return;
            }
            entry.Ready = true;
            Plugin.Log.Information($"Plugin overlay {entry.Id} on {entry.Target} is ready.");
            Changed?.Invoke(null);
        }));
    }

    private static string? Problem(string path, bool normal)
    {
        try
        {
            return OverlaySources.CheckImage(File.ReadAllBytes(path), normal);
        }
        catch (Exception ex)
        {
            return $"couldn't read {Path.GetFileName(path)} ({ex.Message}).";
        }
    }

    /// True when it was there.
    public bool Remove(string owner, string key)
    {
        if (!entries.Remove($"{owner?.Trim()}/{key?.Trim()}", out var gone)) return false;
        Dropped(gone);
        return true;
    }

    /// How many were removed.
    public int Clear(string owner)
    {
        var gone = entries.Values.Where(e => string.Equals(e.Owner, owner?.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var e in gone)
        {
            entries.Remove(e.Id);
            Dropped(e);
        }
        return gone.Count;
    }

    private void ClearAll(string why)
    {
        if (entries.Count == 0) return;
        Plugin.Log.Information($"Removing every plugin overlay ({why}).");
        foreach (var e in entries.Values.ToList())
        {
            entries.Remove(e.Id);
            Dropped(e);
        }
    }

    private void Dropped(PluginOverlay gone)
    {
        Removed?.Invoke(gone.Owner, gone.Key);
        Changed?.Invoke(gone.Stamp);
    }

    /// The wearer switched <see cref="Configuration.ShowPluginOverlays"/>.
    public void SettingChanged() => Changed?.Invoke(null);

    /// The art the showing overlays put on <paramref name="part"/>, bottom first, with each one's opacity (1-100).
    /// A full-body image covers every body part (not Face) with its share; a part's image only that part.
    public IReadOnlyList<(OverlaySource Source, int Opacity)> SourcesFor(BodyPart part)
        => Showing.Where(e => e.Part == part || (e.Part == null && !part.IsFacial()))
                  .Select(e => (Source(e), e.Opacity))
                  .ToList();

    /// The parts any showing overlay puts art on.
    public IReadOnlyList<BodyPart> Parts()
        => BodyParts.PaintOrder.Where(p => SourcesFor(p).Count > 0).ToList();

    /// A full-body image is split by the region map like the wearer's own; a part's image is used whole, like a part
    /// override.
    public static OverlaySource Source(PluginOverlay e)
        => new(e.Part == null ? OverlayOrigin.FullBody : OverlayOrigin.Part, e.Diffuse, e.Normal, e.Stamp);

    private bool IsLoadedPlugin(string name)
        => name.Length > 0 && pi.InstalledPlugins.Any(p => p.IsLoaded && string.Equals(p.InternalName, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsUsableFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && OverlaySources.IsImagePath(path) && File.Exists(path);

    private void OnPluginsChanged(IActivePluginsChangedEventArgs args)
    {
        if (args.Kind is not (PluginListInvalidationKind.Unloaded or PluginListInvalidationKind.Update
                              or PluginListInvalidationKind.AutoUpdate)) return;
        var names = args.AffectedInternalNames.ToList();
        Plugin.Framework.RunOnFrameworkThread(() =>
        {
            foreach (var name in names)
                if (Clear(name) > 0) Plugin.Log.Information($"Removed {name}'s overlays: it unloaded.");
        });
    }

    private void OnLogout(int type, int code) => ClearAll("logged out");
}
