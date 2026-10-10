// Subset of Proteus's PenumbraBridge (https://github.com/solona-m/proteus, AGPL-3.0), adapted for Cummy.

using System;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Plugin;
using Penumbra.Api.Enums;
using Luna;
using Penumbra.Api.IpcSubscribers;

namespace Cummy.Ipc;

/// The Penumbra calls the compositor needs. Every call is safe when Penumbra is missing (it returns null/false),
/// and the game-object calls must run on the framework thread.
public sealed class PenumbraIpc : IDisposable
{
    private readonly ApiVersion apiVersion;
    private readonly GetCollectionForObject getCollectionForObject;
    private readonly ResolvePlayerPath resolvePlayerPath;
    private readonly GetGameObjectResourcePaths getGameObjectResourcePaths;
    private readonly RemoveTemporaryMod removeTemporaryMod;
    private readonly RedrawObject redrawObject;
    private readonly GetModDirectory getModDirectory;
    private readonly AddMod addMod;
    private readonly ReloadMod reloadMod;
    private readonly TrySetMod trySetMod;
    private readonly TrySetModPriority trySetModPriority;
    private readonly GetCurrentModSettings getCurrentModSettings;
    private readonly TrySetModSettings trySetModSettings;

    private readonly EventSubscriber<ModSettingChange, Guid, string, bool> modSettingChangedSub;
    private readonly EventSubscriber<nint, int> gameObjectRedrawnSub;
    private readonly EventSubscriber initializedSub;
    private readonly EventSubscriber disposedSub;

    /// The managed mod's folder name; its own setting changes are never the wearer's skin changing.
    public const string ManagedModName = "Cummy";

    /// When we last changed our mod or redrew, so Penumbra's echoes of that can be dropped.
    private long lastOwnChangeTick;

    public bool IsAvailable { get; private set; }

    public event Action? Ready;

    /// A collection's mod settings changed (not our own echo): the skin underneath may be different now.
    /// Carries the collection id.
    public event Action<Guid>? ModSettingsChanged;

    /// The local player was redrawn by someone else (not our own redraw).
    public event Action? PlayerRedrawn;

    public PenumbraIpc(IDalamudPluginInterface pi)
    {
        apiVersion = new ApiVersion(pi);
        getCollectionForObject = new GetCollectionForObject(pi);
        resolvePlayerPath = new ResolvePlayerPath(pi);
        getGameObjectResourcePaths = new GetGameObjectResourcePaths(pi);
        removeTemporaryMod = new RemoveTemporaryMod(pi);
        redrawObject = new RedrawObject(pi);
        getModDirectory = new GetModDirectory(pi);
        getCurrentModSettings = new GetCurrentModSettings(pi);
        trySetModSettings = new TrySetModSettings(pi);
        addMod = new AddMod(pi);
        reloadMod = new ReloadMod(pi);
        trySetMod = new TrySetMod(pi);
        trySetModPriority = new TrySetModPriority(pi);

        modSettingChangedSub = ModSettingChanged.Subscriber(pi, (_, collection, modDir, _) =>
        {
            // Our own mod changes come back as events, and Glamourer re-applies its temporary settings in reaction to
            // them; both land inside the echo window.
            if (IsOwnEcho() || string.Equals(modDir, ManagedModName, StringComparison.OrdinalIgnoreCase)) return;
            ModSettingsChanged?.Invoke(collection);
        });
        gameObjectRedrawnSub = GameObjectRedrawn.Subscriber(pi, (_, index) =>
        {
            if (index != 0 || IsOwnEcho()) return;
            PlayerRedrawn?.Invoke();
        });
        initializedSub = Initialized.Subscriber(pi, () =>
        {
            CheckAvailability();
            if (IsAvailable) Ready?.Invoke();
        });
        disposedSub = Disposed.Subscriber(pi, () => IsAvailable = false);

        CheckAvailability();
    }

    public void Dispose()
    {
        modSettingChangedSub.Dispose();
        gameObjectRedrawnSub.Dispose();
        initializedSub.Dispose();
        disposedSub.Dispose();
    }

    private bool IsOwnEcho()
        => unchecked(Environment.TickCount64 - Interlocked.Read(ref lastOwnChangeTick)) < 3000;

    private void StampOwnChange() => Interlocked.Exchange(ref lastOwnChangeTick, Environment.TickCount64);

    private void CheckAvailability()
    {
        try
        {
            var version = apiVersion.Invoke();
            IsAvailable = true;
            Plugin.Log.Information($"Penumbra IPC available (v{version.Breaking}.{version.Features}).");
        }
        catch
        {
            IsAvailable = false;
            Plugin.Log.Warning("Penumbra IPC not available.");
        }
    }

