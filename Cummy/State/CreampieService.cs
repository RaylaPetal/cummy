using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace Cummy.State;

/// Why a creampie started or ended, as the plugin API reports it (Ipc/ApiStrings.cs has the wire names).
public enum CreampieCause { Creampie, Timer, Wash, Lick, Clean, Plugin }

/// The creampie drip timers of the logged-in character, one per hole: started (or restarted at full length) by a
/// creampie, ended when it runs out, by washing in water, by a partner's lick of Groin/Butt, or by cleaning. Stored per
/// character, counting on while logged out. <see cref="Changed"/> drives the drip VFX, the ring and the API.
/// Framework thread.
public sealed class CreampieService : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly Configuration config;
    private readonly LayerState layers;
    private DateTime nextTick = DateTime.MinValue;

    /// (hole, active, seconds left, cause, source name, source world). Started, restarted or ended.
    public event Action<Hole, bool, int, CreampieCause, string, string>? Changed;

    /// The stored timers were loaded for a newly logged-in character (expired ones already ended): reconcile.
    public event Action? Loaded;

    public CreampieService(Configuration config, LayerState layers)
    {
        this.config = config;
        this.layers = layers;
        Plugin.Framework.Update += OnUpdate;
        Plugin.ClientState.Login += OnLogin;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.ClientState.Login -= OnLogin;
    }

    private Dictionary<string, CreampieRecord>? Records => layers.Current?.Creampies;

    public bool IsActive(Hole hole)
        => Records is { } r && r.TryGetValue(hole.Id(), out var c) && c.ExpiresUtc > DateTime.UtcNow;

    /// Time left on <paramref name="hole"/>'s drip, or null when it isn't dripping.
    public TimeSpan? Remaining(Hole hole)
        => Records is { } r && r.TryGetValue(hole.Id(), out var c) && c.ExpiresUtc > DateTime.UtcNow
            ? c.ExpiresUtc - DateTime.UtcNow
            : null;

    public IReadOnlyList<Hole> Active => Holes.All.Where(IsActive).ToList();

    /// Starts <paramref name="hole"/>'s drip, or restarts it at full length. True when it was already dripping.
    /// False (and nothing changes) while no character is logged in.
    public bool Start(Hole hole, string sourceName, string sourceWorld, out bool wasRunning)
    {
        wasRunning = IsActive(hole);
        if (Records is not { } records) return false;
        var duration = config.CreampieDuration;
        records[hole.Id()] = new CreampieRecord
        {
            ExpiresUtc = DateTime.UtcNow + duration,
            SourceName = sourceName,
            SourceWorld = sourceWorld,
        };
        config.Save();
        Changed?.Invoke(hole, true, (int)duration.TotalSeconds, CreampieCause.Creampie, sourceName, sourceWorld);
        return true;
    }

    /// Ends <paramref name="hole"/>'s drip. False when it wasn't dripping.
    public bool End(Hole hole, CreampieCause cause, string sourceName = "", string sourceWorld = "")
    {
        if (Records is not { } records || !records.Remove(hole.Id(), out var gone)) return false;
        config.Save();
        // A timer that ran out was still "dripping" until now; any other end is only news if it was still running.
        if (cause != CreampieCause.Timer && gone.ExpiresUtc <= DateTime.UtcNow) cause = CreampieCause.Timer;
        Plugin.Log.Information($"Creampie ({hole.ApiName()}) ended: {cause}.");
        Changed?.Invoke(hole, false, 0, cause, sourceName, sourceWorld);
        return true;
    }

    /// How many ended.
    public int EndAll(CreampieCause cause) => Holes.All.Count(h => End(h, cause));

    private void OnUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now < nextTick) return;
        nextTick = now + Tick;
        if (Records is not { Count: > 0 } records) return;
        foreach (var hole in Holes.All)
            if (records.TryGetValue(hole.Id(), out var c) && c.ExpiresUtc <= now)
                End(hole, CreampieCause.Timer);
    }

    private void OnLogin()
    {
        // Same delay as LayerState's re-apply: the character's data is there by then.
        Plugin.Framework.RunOnTick(() =>
        {
            if (Records is { } records)
                foreach (var hole in Holes.All)
                    if (records.TryGetValue(hole.Id(), out var c) && c.ExpiresUtc <= DateTime.UtcNow)
                        End(hole, CreampieCause.Timer);
            Loaded?.Invoke();
        }, TimeSpan.FromSeconds(3));
    }
}
