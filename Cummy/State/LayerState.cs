using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace Cummy.State;

/// What a layer command did: layered a clean part, advanced a layered one a stage, or nothing (no character, or the
/// part was already at its last stage).
public enum LayerOutcome { None, Layered, Advanced, Full }

/// Which of the logged-in character's parts are layered, and at which stage (staged parts build up through
/// <see cref="BodyParts.MaxStages"/> stages, each layer adding one). Layer, clean, dry off, persistence. Raises <see cref="Changed"/> whenever the set changes, which is what the
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
    public LayerOutcome Layer(BodyPart part)
    {
        // Full Body is not a part: see LayerAll.
        if (part == BodyPart.FullBody || Current is not { } data) return LayerOutcome.None;
        var stage = Stage(part);
        if (stage >= part.StageCount()) return stage == 0 ? LayerOutcome.None : LayerOutcome.Full;

        Step(data, part, stage);
        Commit();
        return stage == 0 ? LayerOutcome.Layered : LayerOutcome.Advanced;
    }

    /// A Full Body layer: one <see cref="Layer"/> step on every regional part not in <paramref name="blockMask"/>, in
    /// one commit (one recomposite). Returns the parts that changed and the stage they all reached, or null for
    /// <c>Stage</c> when they ended at different stages. No parts changed: nothing was layerable or all were full.
    public (IReadOnlyList<BodyPart> Changed, int? Stage) LayerAll(int blockMask)
    {
        if (Current is not { } data) return ([], null);
        var changed = new List<BodyPart>();
        var stages = new HashSet<int>();
        foreach (var part in BodyParts.Regional)
        {
            var stage = Stage(part);
            if (part.IsBlockedBy(blockMask) || stage >= part.StageCount()) continue;
            Step(data, part, stage);
            changed.Add(part);
            stages.Add(stage + 1);
        }
        if (changed.Count > 0) Commit();
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

        data.LayeredAt[part.Id()] = DateTime.UtcNow;
        if (part.StageCount() > 1) data.Stages[part.Id()] = stage + 1;
        Commit();
        return true;
    }

    /// Cleans the whole part, whatever its stage.
    public bool Clean(BodyPart part)
    {
        if (Current is not { } data || !data.LayeredAt.Remove(part.Id())) return false;
        data.Stages.Remove(part.Id());
        Commit();
        return true;
    }

    public bool CleanAll()
    {
        if (Current is not { } data || data.LayeredAt.Count == 0) return false;
        data.LayeredAt.Clear();
        data.Stages.Clear();
        Commit();
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

    /// Cleans every part (whole, whatever its stage) layered or advanced longer ago than the drying time.
    private void DryOff()
    {
        if (!config.DryOffEnabled || Current is not { } data || data.LayeredAt.Count == 0) return;

        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(Configuration.MinDryMinutes, config.DryOffMinutes));
        var dried = data.LayeredAt.Where(kv => kv.Value <= cutoff).Select(kv => kv.Key).ToList();
        if (dried.Count == 0) return;

        foreach (var key in dried)
        {
            data.LayeredAt.Remove(key);
            data.Stages.Remove(key);
        }
        Plugin.Log.Information($"Dried off: {string.Join(", ", dried)}.");
        Commit();
    }
}
