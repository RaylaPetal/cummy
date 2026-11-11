using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cummy.Ipc;
using Cummy.ThirdParty.Proteus;
using Penumbra.Api.Enums;

namespace Cummy.Compositor;

/// Cummy's one Penumbra mod (folder "Cummy"), shown as "Cummy [Name Surname @ World]" for the logged-in character. It
/// carries both the textures Cummy composites itself (redirects) and, for Proteus mode, the "Cummy layers" option
/// group whose art and sidecar live under its Proteus/ folder. The only writer of its manifest: CompositorService sets
/// the redirects, OverlayPackService the options, and each change is written and reloaded only when it differs.
/// Also removes the separate "Cummy Layers" mod older versions kept. Framework thread.
public sealed class ManagedMod
{
    public const string Folder = PenumbraIpc.ManagedModName;
    public const string GroupName = "Cummy layers";
    public const int Priority = 9999;

    /// Where older versions kept the Proteus overlay pack as a mod of its own.
    public const string LegacyPackFolder = "CummyLayers";

    private readonly PenumbraIpc penumbra;
    private readonly HashSet<Guid> enabledIn = new();
    private bool registered;
    private bool legacyChecked;

    private IReadOnlyDictionary<string, string> files = new Dictionary<string, string>();
    private IReadOnlyList<string> options = [];
    private string name = "Cummy";

    /// Penumbra re-read the manifest. Option selections should survive a reload with unchanged groups, but the pack
    /// selects again to be sure.
    public event Action? Reloaded;

    /// The older separate "Cummy Layers" mod was deleted, so Proteus should recomposite.
    public event Action? LegacyPackRemoved;

    public ManagedMod(PenumbraIpc penumbra) => this.penumbra = penumbra;

    /// The mod folder, once Penumbra reported its mod root.
    public string? Dir { get; private set; }

    /// The mod exists, is registered with Penumbra and loaded with the current manifest. On the first call each session
    /// the redirects start empty, so a crash can't leave stale layers showing.
    public bool Ensure()
    {
        if (Dir == null)
        {
            if (penumbra.GetModDirectory() is not { } root) return false;
            Dir = Path.Combine(root, Folder);
        }
        if (!legacyChecked)
        {
            legacyChecked = true;
            RemoveLegacyPack(Path.GetDirectoryName(Dir)!);
        }
        if (registered && ManagedModManifest.Exists(Dir)) return true;

        files = new Dictionary<string, string>();
        if (!Write()) return false;
        if (!penumbra.AddMod(Folder))   // already known after the first session: Penumbra just says so
        {
            Plugin.Log.Warning("Penumbra refused to add the Cummy mod.");
            return false;
        }
        registered = true;
        Reload();
        return true;
    }

    /// Penumbra seems to have dropped the mod (a reload failed): register it again on the next <see cref="Ensure"/>.
    public void ForgetRegistration()
    {
        registered = false;
        enabledIn.Clear();
    }

    /// Replaces the redirects (game path → path relative to <see cref="Dir"/>), writing and reloading only when they
    /// changed. Penumbra's answer to the reload, or NothingChanged.
    public PenumbraApiEc SetRedirects(IReadOnlyDictionary<string, string> redirects)
    {
        if (Dir == null) return PenumbraApiEc.SystemDisposed;
        if (SameRedirects(files, redirects)) return PenumbraApiEc.NothingChanged;
        files = new Dictionary<string, string>(redirects, StringComparer.OrdinalIgnoreCase);
        return Write() ? Reload() : PenumbraApiEc.UnknownError;
    }

    /// Replaces the "Cummy layers" options (none = no group). False when they could not be written.
    public bool SetOptions(IReadOnlyList<string> names)
    {
        if (Dir == null) return false;
        if (options.SequenceEqual(names)) return true;
        options = names.ToList();
        if (!Write()) return false;
        Reload();
        return true;
    }

    /// Names the mod after the logged-in character; rewritten only when the character changed.
    public void SetOwner(string playerName, string world)
    {
        var wanted = $"Cummy [{playerName} @ {world}]";
        if (wanted == name || playerName.Length == 0) return;
        name = wanted;
        if (Dir == null || !registered) return;   // Ensure writes it
        if (Write()) Reload();
        Plugin.Log.Information($"The Cummy mod is now named {name}.");
    }

    /// Enables the mod in <paramref name="collection"/> at Cummy's priority, once per collection per session.
    public bool Enable(Guid collection)
    {
        if (enabledIn.Contains(collection)) return true;
        if (!penumbra.EnableMod(collection, Folder, Priority)) return false;
        enabledIn.Add(collection);
        return true;
    }

    private bool Write()
    {
        try
        {
            ManagedModManifest.Write(Dir!, name, files, GroupName, options);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Could not write the Cummy mod at {Dir}");
            return false;
        }
    }

    private PenumbraApiEc Reload()
    {
        var ec = penumbra.ReloadMod(Folder);
        if (ec is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged) Reloaded?.Invoke();
        return ec;
    }

    private static bool SameRedirects(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.OrdinalIgnoreCase));

    /// Older versions kept the Proteus pack as its own "Cummy Layers" mod: delete it through Penumbra (its settings go
    /// with it), or from disk when Penumbra doesn't know it.
    private void RemoveLegacyPack(string modRoot)
    {
        var legacy = Path.Combine(modRoot, LegacyPackFolder);
        if (!Directory.Exists(legacy)) return;
        var ec = penumbra.DeleteMod(LegacyPackFolder);
        if (Directory.Exists(legacy))
        {
            try
            {
                Directory.Delete(legacy, recursive: true);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, $"Could not delete the old Cummy Layers mod at {legacy} (Penumbra: {ec}).");
                return;
            }
        }
        Plugin.Log.Information($"Removed the old separate Cummy Layers mod (Penumbra: {ec}); its options now live in the Cummy mod.");
        LegacyPackRemoved?.Invoke();
    }
}
