using System;
using System.Collections.Generic;
using System.Linq;
using Cummy.Ipc;
using Glamourer.Api.Enums;

namespace Cummy.State;

/// Wears the drip ring (the Emperor's New Ring) through Glamourer for each dripping hole, keeps those slots locked to it
/// while they drip (re-applied whenever Glamourer reports a change, as Oathbound's SlotLockManager does; never
/// Glamourer's actor-wide lock), and gives each slot back when its drip ends: the item and dyes it held before, unless
/// the slot no longer shows the ring.
///
/// Which slot carries which hole is <see cref="Assignment"/>: right for pussy and left for ass by preference, but a slot
/// that something else holds (another plugin keeps putting its own item back) is given up and the hole moves to the
/// other slot; when both drip on one usable slot, the most recent creampie wins. DripVfx builds the effects from it.
/// What a slot held before is stored per character (CharacterData.DripRings), so a restore that has to wait for
/// Glamourer, a relog or a plugin reload still happens. Without Glamourer nothing is worn (settings says to wear the ring
/// by hand) and the preferred slots carry the effects. Framework thread.
public sealed class DripRings : IDisposable
{
    private static readonly ApiEquipSlot[] Slots = [ApiEquipSlot.RFinger, ApiEquipSlot.LFinger];

    /// Re-applies within this window beyond this many mean something else holds the slot: stop fighting it.
    private static readonly TimeSpan FightWindow = TimeSpan.FromSeconds(10);
    private const int MaxReapplies = 4;

    /// Long enough for a redraw requested just before the ring went on to finish (it can race Glamourer silently).
    private static readonly TimeSpan VerifyDelay = TimeSpan.FromSeconds(1);

    private readonly Configuration config;
    private readonly LayerState layers;
    private readonly CreampieService creampies;
    private readonly GlamourerIpc glamourer;

    private readonly Dictionary<ApiEquipSlot, List<DateTime>> reapplied = new();
    /// Slots something else holds, skipped until nothing drips any more (then tried again).
    private readonly HashSet<ApiEquipSlot> givenUp = new();
    private Dictionary<Hole, ApiEquipSlot> assignment = new();
    private bool enforcing;

    /// <see cref="Assignment"/> changed: DripVfx rebuilds the effects (before the rings move, so they load the new ones).
    public event Action? AssignmentChanged;

    public DripRings(Configuration config, LayerState layers, CreampieService creampies, GlamourerIpc glamourer)
    {
        this.config = config;
        this.layers = layers;
        this.creampies = creampies;
        this.glamourer = glamourer;
        creampies.Changed += OnChanged;
        creampies.Loaded += Reconcile;
        glamourer.AvailabilityChanged += Reconcile;
        glamourer.LocalPlayerStateChanged += Enforce;
        Plugin.Framework.RunOnTick(Reconcile, TimeSpan.FromSeconds(3));   // plugin reload
    }

    public void Dispose()
    {
        creampies.Changed -= OnChanged;
        creampies.Loaded -= Reconcile;
        glamourer.AvailabilityChanged -= Reconcile;
        glamourer.LocalPlayerStateChanged -= Enforce;
    }

    /// Glamourer is there to wear the ring.
    public bool CanWearRing => glamourer.IsAvailable && glamourer.DripRingItem != null;

    /// The ring slot carrying each dripping hole's effect (holes left out don't show: no usable slot).
    public IReadOnlyDictionary<Hole, ApiEquipSlot> Assignment => assignment;

    private void OnChanged(Hole hole, bool active, int secondsLeft, CreampieCause cause, string name, string world) => Reconcile();

    /// The preferred slot for each dripping hole, else the other one; slots given up are skipped, and the most recent
    /// creampie picks first.
    private Dictionary<Hole, ApiEquipSlot> Assign()
    {
        var active = creampies.Active.OrderByDescending(h => creampies.Remaining(h) ?? TimeSpan.Zero).ToList();
        if (active.Count == 0) givenUp.Clear();   // nothing drips: try every slot again next time
        var result = new Dictionary<Hole, ApiEquipSlot>();
        foreach (var hole in active)
        {
            var preferred = hole.RingSlot();
            var other = preferred == ApiEquipSlot.RFinger ? ApiEquipSlot.LFinger : ApiEquipSlot.RFinger;
            foreach (var slot in new[] { preferred, other })
            {
                if (givenUp.Contains(slot) || result.ContainsValue(slot)) continue;
                result[hole] = slot;
                break;
            }
        }
        return result;
    }

