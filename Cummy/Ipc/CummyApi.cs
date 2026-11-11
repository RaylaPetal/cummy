using System;
using System.Collections.Generic;
using System.Linq;
using Cummy.Compositor;
using Cummy.Messaging;
using Cummy.Pairing;
using Cummy.State;
using Dalamud.Plugin.Ipc;

namespace Cummy.Ipc;

/// Cummy's IPC for other plugins (API version 5). The README's "Plugin API" section is the reference, and
/// Cummy.Api/CummyIpc.cs is a typed client other plugins can copy. This file keeps the version 1-4 endpoints, the
/// lifecycle events and the plumbing; the partial files add the reads (State), events (Events), actions (Actions),
/// plugin messaging (Messaging) and plugin overlays (Overlays).
///
/// Every endpoint except Cummy.ApiVersion answers only on the framework thread (<see cref="ApiStrings.WrongThread"/> or
/// an empty value otherwise), and no exception ever crosses into the caller (<see cref="ApiStrings.Error"/>).
/// Outcome strings are in <see cref="ApiStrings"/>. Consent settings (blocks, mode, blacklist, partners, cleaning
/// settings, the plugin toggles) are never writable from here.
public sealed partial class CummyApi : IDisposable
{
    public const int Version = 5;

    private readonly Configuration config;
    private readonly PairingService pairing;
    private readonly LayerState layers;
    private readonly PluginRelay relay;
    private readonly PluginOverlays overlays;

    /// Undoes each function registration, so Dispose can't miss one.
    private readonly List<Action> unregister = new();
    private readonly ICallGateProvider<object> ready;
    private readonly ICallGateProvider<object> disposing;
    private readonly ICallGateProvider<string, string, string, object> onLayerReceived;
    private readonly ICallGateProvider<int, object> onLayersChanged;

    public CummyApi(Configuration config, PairingService pairing, LayerState layers, PluginRelay relay,
                    PluginOverlays overlays)
    {
        this.overlays = overlays;
        this.config = config;
        this.pairing = pairing;
        this.layers = layers;
        this.relay = relay;

        // Answers from any thread: plugins check it while loading.
        var apiVersion = Plugin.PluginInterface.GetIpcProvider<int>("Cummy.ApiVersion");
        apiVersion.RegisterFunc(() => Version);
        unregister.Add(apiVersion.UnregisterFunc);

        Outcome<string, string, string>("Cummy.SendLayer", SendLayer);
        Read("Cummy.GetLayerCount", GetLayerCount, 0);
        Outcome<string, string, string>("Cummy.LayerSelf", LayerSelf);
        onLayerReceived = Plugin.PluginInterface.GetIpcProvider<string, string, string, object>("Cummy.OnLayerReceived");
        onLayersChanged = Plugin.PluginInterface.GetIpcProvider<int, object>("Cummy.OnLayersChanged");
        pairing.LayerReceived += OnPairingLayerReceived;
        layers.Changed += OnLayerStateChanged;

        RegisterState();
        RegisterEvents();
        RegisterActions();
        RegisterMessaging();
        RegisterOverlays();

#if DEBUG
        Outcome<string>("Cummy.DebugLayerSelf", DebugLayerSelf);
#endif

        disposing = Plugin.PluginInterface.GetIpcProvider<object>("Cummy.Disposing");
        ready = Plugin.PluginInterface.GetIpcProvider<object>("Cummy.Ready");
#if DEBUG
        DebugClient = new CummyIpcClient.CummyIpc(Plugin.PluginInterface);
        AttachDebugClient();
#endif
        Send("Cummy.Ready", () => ready.SendMessage());
    }

