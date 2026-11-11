using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace Cummy.State;

/// What a layer command did: layered a clean part, advanced a layered one a stage, or nothing (no character, or the
/// part was already at its last stage).
public enum LayerOutcome { None, Layered, Advanced, Full }

/// Why a part's stage changed, as the plugin API reports it (Ipc/ApiStrings.cs has the wire names).
public enum ChangeCause { Layer, FullBody, Drip, Rinse, Lick, Kiss, Dry, Clean, Plugin }

/// What caused a change, and the player responsible when there is one (empty otherwise).
public readonly record struct LayerChange(ChangeCause Cause, string SourceName = "", string SourceWorld = "")
{
    public static LayerChange Of(ChangeCause cause) => new(cause);
}

/// One part's stage before and after a change (0 = clean).
public readonly record struct PartDelta(BodyPart Part, int Old, int New);

/// Which of the logged-in character's parts are layered, and at which stage (staged parts build up through
/// <see cref="BodyParts.MaxStages"/> stages, each layer adding one). Layer, clean, dry off, persistence. Raises <see cref="Changed"/> whenever the set changes, which is what the
/// compositor listens to.
public sealed class LayerState : IDisposable
{
    private static readonly TimeSpan DryCheckInterval = TimeSpan.FromSeconds(30);

    private readonly Configuration config;
    private DateTime nextDryCheck = DateTime.MinValue;

    public event Action? Changed;

    /// Raised before <see cref="Changed"/> with the parts whose stage a commit changed, and why. Not raised on login.
    public event Action<IReadOnlyList<PartDelta>, LayerChange>? PartsChanged;

    /// Raised after login, once the stored state is loaded (true), and on logout (false).
    public event Action<bool>? CharacterChanged;

    // Events raised while a subscriber is still handling an earlier one wait here, so a subscriber that changes the
    // state again (the plugin API's SetStage, say) never sees events out of order.
    private readonly Queue<Action> pendingEvents = new();
    private bool raising;

    public LayerState(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
        Plugin.ClientState.Login += OnLogin;
        Plugin.ClientState.Logout += OnLogout;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.ClientState.Login -= OnLogin;
        Plugin.ClientState.Logout -= OnLogout;
    }

    /// Null while no character is logged in.
    public CharacterData? Current
        => Plugin.PlayerState.ContentId is var id and not 0 ? config.ForCharacter(id) : null;

    public bool IsLayered(BodyPart part)
        => Current is { } data && data.LayeredAt.ContainsKey(part.Id());

    /// The part's stage: 0 when clean, else 1 to its <see cref="BodyParts.StageCount"/>.
    public int Stage(BodyPart part)
    {
        if (Current is not { } data || !data.LayeredAt.ContainsKey(part.Id())) return 0;
        var last = part.StageCount();
        return data.Stages.TryGetValue(part.Id(), out var stage) ? Math.Clamp(stage, 1, last) : last;
    }

    /// The layered parts, in paint order.
    public IReadOnlyList<BodyPart> Snapshot()
        => BodyParts.PaintOrder.Where(IsLayered).ToList();

    public bool IsEmpty => Snapshot().Count == 0;

    /// Layers a clean part at stage 1, or advances a layered one a stage; either restarts its drying time.
    /// <see cref="LayerOutcome.Full"/> (nothing changed) when the part is already at its last stage.
    public LayerOutcome Layer(BodyPart part, LayerChange change)
    {
        // Full Body is not a part: see LayerAll.
        if (part == BodyPart.FullBody || Current is not { } data) return LayerOutcome.None;
        var stage = Stage(part);
        if (stage >= part.StageCount()) return stage == 0 ? LayerOutcome.None : LayerOutcome.Full;

        var before = Stages();
        Step(data, part, stage);
        Commit(before, change);
        return stage == 0 ? LayerOutcome.Layered : LayerOutcome.Advanced;
    }

    /// A Full Body layer: one <see cref="Layer"/> step on every part (Face included) not in <paramref name="blockMask"/>, in
    /// one commit (one recomposite). Returns the parts that changed and the stage they all reached, or null for
    /// <c>Stage</c> when they ended at different stages. No parts changed: nothing was layerable or all were full.
    public (IReadOnlyList<BodyPart> Changed, int? Stage) LayerAll(int blockMask, LayerChange change)
    {
        if (Current is not { } data) return ([], null);
        var before = Stages();
        var changed = new List<BodyPart>();
        var stages = new HashSet<int>();
        foreach (var part in BodyParts.LayerTargets)
        {
            var stage = Stage(part);
            if (part.IsBlockedBy(blockMask) || stage >= part.StageCount()) continue;
            Step(data, part, stage);
            changed.Add(part);
            stages.Add(stage + 1);
        }
        if (changed.Count > 0) Commit(before, change);
        return (changed, stages.Count == 1 ? stages.First() : null);
    }

    /// Layers a clean part at stage 1 or advances it one stage, restarting its drying time. No commit.
    private static void Step(CharacterData data, BodyPart part, int stage)
    {
        data.LayeredAt[part.Id()] = DateTime.UtcNow;
        if (part.StageCount() > 1) data.Stages[part.Id()] = stage + 1;
    }

    /// A drip from above (DripService): advances the part one stage (a clean part to stage 1) while it is below
    /// <paramref name="cap"/> and its last stage, restarting its drying time. Quiet: no partner event, so no
    /// notification or moodle. False when nothing changed.
    public bool Drip(BodyPart part, int cap)
    {
        if (Current is not { } data) return false;
        var stage = Stage(part);
        if (stage >= Math.Min(cap, part.StageCount())) return false;

        var before = Stages();
        data.LayeredAt[part.Id()] = DateTime.UtcNow;
        if (part.StageCount() > 1) data.Stages[part.Id()] = stage + 1;
        Commit(before, LayerChange.Of(ChangeCause.Drip));
        return true;
    }

