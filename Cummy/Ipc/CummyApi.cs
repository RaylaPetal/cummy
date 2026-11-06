using System;
using System.Collections.Generic;
using Cummy.Pairing;
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
/// </list>
/// Framework thread: SendLayer reads the object table.
public sealed class CummyApi : IDisposable
{
    public const int Version = 1;

    public const string Sent = "sent";
    public const string NotPaired = "not-paired";
    public const string OutOfRange = "out-of-range";
    public const string Blocked = "blocked";
    public const string QueueFull = "queue-full";
    public const string UnknownPart = "unknown-part";

    private const string OnLayerReceivedName = "Cummy.OnLayerReceived";

    private readonly PairingService pairing;
    private readonly ICallGateProvider<int> apiVersion;
    private readonly ICallGateProvider<string, string, string, string> sendLayer;
    private readonly ICallGateProvider<string, string, string, object> onLayerReceived;

    public CummyApi(PairingService pairing)
    {
        this.pairing = pairing;

        apiVersion = Plugin.PluginInterface.GetIpcProvider<int>("Cummy.ApiVersion");
        apiVersion.RegisterFunc(() => Version);
        sendLayer = Plugin.PluginInterface.GetIpcProvider<string, string, string, string>("Cummy.SendLayer");
        sendLayer.RegisterFunc(SendLayer);
        onLayerReceived = Plugin.PluginInterface.GetIpcProvider<string, string, string, object>(OnLayerReceivedName);
        pairing.LayerReceived += OnPairingLayerReceived;

#if DEBUG
        debugSubscriber = Plugin.PluginInterface.GetIpcSubscriber<string, string, string, object>(OnLayerReceivedName);
        debugSubscriber.Subscribe(OnDebugLayerReceived);
#endif
    }

    public void Dispose()
    {
#if DEBUG
        debugSubscriber.Unsubscribe(OnDebugLayerReceived);
#endif
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

    private void OnPairingLayerReceived(BodyPart part, string sourceName, string sourceWorld)
        => onLayerReceived.SendMessage(part.Id(), sourceName, sourceWorld);

#if DEBUG
    private const int DebugLogSize = 8;

    private readonly ICallGateSubscriber<string, string, string, object> debugSubscriber;
    private readonly List<string> debugLog = new();

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