    public void Dispose()
    {
        Send("Cummy.Disposing", () => disposing.SendMessage());
#if DEBUG
        DebugClient.Dispose();
#endif
        UnhookEvents();
        layers.Changed -= OnLayerStateChanged;
        pairing.LayerReceived -= OnPairingLayerReceived;
        foreach (var undo in unregister) undo();
        unregister.Clear();
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    /// Runs <paramref name="body"/> on the framework thread only, never letting an exception reach the caller.
    private static T Guarded<T>(string name, Func<T> body, T offThread, T onError)
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread) return offThread;
        try
        {
            return body();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"{name} failed.");
            return onError;
        }
    }

    /// Raises an event, logging (never throwing) when a subscriber fails.
    private static void Send(string name, Action send)
    {
        try
        {
            send();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"A {name} subscriber failed.");
        }
    }

    // Reads: the empty value off the framework thread and on a failure.
    private void Read<TRet>(string name, Func<TRet> body, TRet empty)
    {
        var gate = Plugin.PluginInterface.GetIpcProvider<TRet>(name);
        gate.RegisterFunc(() => Guarded(name, body, empty, empty));
        unregister.Add(gate.UnregisterFunc);
    }

    private void Read<T1, TRet>(string name, Func<T1, TRet> body, TRet empty)
    {
        var gate = Plugin.PluginInterface.GetIpcProvider<T1, TRet>(name);
        gate.RegisterFunc(a => Guarded(name, () => body(a), empty, empty));
        unregister.Add(gate.UnregisterFunc);
    }

    private void Read<T1, T2, TRet>(string name, Func<T1, T2, TRet> body, TRet empty)
    {
        var gate = Plugin.PluginInterface.GetIpcProvider<T1, T2, TRet>(name);
        gate.RegisterFunc((a, b) => Guarded(name, () => body(a, b), empty, empty));
        unregister.Add(gate.UnregisterFunc);
    }

    // Outcomes: wrong-thread off the framework thread, error on a failure.
    private const string Off = ApiStrings.WrongThread;
    private const string Err = ApiStrings.Error;

    private void Outcome(string name, Func<string> body)
    {
        var gate = Plugin.PluginInterface.GetIpcProvider<string>(name);
        gate.RegisterFunc(() => Guarded(name, body, Off, Err));
        unregister.Add(gate.UnregisterFunc);
    }

    private void Outcome<T1>(string name, Func<T1, string> body)
    {
        var gate = Plugin.PluginInterface.GetIpcProvider<T1, string>(name);
        gate.RegisterFunc(a => Guarded(name, () => body(a), Off, Err));
        unregister.Add(gate.UnregisterFunc);
    }

    private void Outcome<T1, T2>(string name, Func<T1, T2, string> body)
    {
        var gate = Plugin.PluginInterface.GetIpcProvider<T1, T2, string>(name);
        gate.RegisterFunc((a, b) => Guarded(name, () => body(a, b), Off, Err));
        unregister.Add(gate.UnregisterFunc);
    }

    private void Outcome<T1, T2, T3>(string name, Func<T1, T2, T3, string> body)
    {
        var gate = Plugin.PluginInterface.GetIpcProvider<T1, T2, T3, string>(name);
        gate.RegisterFunc((a, b, c) => Guarded(name, () => body(a, b, c), Off, Err));
        unregister.Add(gate.UnregisterFunc);
    }

    private void Outcome<T1, T2, T3, T4>(string name, Func<T1, T2, T3, T4, string> body)
    {
        var gate = Plugin.PluginInterface.GetIpcProvider<T1, T2, T3, T4, string>(name);
        gate.RegisterFunc((a, b, c, d) => Guarded(name, () => body(a, b, c, d), Off, Err));
        unregister.Add(gate.UnregisterFunc);
    }

    private void Outcome<T1, T2, T3, T4, T5, T6>(string name, Func<T1, T2, T3, T4, T5, T6, string> body)
    {
        var gate = Plugin.PluginInterface.GetIpcProvider<T1, T2, T3, T4, T5, T6, string>(name);
        gate.RegisterFunc((a, b, c, d, e, f) => Guarded(name, () => body(a, b, c, d, e, f), Off, Err));
        unregister.Add(gate.UnregisterFunc);
    }

    /// Trims a source name the way LayerSelf always has: blank means "Someone".
    private static (string Name, string World) Source(string? name, string? world)
        => (string.IsNullOrWhiteSpace(name) ? "Someone" : name.Trim(), world?.Trim() ?? "");

    // ── Version 1-4 ──────────────────────────────────────────────────────────

    /// One of sent, not-paired, blacklisted, out-of-range, blocked, queue-full or unknown-part; a tell is queued only
    /// for sent.
    public string SendLayer(string name, string world, string partId)
    {
        var check = CanLayer(name, world, partId, out var peer, out var part);
        if (check != ApiStrings.Ok) return check;
        return pairing.SendLayer(peer!, part) ? ApiStrings.Sent : ApiStrings.QueueFull;
    }

    /// Full Body isn't a part: it layers whatever isn't blocked, so only blocking every part refuses it, as on the
    /// wearer's side (PairingService.ApplyFullBody).
    private static bool IsBlocked(BodyPart part, int blockMask)
        => part == BodyPart.FullBody
            ? (BodyParts.LayerTargetsMask & ~blockMask) == 0
            : part.IsBlockedBy(blockMask);

    /// One of layered, advanced, full, blocked, blacklisted, unknown-part or no-character. Full Body (since version 5)
    /// layers every unblocked part one stage: layered when any changed.
    public string LayerSelf(string partId, string sourceName, string sourceWorld)
    {
        if (!BodyParts.TryParseId(partId ?? "", out var part)) return ApiStrings.UnknownPart;
        var (name, world) = Source(sourceName, sourceWorld);
        return pairing.LayerSelf(part, name, world);
    }

    /// Layer stages worn: each layered part counts its stage.
    public int GetLayerCount() => layers.Snapshot().Sum(layers.Stage);

    private void OnLayerStateChanged()
    {
        var count = GetLayerCount();
        Send("Cummy.OnLayersChanged", () => onLayersChanged.SendMessage(count));
    }

    private void OnPairingLayerReceived(BodyPart part, string sourceName, string sourceWorld)
        => Send("Cummy.OnLayerReceived", () => onLayerReceived.SendMessage(part.Id(), sourceName, sourceWorld));