    /// Cleans the whole part, whatever its stage.
    public bool Clean(BodyPart part, LayerChange change)
    {
        if (Current is not { } data || !data.LayeredAt.ContainsKey(part.Id())) return false;
        var before = Stages();
        data.LayeredAt.Remove(part.Id());
        data.Stages.Remove(part.Id());
        Commit(before, change);
        return true;
    }

    public bool CleanAll(LayerChange change)
    {
        if (Current is not { } data || data.LayeredAt.Count == 0) return false;
        var before = Stages();
        data.LayeredAt.Clear();
        data.Stages.Clear();
        Commit(before, change);
        return true;
    }

    /// Sets a stored part straight to <paramref name="stage"/> (0 cleans it), in one commit. A raised or set stage
    /// restarts its drying time. False when the part already is at that stage, or nothing can be stored for it.
    public bool SetStage(BodyPart part, int stage, LayerChange change)
    {
        if (part == BodyPart.FullBody || Current is not { } data) return false;
        stage = Math.Clamp(stage, 0, part.StageCount());
        if (Stage(part) == stage) return false;

        var before = Stages();
        if (stage == 0)
        {
            data.LayeredAt.Remove(part.Id());
            data.Stages.Remove(part.Id());
        }
        else
        {
            data.LayeredAt[part.Id()] = DateTime.UtcNow;
            if (part.StageCount() > 1) data.Stages[part.Id()] = stage;
        }
        Commit(before, change);
        return true;
    }

    /// Washing off (WashOffService): every layered part one stage down, a part at stage 1 cleaned, in one commit.
    /// Drying times are left alone, so a rinse never buys a part more time. False when nothing was layered.
    public bool Rinse()
    {
        if (Current is not { } data || data.LayeredAt.Count == 0) return false;
        var before = Stages();
        foreach (var part in Snapshot())
        {
            var stage = Stage(part);
            if (stage <= 1)
            {
                data.LayeredAt.Remove(part.Id());
                data.Stages.Remove(part.Id());
            }
            else
            {
                data.Stages[part.Id()] = stage - 1;
            }
        }
        Commit(before, LayerChange.Of(ChangeCause.Rinse));
        return true;
    }

    /// Restart one layered part's drying time without changing how it looks (no <see cref="Changed"/>).
    public void RestartDrying(BodyPart part)
    {
        if (Current is not { } data || !data.LayeredAt.ContainsKey(part.Id())) return;
        data.LayeredAt[part.Id()] = DateTime.UtcNow;
        config.Save();
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

    /// Every stored part's stage (0 = clean), to diff a change against.
    private Dictionary<BodyPart, int> Stages() => BodyParts.PaintOrder.ToDictionary(p => p, Stage);

    private void Commit(Dictionary<BodyPart, int> before, LayerChange change)
    {
        config.Save();
        var deltas = BodyParts.PaintOrder
            .Select(p => new PartDelta(p, before.GetValueOrDefault(p), Stage(p)))
            .Where(d => d.Old != d.New)
            .ToList();
        if (deltas.Count > 0)
            Plugin.Log.Debug($"Parts changed ({change.Cause} {change.SourceName}@{change.SourceWorld}): "
                             + string.Join(", ", deltas.Select(d => $"{d.Part.Id()} {d.Old}->{d.New}")));
        Raise(() =>
        {
            if (deltas.Count > 0) PartsChanged?.Invoke(deltas, change);
            Changed?.Invoke();
        });
    }

    /// Runs <paramref name="raise"/> now, or after the events being raised right now when called from a subscriber.
    private void Raise(Action raise)
    {
        pendingEvents.Enqueue(raise);
        if (raising) return;
        raising = true;
        try
        {
            while (pendingEvents.TryDequeue(out var next)) next();
        }
        finally
        {
            raising = false;
            pendingEvents.Clear();
        }
    }

    private void OnLogin()
    {
        // Dry off whatever expired while offline, then let the compositor re-apply what's left.
        nextDryCheck = DateTime.MinValue;
        Plugin.Framework.RunOnTick(() =>
        {
            DryOff();
            Raise(() =>
            {
                Plugin.Log.Debug("Character changed: logged in.");
                Changed?.Invoke();
                CharacterChanged?.Invoke(true);
            });
        }, TimeSpan.FromSeconds(3));
    }

    private void OnLogout(int type, int code)
    {
        Plugin.Log.Debug("Character changed: logged out.");
        Raise(() => CharacterChanged?.Invoke(false));
    }

    private void OnUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now < nextDryCheck) return;
        nextDryCheck = now + DryCheckInterval;
        DryOff();
    }

    /// Cleans every part (whole, whatever its stage) layered or advanced longer ago than the drying time.
    private void DryOff()
    {
        if (!config.DryOffEnabled || Current is not { } data || data.LayeredAt.Count == 0) return;

        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(Configuration.MinDryMinutes, config.DryOffMinutes));
        var dried = data.LayeredAt.Where(kv => kv.Value <= cutoff).Select(kv => kv.Key).ToList();
        if (dried.Count == 0) return;

        var before = Stages();
        foreach (var key in dried)
        {
            data.LayeredAt.Remove(key);
            data.Stages.Remove(key);
        }
        Plugin.Log.Information($"Dried off: {string.Join(", ", dried)}.");
        Commit(before, LayerChange.Of(ChangeCause.Dry));
    }
}