    /// Recomputes the assignment (rebuilding the effects when it changed), then puts the ring on each assigned slot and
    /// gives back every slot no hole needs any more.
    public void Reconcile()
    {
        var wanted = Assign();
        if (!wanted.OrderBy(kv => kv.Key).SequenceEqual(assignment.OrderBy(kv => kv.Key)))
        {
            assignment = wanted;
            Plugin.Log.Information("Drip slots: " + (assignment.Count == 0 ? "none"
                : string.Join(", ", assignment.Select(kv => $"{kv.Key.ApiName()} on {kv.Value}"))));
            AssignmentChanged?.Invoke();
        }

        if (!glamourer.IsAvailable || layers.Current is not { } me || Plugin.ObjectTable.LocalPlayer == null) return;
        if (glamourer.DripRingItem is not { } ring) return;

        foreach (var slot in Slots)
        {
            var key = slot.ToString();
            var recorded = me.DripRings.TryGetValue(key, out var previous);
            if (assignment.ContainsValue(slot))
            {
                if (recorded || glamourer.GetRing(slot) is not { } current) continue;
                if (!glamourer.SetRing(slot, ring, 0, 0)) continue;
                me.DripRings[key] = new DripRingRecord
                {
                    PreviousItem = current.ItemId,
                    PreviousStain = current.Stain,
                    PreviousStain2 = current.Stain2,
                };
                config.Save();
                reapplied.Remove(slot);
                Plugin.Log.Information($"Drip ring on {key} (was item {current.ItemId}); slot locked while it drips.");
                Plugin.Framework.RunOnTick(Enforce, VerifyDelay);
            }
            else if (recorded)
            {
                if (glamourer.GetRing(slot) is not { } current) continue;   // try again later
                // Only take our own ring off: if something else shows there now, leave it.
                if (current.ItemId == ring && !glamourer.SetRing(slot, previous!.PreviousItem, previous.PreviousStain, previous.PreviousStain2))
                    continue;
                me.DripRings.Remove(key);
                config.Save();
                reapplied.Remove(slot);
                Plugin.Log.Information($"Drip ring off {key}.");
            }
        }
    }

    /// The lock: each assigned slot that wears Cummy's ring (recorded) but now shows something else gets the ring back.
    /// A slot that keeps being changed back is given up, and its hole moves to the other slot if that is free.
    /// Runs on every Glamourer state change of the local player, and once a second after a ring goes on.
    private void Enforce()
    {
        if (enforcing || !glamourer.IsAvailable || layers.Current is not { } me || me.DripRings.Count == 0) return;
        if (glamourer.DripRingItem is not { } ring) return;
        var gaveUp = false;
        enforcing = true;   // our own SetItem raises a state change too
        try
        {
            foreach (var slot in assignment.Values.ToList())
            {
                var key = slot.ToString();
                if (!me.DripRings.ContainsKey(key) || givenUp.Contains(slot)) continue;
                if (glamourer.GetRing(slot) is not { } current || current.ItemId == ring) continue;

                var now = DateTime.UtcNow;
                if (!reapplied.TryGetValue(slot, out var times)) reapplied[slot] = times = new List<DateTime>();
                times.RemoveAll(t => now - t > FightWindow);
                if (times.Count >= MaxReapplies)
                {
                    givenUp.Add(slot);
                    gaveUp = true;
                    Plugin.Log.Warning($"Something keeps putting item {current.ItemId} on {key} (another plugin holding the "
                                     + "slot?): the drip moves to the other ring slot if it is free.");
                    continue;
                }
                times.Add(now);
                if (glamourer.SetRing(slot, ring, 0, 0))
                    Plugin.Log.Debug($"{key} showed item {current.ItemId} while dripping - drip ring put back.");
            }
        }
        finally
        {
            enforcing = false;
        }
        // Outside the guard: reassigning puts the ring on the other slot, which raises state changes of its own.
        if (gaveUp) Plugin.Framework.RunOnTick(Reconcile);
    }
}