#if DEBUG
    // ── Debug builds ─────────────────────────────────────────────────────────

    private const int DebugLogSize = 8;

    /// The published client (Cummy.Api/CummyIpc.cs), subscribed through the real gates as another plugin would be.
    public CummyIpcClient.CummyIpc DebugClient { get; }

    /// The latest OnLayerReceived messages, as a subscriber sees them.
    public IReadOnlyList<string> DebugLog => debugLog;
    private readonly List<string> debugLog = new();

    /// The latest OnPartChanged messages, as a subscriber sees them.
    public IReadOnlyList<string> DebugPartLog => debugPartLog;
    private readonly List<string> debugPartLog = new();

    /// The last OnLayersChanged count and when it arrived.
    public string DebugLastLayersChanged { get; private set; } = "none yet";

    /// Other events (lifecycle, access, blocks, invites, lick results, messages, overlays), latest last.
    public IReadOnlyList<string> DebugEventLog => debugEventLog;
    private readonly List<string> debugEventLog = new();

    private static void Keep(List<string> log, string line)
    {
        log.Add($"{DateTime.Now:HH:mm:ss} {line}");
        if (log.Count > DebugLogSize) log.RemoveAt(0);
    }

    internal void DebugEvent(string line) => Keep(debugEventLog, line);

    private void AttachDebugClient()
    {
        var c = DebugClient;
        c.LayerReceived += (part, name, world) => Keep(debugLog, $"{part} from {name}@{world}");
        c.LayersChanged += count => DebugLastLayersChanged = $"{count} at {DateTime.Now:HH:mm:ss}";
        c.PartChanged += (part, from, to, cause, name, world)
            => Keep(debugPartLog, $"{part} {from}->{to} {cause}{(name.Length > 0 ? $" by {name}@{world}" : "")}");
        c.Ready += () => DebugEvent("Ready");
        c.Disposing += () => DebugEvent("Disposing");
        c.CharacterChanged += loggedIn => DebugEvent($"CharacterChanged {loggedIn}");
        c.BlocksChanged += blocked => DebugEvent($"BlocksChanged [{string.Join(",", blocked)}]");
        c.AccessChanged += () => DebugEvent("AccessChanged");
        c.InviteReceived += (name, world) => DebugEvent($"InviteReceived {name}@{world}");
        c.LickResult += (name, world, part, result) => DebugEvent($"LickResult {name}@{world} {part} {result}");
        c.Message += (channel, payload, name, world) => DebugEvent($"Message [{channel}] {payload} from {name}@{world}");
        c.OverlayFailed += (owner, key, reason) => DebugEvent($"OverlayFailed {owner}/{key}: {reason}");
        c.OverlayRemoved += (owner, key) => DebugEvent($"OverlayRemoved {owner}/{key}");
    }

    /// For another plugin to test against: layers this character as if a paired partner had sent the tell, through the
    /// same handling as a real one. OnLayerReceived reports it as coming from "Debug Partner@Debug".
    private string DebugLayerSelf(string partId)
    {
        if (!BodyParts.TryParseId(partId ?? "", out var part)) return ApiStrings.UnknownPart;
        pairing.SimulateLayerFromPartner(part);
        return ApiStrings.Sent;
    }
#endif
}