    /// The local player's effective collection.
    public Guid? GetPlayerCollectionId()
    {
        if (!IsAvailable) return null;
        try
        {
            var result = getCollectionForObject.Invoke(0);
            return result.ObjectValid ? result.EffectiveCollection.Id : null;
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "GetCollectionForObject failed"); return null; }
    }

    /// Resolve a game path to the player's current on-disk file (respects all active mods), or null for game data.
    public string? ResolvePlayer(string gamePath)
    {
        if (!IsAvailable) return null;
        try
        {
            var resolved = resolvePlayerPath.Invoke(gamePath);
            return string.IsNullOrEmpty(resolved) || string.Equals(resolved, gamePath, StringComparison.OrdinalIgnoreCase)
                ? null
                : resolved;
        }
        catch (Exception ex) { Plugin.Log.Error(ex, $"ResolvePlayerPath failed for {gamePath}"); return null; }
    }

    /// Material game paths currently loaded by the local player's draw object. Framework thread only.
    public HashSet<string>? GetActivePlayerMaterialPaths()
    {
        if (!IsAvailable) return null;
        try
        {
            var dict = getGameObjectResourcePaths.Invoke(0)[0];
            if (dict == null) return null;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var gamePaths in dict.Values)
                foreach (var p in gamePaths)
                    if (p.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
                        paths.Add(p);
            return paths;
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "GetGameObjectResourcePaths failed"); return null; }
    }

    /// Penumbra's mod root folder, or null.
    public string? GetModDirectory()
    {
        if (!IsAvailable) return null;
        try
        {
            var dir = getModDirectory.Invoke();
            return string.IsNullOrEmpty(dir) ? null : dir;
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "GetModDirectory failed"); return null; }
    }

    /// Register a mod folder (inside the mod root) with Penumbra. Already-known counts as success.
    public bool AddMod(string modDirectory)
    {
        if (!IsAvailable) return false;
        try
        {
            StampOwnChange();
            var ec = addMod.Invoke(modDirectory);
            if (ec is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged) return true;
            Plugin.Log.Warning($"AddMod {modDirectory} -> {ec}");
            return false;
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "AddMod failed"); return false; }
    }

    /// Make Penumbra re-read a mod's manifest after we rewrote it.
    public PenumbraApiEc ReloadMod(string modDirectory)
    {
        if (!IsAvailable) return PenumbraApiEc.SystemDisposed;
        try
        {
            StampOwnChange();
            return reloadMod.Invoke(modDirectory);
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "ReloadMod failed"); return PenumbraApiEc.UnknownError; }
    }

    /// Enable the mod in a collection at the given priority.
    public bool EnableMod(Guid collection, string modDirectory, int priority)
    {
        if (!IsAvailable) return false;
        try
        {
            StampOwnChange();
            var enabled = trySetMod.Invoke(collection, modDirectory, true);
            var prio = trySetModPriority.Invoke(collection, modDirectory, priority);
            var ok = enabled is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged
                  && prio is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged;
            if (!ok) Plugin.Log.Warning($"Enabling {modDirectory} in {collection}: enable -> {enabled}, priority -> {prio}");
            return ok;
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "TrySetMod failed"); return false; }
    }

    /// Select exactly <paramref name="options"/> in a mod's option group (empty = none) in a collection.
    public bool SetModOptions(Guid collection, string modDirectory, string group, IReadOnlyList<string> options)
    {
        if (!IsAvailable) return false;
        try
        {
            StampOwnChange();
            var ec = trySetModSettings.Invoke(collection, modDirectory, group, options);
            if (ec is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged) return true;
            Plugin.Log.Warning($"Setting {modDirectory} / {group} to [{string.Join(", ", options)}] in {collection}: {ec}");
            return false;
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "TrySetModSettings failed"); return false; }
    }

    /// A mod's priority in a collection when it is enabled there, else null.
    public int? GetEnabledModPriority(Guid collection, string modDirectory)
    {
        if (!IsAvailable) return null;
        try
        {
            var (ec, settings) = getCurrentModSettings.Invoke(collection, modDirectory);
            return ec == PenumbraApiEc.Success && settings is { Item1: true } s ? s.Item2 : null;
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "GetCurrentModSettings failed"); return null; }
    }

    /// Remove a temporary mod left behind by an older build, which published that way.
    public void RemoveLegacyTemporaryMod(Guid collection)
    {
        if (!IsAvailable) return;
        try { removeTemporaryMod.Invoke(ManagedModName, collection, 99); }
        catch { /* best effort */ }
    }

    public bool RedrawPlayer()
    {
        if (!IsAvailable) return false;
        try
        {
            StampOwnChange();
            redrawObject.Invoke(0, RedrawType.Redraw);
            return true;
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "RedrawObject failed"); return false; }
    }
}
