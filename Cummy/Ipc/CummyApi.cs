using System;
using System.Collections.Generic;
using System.Linq;
using Cummy.Pairing;
using Cummy.State;
using Dalamud.Plugin.Ipc;

namespace Cummy.Ipc;

/// Cummy's own IPC, for other plugins:
/// <list type="bullet">
/// <item><c>Cummy.ApiVersion</c> () -> int</item>
/// <item><c>Cummy.SendLayer</c> (name, world, partId) -> outcome: layers a paired partner with the ordinary `cmy1 lay`
/// tell, so their Cummy still applies its own pairing, block and range rules. Part ids are the wire ids in
/// <see cref="BodyParts.Id"/>.</item>
/// <item><c>Cummy.OnLayerReceived</c> event (partId, sourceName, sourceWorld): a partner's layer changed a stage on the
/// wearer. Not raised for blocked or already full parts, nor for drips.</item>
/// <item><c>Cummy.GetLayerCount</c> () -> int (version 2): the layer stages the logged-in character wears, summed over
/// layered parts; 0 when clean or logged out.</item>
/// <item><c>Cummy.OnLayersChanged</c> event (count) (version 2): raised with the new count whenever layers are added,
/// drip, rinse, clean or dry off, and on login.</item>
/// </list>
/// Framework thread: SendLayer reads the object table.
public sealed class CummyApi : IDisposable
{
    public const int Version = 2;

    public const string Sent = "sent";
    public const string NotPaired = "not-paired";
    public const string OutOfRange = "out-of-range";
    public const string Blocked = "blocked";
    public const string QueueFull = "queue-full";
    public const string UnknownPart = "unknown-part";

    private const string OnLayerReceivedName = "Cummy.OnLayerReceived";
    private const string OnLayersChangedName = "Cummy.OnLayersChanged";

    private readonly PairingService pairing;
    private readonly LayerState layers;
    private readonly ICallGateProvider<int> apiVersion;
    private readonly ICallGateProvider<string, string, string, string> sendLayer;
    private readonly ICallGateProvider<string, string, string, object> onLayerReceived;
    private readonly ICallGateProvider<int> getLayerCount;
    private readonly ICallGateProvider<int, object> onLayersChanged;

    public CummyApi(PairingService pairing, LayerState layers)
    {
        this.pairing = pairing;
        this.layers = layers;

        apiVersion = Plugin.PluginInterface.GetIpcProvider<int>("Cummy.ApiVersion");
        apiVersion.RegisterFunc(() => Version);
        sendLayer = Plugin.PluginInterface.GetIpcProvider<string, string, string, string>("Cummy.SendLayer");
        sendLayer.RegisterFunc(SendLayer);
        onLayerReceived = Plugin.PluginInterface.GetIpcProvider<string, string, string, object>(OnLayerReceivedName);
        pairing.LayerReceived += OnPairingLayerReceived;
        getLayerCount = Plugin.PluginInterface.GetIpcProvider<int>("Cummy.GetLayerCount");
        getLayerCount.RegisterFunc(GetLayerCount);
        onLayersChanged = Plugin.PluginInterface.GetIpcProvider<int, object>(OnLayersChangedName);
        layers.Changed += OnLayerStateChanged;

#if DEBUG
        debugSubscriber = Plugin.PluginInterface.GetIpcSubscriber<string, string, string, object>(OnLayerReceivedName);
        debugSubscriber.Subscribe(OnDebugLayerReceived);
        debugLayerSelf = Plugin.PluginInterface.GetIpcProvider<string, string>("Cummy.DebugLayerSelf");
        debugLayerSelf.RegisterFunc(DebugLayerSelf);
        debugCountSubscriber = Plugin.PluginInterface.GetIpcSubscriber<int, object>(OnLayersChangedName);
        debugCountSubscriber.Subscribe(OnDebugLayersChanged);
#endif
    }

    public void Dispose()
    {
#if DEBUG
        debugSubscriber.Unsubscribe(OnDebugLayerReceived);
        debugLayerSelf.UnregisterFunc();
        debugCountSubscriber.Unsubscribe(OnDebugLayersChanged);
#endif
        layers.Changed -= OnLayerStateChanged;
        getLayerCount.UnregisterFunc();
        pairing.LayerReceived -= OnPairingLayerReceived;
        sendLayer.UnregisterFunc();
        apiVersion.UnregisterFunc();
    }

    /// One of the outcome constants above; a tell is queued only for <see cref="Sent"/>.
    public string SendLayer(string name, string world, string partId)
    {
        if (!BodyParts.TryParseId(partId, out var part)) return UnknownPart;
        if (pairing.FindPeer(name, world) is not { } peer) return NotPaired;
        if (!Proximity.Within(peer, Proximity.LayerRange)) return OutOfRange;
        if (IsBlocked(part, peer.BlockMask)) return Blocked;
        return pairing.SendLayer(peer, part) ? Sent : QueueFull;
    }

    /// Full Body isn't a part: it layers whatever isn't blocked, so only blocking every part refuses it, as on the
    /// wearer's side (PairingService.ApplyFullBody).
    private static bool IsBlocked(BodyPart part, int blockMask)
        => part == BodyPart.FullBody
            ? (BodyParts.LayerTargetsMask & ~blockMask) == 0
            : part.IsBlockedBy(blockMask);

    /// Layer stages worn: each layered part counts its stage (1 for single-stage parts).
    public int GetLayerCount() => layers.Snapshot().Sum(layers.Stage);

    private void OnLayerStateChanged() => onLayersChanged.SendMessage(GetLayerCount());

    private void OnPairingLayerReceived(BodyPart part, string sourceName, string sourceWorld)
        => onLayerReceived.SendMessage(part.Id(), sourceName, sourceWorld);

#if DEBUG
    private const int DebugLogSize = 8;

    private readonly ICallGateSubscriber<string, string, string, object> debugSubscriber;
    private readonly List<string> debugLog = new();
    private readonly ICallGateProvider<string, string> debugLayerSelf;

    /// Debug builds only, for another plugin to test against: layers this character as if a paired partner had sent the
    /// tell, through the same handling as a real one. OnLayerReceived reports it as coming from "Debug Partner@Debug".
    private string DebugLayerSelf(string partId)
    {
        if (!BodyParts.TryParseId(partId, out var part)) return UnknownPart;
        pairing.SimulateLayerFromPartner(part);
        return Sent;
    }

    private readonly ICallGateSubscriber<int, object> debugCountSubscriber;

    /// Debug builds only: the last OnLayersChanged count and when it arrived, through the real gate.
    public string DebugLastLayersChanged { get; private set; } = "none yet";

    private void OnDebugLayersChanged(int count)
        => DebugLastLayersChanged = $"{count} at {DateTime.Now:HH:mm:ss}";

    /// Debug builds only: the latest OnLayerReceived messages, as a subscriber sees them through the real gate.
    public IReadOnlyList<string> DebugLog => debugLog;

    private void OnDebugLayerReceived(string partId, string sourceName, string sourceWorld)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {partId} from {sourceName}@{sourceWorld}";
        Plugin.Log.Debug($"{OnLayerReceivedName}: {line}");
        debugLog.Add(line);
        if (debugLog.Count > DebugLogSize) debugLog.RemoveAt(0);
    }
#endif
}
