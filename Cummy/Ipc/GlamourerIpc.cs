using System;
using System.Collections.Generic;
using System.Linq;
using Glamourer.Api.Enums;
using Glamourer.Api.Helpers;
using Glamourer.Api.IpcSubscribers;
using Newtonsoft.Json.Linq;
using ItemSheet = Lumina.Excel.Sheets.Item;

namespace Cummy.Ipc;

/// The Glamourer calls the creampie drip needs: read what the local player wears on a ring slot, and put an item there
/// (the drip ring, or the previous ring back). Safe when Glamourer is missing: every call is guarded and returns
/// null/false. Framework thread.
public sealed class GlamourerIpc : IDisposable
{
    /// The drip ring's model: accessory 53, variant 1 (the Emperor's New Ring).
    private const ulong DripRingModel = 53 | (1UL << 16);

    private readonly ApiVersion apiVersion;
    private readonly GetState getState;
    private readonly SetItem setItem;
    private readonly EventSubscriber initializedSub;
    private readonly EventSubscriber disposedSub;
    private readonly EventSubscriber<nint, StateChangeType> stateChangedSub;
    private readonly EventSubscriber<nint, StateFinalizationType> stateFinalizedSub;
    private ulong? dripRingItem;
    private bool lookedUpRing;

    /// Glamourer appeared or went away.
    public event Action? AvailabilityChanged;

    /// The local player's Glamourer state changed: a slot edit (StateChangedWithType), or a design, automation or redraw
    /// settling (StateFinalized); both, as Oathbound does, since a single slot edit only raises the first.
    public event Action? LocalPlayerStateChanged;

    public bool IsAvailable { get; private set; }

    public GlamourerIpc(Dalamud.Plugin.IDalamudPluginInterface pi)
    {
        apiVersion = new ApiVersion(pi);
        getState = new GetState(pi);
        setItem = new SetItem(pi);
        initializedSub = Initialized.Subscriber(pi, () => SetAvailable(Check()));
        disposedSub = Disposed.Subscriber(pi, () => SetAvailable(false));
        stateChangedSub = StateChangedWithType.Subscriber(pi, (actor, _) => OnLocalState(actor));
        stateFinalizedSub = StateFinalized.Subscriber(pi, (actor, _) => OnLocalState(actor));
        IsAvailable = Check();
    }

    public void Dispose()
    {
        initializedSub.Dispose();
        disposedSub.Dispose();
        stateChangedSub.Dispose();
        stateFinalizedSub.Dispose();
    }

    private void OnLocalState(nint actor)
    {
        if (actor != 0 && actor == Plugin.ObjectTable.LocalPlayer?.Address) LocalPlayerStateChanged?.Invoke();
    }

    private bool Check()
    {
        try
        {
            var (major, minor) = apiVersion.Invoke();
            Plugin.Log.Information($"Glamourer IPC available (v{major}.{minor}).");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SetAvailable(bool available)
    {
        if (IsAvailable == available) return;
        IsAvailable = available;
        AvailabilityChanged?.Invoke();
    }

    /// The Emperor's New Ring's item id, from the Item sheet: the finger item with model 53, variant 1.
    public ulong? DripRingItem
    {
        get
        {
            if (lookedUpRing) return dripRingItem;
            lookedUpRing = true;
            var item = Plugin.DataManager.GetExcelSheet<ItemSheet>()
                .FirstOrDefault(i => i.ModelMain == DripRingModel
                                     && i.EquipSlotCategory.ValueNullable is { } slot && (slot.FingerL != 0 || slot.FingerR != 0));
            if (item.RowId != 0) dripRingItem = item.RowId;
            Plugin.Log.Information(dripRingItem is { } id ? $"Drip ring: item {id}." : "Drip ring item not found in the Item sheet.");
            return dripRingItem;
        }
    }

    /// What the local player wears on <paramref name="slot"/> per Glamourer: item id and both dyes. Null when it can't
    /// be read.
    public (ulong ItemId, byte Stain, byte Stain2)? GetRing(ApiEquipSlot slot)
    {
        if (!IsAvailable) return null;
        try
        {
            var (ec, state) = getState.Invoke(0, 0);
            if (ec != GlamourerApiEc.Success || state?["Equipment"]?[slot.ToString()] is not JObject item) return null;
            return (item.Value<ulong?>("ItemId") ?? 0, item.Value<byte?>("Stain") ?? 0, item.Value<byte?>("Stain2") ?? 0);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Glamourer GetState failed for {slot}.");
            return null;
        }
    }

    /// Puts <paramref name="itemId"/> on the local player's <paramref name="slot"/> as a manual Glamourer change. False
    /// when Glamourer refused or is missing.
    public bool SetRing(ApiEquipSlot slot, ulong itemId, byte stain, byte stain2)
    {
        if (!IsAvailable) return false;
        try
        {
            var ec = setItem.Invoke(0, slot, itemId, new List<byte> { stain, stain2 }, 0, ApplyFlag.Once);
            if (ec is GlamourerApiEc.Success or GlamourerApiEc.NothingDone) return true;
            Plugin.Log.Warning($"Glamourer SetItem {itemId} on {slot}: {ec}");
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Glamourer SetItem failed for {slot}.");
            return false;
        }
    }
}
