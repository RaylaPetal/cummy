using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace Cummy.State;

/// Which of the logged-in character's parts are layered: one layer per part (there is one texture per part).
/// Layer, clean, dry off, persistence. Raises <see cref="Changed"/> whenever the set changes, which is what the
/// compositor listens to.
public sealed class LayerState : IDisposable
{
    private static readonly TimeSpan DryCheckInterval = TimeSpan.FromSeconds(30);

    private readonly Configuration config;
    private DateTime nextDryCheck = DateTime.MinValue;

    public event Action? Changed;

    public LayerState(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
        Plugin.ClientState.Login += OnLogin;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.ClientState.Login -= OnLogin;
    }

    /// Null while no character is logged in.
    public CharacterData? Current
        => Plugin.PlayerState.ContentId is var id and not 0 ? config.ForCharacter(id) : null;

    public bool IsLayered(BodyPart part)
        => Current is { } data && data.LayeredAt.ContainsKey(part.Id());

    /// The layered parts, in paint order.
    public IReadOnlyList<BodyPart> Snapshot()
        => BodyParts.PaintOrder.Where(IsLayered).ToList();

    public bool IsEmpty => Snapshot().Count == 0;

    /// Layers a clean part. False when nothing changed (no character, or the part is already layered).
    public bool Add(BodyPart part)
    {
        if (Current is not { } data || IsLayered(part)) return false;
        data.LayeredAt[part.Id()] = DateTime.UtcNow;
        Commit();
        return true;
    }

    public bool Clean(BodyPart part)
    {
        if (Current is not { } data || !data.LayeredAt.Remove(part.Id())) return false;
        Commit();
        return true;
    }

    public bool CleanAll()
    {
        if (Current is not { } data || data.LayeredAt.Count == 0) return false;
        data.LayeredAt.Clear();
        Commit();
        return true;
    }

    /// Restart every layered part's drying time, e.g. when drying off is switched on, so nothing that was layered
    /// long ago vanishes the moment the option is ticked.
    public void ResetDryTimers()
    {
        if (Current is not { } data) return;
        foreach (var key in data.LayeredAt.Keys.ToList())
            data.LayeredAt[key] = DateTime.UtcNow;
        config.Save();
    }

    private void Commit()
    {
        config.Save();
        Changed?.Invoke();
    }

    private void OnLogin()
    {
        // Dry off whatever expired while offline, then let the compositor re-apply what's left.
        nextDryCheck = DateTime.MinValue;
        Plugin.Framework.RunOnTick(() =>
        {
            DryOff();
            Changed?.Invoke();
        }, TimeSpan.FromSeconds(3));
    }

    private void OnUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now < nextDryCheck) return;
        nextDryCheck = now + DryCheckInterval;
        DryOff();
    }

    /// Cleans every part layered longer ago than the drying time.
    private void DryOff()
    {
        if (!config.DryOffEnabled || Current is not { } data || data.LayeredAt.Count == 0) return;

        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(Configuration.MinDryMinutes, config.DryOffMinutes));
        var dried = data.LayeredAt.Where(kv => kv.Value <= cutoff).Select(kv => kv.Key).ToList();
        if (dried.Count == 0) return;

        foreach (var key in dried) data.LayeredAt.Remove(key);
        Plugin.Log.Information($"Dried off: {string.Join(", ", dried)}.");
        Commit();
    }
}
