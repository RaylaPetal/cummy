// Cummy plugin API client (API version 6). Copy this file into your Dalamud plugin to call Cummy over IPC with types
// and named outcomes instead of raw call-gate strings. Cummy's README ("Plugin API") documents every endpoint.
//
//     var cummy = new CummyIpcClient.CummyIpc(PluginInterface);
//     cummy.Ready += () => { /* Cummy (re)loaded: re-set your overlays, refresh your state */ };
//     cummy.PartChanged += (part, from, to, cause, name, world) => { ... };
//     if (cummy.IsAvailable) cummy.LayerSelf("ch", "Someone", "");
//     ...
//     cummy.Dispose();   // in your plugin's Dispose
//
// Call everything from the framework thread (Framework.Update, RunOnFrameworkThread, or your UI's Draw); other threads
// get "wrong-thread". While Cummy isn't loaded, calls return "unavailable" (or an empty value) instead of throwing.
// Event subscriptions survive Cummy reloads: subscribe once, here, and watch Ready.

using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace CummyIpcClient;

/// The strings Cummy returns and raises. Compare against these rather than literals.
public static class CummyStrings
{
    /// Client-side only: Cummy isn't loaded (or is older than the call needs).
    public const string Unavailable = "unavailable";

    public const string WrongThread = "wrong-thread";
    public const string Error = "error";
    public const string NoCharacter = "no-character";
    public const string UnknownPart = "unknown-part";

    public const string Sent = "sent";
    public const string Ok = "ok";
    public const string NotPaired = "not-paired";
    public const string OutOfRange = "out-of-range";
    public const string Blocked = "blocked";
    public const string QueueFull = "queue-full";
    public const string Blacklisted = "blacklisted";
    public const string AlreadyPaired = "already-paired";
    public const string Pending = "pending";
    public const string Invalid = "invalid";
    public const string Disabled = "disabled";

    public const string Layered = "layered";
    public const string Advanced = "advanced";
    public const string Full = "full";
    public const string Cleaned = "cleaned";
    public const string AlreadyClean = "already-clean";
    public const string Locked = "locked";
    public const string Rinsed = "rinsed";
    public const string Set = "set";
    public const string Unchanged = "unchanged";
    public const string InvalidStage = "invalid-stage";

    public const string Creampied = "creampied";
    public const string Refreshed = "refreshed";
    public const string UnknownHole = "unknown-hole";

    /// Creampie holes (SendCreampie, CreampieSelf, GetCreampies, CleanSelf).
    public const string Pussy = "pussy";
    public const string Ass = "ass";

    public const string Accepted = "accepted";
    public const string TooMany = "too-many";
    public const string Removed = "removed";

    public const string Licked = "licked";
    public const string Nothing = "nothing";

    public const string Pairing = "pairing";
    public const string Public = "public";

    /// CleanSelf's "every part".
    public const string AllParts = "all";
    /// SetOverlay's target for a full-body image (Bibo layout).
    public const string FullBodyImage = "full";
    /// Full Body's part id (SendLayer, CanLayer, LayerSelf).
    public const string FullBody = "fb";

    public const string CauseLayer = "layer";
    public const string CauseFullBody = "full-body";
    public const string CauseDrip = "drip";
    public const string CauseRinse = "rinse";
    public const string CauseLick = "lick";
    public const string CauseKiss = "kiss";
    public const string CauseDry = "dry";
    public const string CauseClean = "clean";
    public const string CausePlugin = "plugin";
    public const string CauseCreampie = "creampie";
    public const string CauseTimer = "timer";
    public const string CauseWash = "wash";
}

/// Typed access to every Cummy.* call gate. Construct once, dispose with your plugin.
public sealed class CummyIpc : IDisposable
{
    /// The API version this client was written for.
    public const int ClientVersion = 6;

    // Lifecycle.
    public event Action? Ready;
    public event Action? Disposing;

    // Layering state.
    /// (partId, sourceName, sourceWorld): a partner's layer (or LayerSelf) changed a stage on the wearer.
    public event Action<string, string, string>? LayerReceived;
    /// (total stages worn): after any change, and on login.
    public event Action<int>? LayersChanged;
    /// (partId, oldStage, newStage, cause, sourceName, sourceWorld): one per changed part. Causes: CummyStrings.Cause*.
    public event Action<string, int, int, string, string, string>? PartChanged;
    /// (loggedIn).
    public event Action<bool>? CharacterChanged;

    // Consent and pairing.
    /// (blocked part ids).
    public event Action<List<string>>? BlocksChanged;
    /// Partners, their known blocks, Pairing/Public mode or the blacklist changed.
    public event Action? AccessChanged;
    /// (name, world).
    public event Action<string, string>? InviteReceived;
    /// (name, world, partId, licked|nothing): the answer to a lick you sent.
    public event Action<string, string, string, string>? LickResult;

    // Messaging and overlays.
    /// (channel, payload, senderName, senderWorld): a paired partner's plugin sent a message.
    public event Action<string, string, string, string>? Message;
    /// (owner, key, reason).
    public event Action<string, string, string>? OverlayFailed;
    /// (owner, key): removed by the wearer, by your call, or because its owner unloaded.
    public event Action<string, string>? OverlayRemoved;

    /// (hole, active, secondsLeft, cause, sourceName, sourceWorld): a creampie drip started, restarted or ended.
    /// Causes: creampie (start), timer, wash, lick, clean, plugin.
    public event Action<string, bool, int, string, string, string>? CreampieChanged;

    private readonly ICallGateSubscriber<int> apiVersion;
    private readonly ICallGateSubscriber<object> ready;
    private readonly ICallGateSubscriber<object> disposing;

    private readonly ICallGateSubscriber<string, string, string, string> sendLayer;
    private readonly ICallGateSubscriber<string, string, string, string> canLayer;
    private readonly ICallGateSubscriber<string, string, string, string> layerSelf;
    private readonly ICallGateSubscriber<int> getLayerCount;
    private readonly ICallGateSubscriber<List<(string, string, int, bool, bool)>> getParts;
    private readonly ICallGateSubscriber<Dictionary<string, int>> getStages;
    private readonly ICallGateSubscriber<string, int> getStage;
    private readonly ICallGateSubscriber<List<string>> getBlockedParts;
    private readonly ICallGateSubscriber<string> getAccessMode;
    private readonly ICallGateSubscriber<string, string, bool> isBlacklisted;
    private readonly ICallGateSubscriber<List<(string, string, List<string>)>> getPartners;
    private readonly ICallGateSubscriber<(bool, int, bool, bool, bool, bool)> getCleaning;
    private readonly ICallGateSubscriber<List<(string, string, bool)>> getNearby;

    private readonly ICallGateSubscriber<string, string, string, string> sendLick;
    private readonly ICallGateSubscriber<string, string, string> invite;
    private readonly ICallGateSubscriber<string, string> cleanSelf;
    private readonly ICallGateSubscriber<string> rinseSelf;
    private readonly ICallGateSubscriber<string, int, string, string, string> setStage;

    private readonly ICallGateSubscriber<string, string, string, string, string> sendMessage;
    private readonly ICallGateSubscriber<string, string, string, string, string, int, string> setOverlay;
    private readonly ICallGateSubscriber<string, string, string> removeOverlay;
    private readonly ICallGateSubscriber<string, string> clearOverlays;
    private readonly ICallGateSubscriber<List<(string, string, string)>> getOverlays;

    private readonly ICallGateSubscriber<string, string, string, object> onLayerReceived;
    private readonly ICallGateSubscriber<int, object> onLayersChanged;
    private readonly ICallGateSubscriber<string, int, int, string, string, string, object> onPartChanged;
    private readonly ICallGateSubscriber<bool, object> onCharacterChanged;
    private readonly ICallGateSubscriber<List<string>, object> onBlocksChanged;
    private readonly ICallGateSubscriber<object> onAccessChanged;
    private readonly ICallGateSubscriber<string, string, object> onInviteReceived;
    private readonly ICallGateSubscriber<string, string, string, string, object> onLickResult;
    private readonly ICallGateSubscriber<string, string, string, string, object> onMessage;
    private readonly ICallGateSubscriber<string, string, string, object> onOverlayFailed;
    private readonly ICallGateSubscriber<string, string, object> onOverlayRemoved;
    private readonly ICallGateSubscriber<string, bool, int, string, string, string, object> onCreampieChanged;
    private readonly ICallGateSubscriber<string, string, string, string> sendCreampie;
    private readonly ICallGateSubscriber<string, string, string, string> creampieSelf;
    private readonly ICallGateSubscriber<Dictionary<string, int>> getCreampies;

    public CummyIpc(IDalamudPluginInterface pi)
    {
        apiVersion = pi.GetIpcSubscriber<int>("Cummy.ApiVersion");
        ready = pi.GetIpcSubscriber<object>("Cummy.Ready");
        disposing = pi.GetIpcSubscriber<object>("Cummy.Disposing");

        sendLayer = pi.GetIpcSubscriber<string, string, string, string>("Cummy.SendLayer");
        canLayer = pi.GetIpcSubscriber<string, string, string, string>("Cummy.CanLayer");
        layerSelf = pi.GetIpcSubscriber<string, string, string, string>("Cummy.LayerSelf");
        getLayerCount = pi.GetIpcSubscriber<int>("Cummy.GetLayerCount");
        getParts = pi.GetIpcSubscriber<List<(string, string, int, bool, bool)>>("Cummy.GetParts");
        getStages = pi.GetIpcSubscriber<Dictionary<string, int>>("Cummy.GetStages");
        getStage = pi.GetIpcSubscriber<string, int>("Cummy.GetStage");
        getBlockedParts = pi.GetIpcSubscriber<List<string>>("Cummy.GetBlockedParts");
        getAccessMode = pi.GetIpcSubscriber<string>("Cummy.GetAccessMode");
        isBlacklisted = pi.GetIpcSubscriber<string, string, bool>("Cummy.IsBlacklisted");
        getPartners = pi.GetIpcSubscriber<List<(string, string, List<string>)>>("Cummy.GetPartners");
        getCleaning = pi.GetIpcSubscriber<(bool, int, bool, bool, bool, bool)>("Cummy.GetCleaning");
        getNearby = pi.GetIpcSubscriber<List<(string, string, bool)>>("Cummy.GetNearby");

        sendLick = pi.GetIpcSubscriber<string, string, string, string>("Cummy.SendLick");
        invite = pi.GetIpcSubscriber<string, string, string>("Cummy.Invite");
        cleanSelf = pi.GetIpcSubscriber<string, string>("Cummy.CleanSelf");
        rinseSelf = pi.GetIpcSubscriber<string>("Cummy.RinseSelf");
        setStage = pi.GetIpcSubscriber<string, int, string, string, string>("Cummy.SetStage");

        sendMessage = pi.GetIpcSubscriber<string, string, string, string, string>("Cummy.SendMessage");
        setOverlay = pi.GetIpcSubscriber<string, string, string, string, string, int, string>("Cummy.SetOverlay");
        removeOverlay = pi.GetIpcSubscriber<string, string, string>("Cummy.RemoveOverlay");
        clearOverlays = pi.GetIpcSubscriber<string, string>("Cummy.ClearOverlays");
        getOverlays = pi.GetIpcSubscriber<List<(string, string, string)>>("Cummy.GetOverlays");

        onLayerReceived = pi.GetIpcSubscriber<string, string, string, object>("Cummy.OnLayerReceived");
        onLayersChanged = pi.GetIpcSubscriber<int, object>("Cummy.OnLayersChanged");
        onPartChanged = pi.GetIpcSubscriber<string, int, int, string, string, string, object>("Cummy.OnPartChanged");
        onCharacterChanged = pi.GetIpcSubscriber<bool, object>("Cummy.OnCharacterChanged");
        onBlocksChanged = pi.GetIpcSubscriber<List<string>, object>("Cummy.OnBlocksChanged");
        onAccessChanged = pi.GetIpcSubscriber<object>("Cummy.OnAccessChanged");
        onInviteReceived = pi.GetIpcSubscriber<string, string, object>("Cummy.OnInviteReceived");
        onLickResult = pi.GetIpcSubscriber<string, string, string, string, object>("Cummy.OnLickResult");
        onMessage = pi.GetIpcSubscriber<string, string, string, string, object>("Cummy.OnMessage");
        onOverlayFailed = pi.GetIpcSubscriber<string, string, string, object>("Cummy.OnOverlayFailed");
        onOverlayRemoved = pi.GetIpcSubscriber<string, string, object>("Cummy.OnOverlayRemoved");
        onCreampieChanged = pi.GetIpcSubscriber<string, bool, int, string, string, string, object>("Cummy.OnCreampieChanged");
        sendCreampie = pi.GetIpcSubscriber<string, string, string, string>("Cummy.SendCreampie");
        creampieSelf = pi.GetIpcSubscriber<string, string, string, string>("Cummy.CreampieSelf");
        getCreampies = pi.GetIpcSubscriber<Dictionary<string, int>>("Cummy.GetCreampies");

        ready.Subscribe(OnReady);
        disposing.Subscribe(OnDisposing);
        onLayerReceived.Subscribe(OnLayerReceived);
        onLayersChanged.Subscribe(OnLayersChanged);
        onPartChanged.Subscribe(OnPartChanged);
        onCharacterChanged.Subscribe(OnCharacterChanged);
        onBlocksChanged.Subscribe(OnBlocksChanged);
        onAccessChanged.Subscribe(OnAccessChanged);
        onInviteReceived.Subscribe(OnInviteReceived);
        onLickResult.Subscribe(OnLickResult);
        onMessage.Subscribe(OnMessage);
        onOverlayFailed.Subscribe(OnOverlayFailed);
        onOverlayRemoved.Subscribe(OnOverlayRemoved);
        onCreampieChanged.Subscribe(OnCreampieChanged);
    }

    public void Dispose()
    {
        ready.Unsubscribe(OnReady);
        disposing.Unsubscribe(OnDisposing);
        onLayerReceived.Unsubscribe(OnLayerReceived);
        onLayersChanged.Unsubscribe(OnLayersChanged);
        onPartChanged.Unsubscribe(OnPartChanged);
        onCharacterChanged.Unsubscribe(OnCharacterChanged);
        onBlocksChanged.Unsubscribe(OnBlocksChanged);
        onAccessChanged.Unsubscribe(OnAccessChanged);
        onInviteReceived.Unsubscribe(OnInviteReceived);
        onLickResult.Unsubscribe(OnLickResult);
        onMessage.Unsubscribe(OnMessage);
        onOverlayFailed.Unsubscribe(OnOverlayFailed);
        onOverlayRemoved.Unsubscribe(OnOverlayRemoved);
        onCreampieChanged.Unsubscribe(OnCreampieChanged);
    }

    // Named handlers, so Dispose unsubscribes exactly what the constructor subscribed.
    private void OnReady() => Ready?.Invoke();
    private void OnDisposing() => Disposing?.Invoke();
    private void OnLayerReceived(string part, string name, string world) => LayerReceived?.Invoke(part, name, world);
    private void OnLayersChanged(int count) => LayersChanged?.Invoke(count);
    private void OnPartChanged(string part, int from, int to, string cause, string name, string world)
        => PartChanged?.Invoke(part, from, to, cause, name, world);
    private void OnCharacterChanged(bool loggedIn) => CharacterChanged?.Invoke(loggedIn);
    private void OnBlocksChanged(List<string> blocked) => BlocksChanged?.Invoke(blocked);
    private void OnAccessChanged() => AccessChanged?.Invoke();
    private void OnInviteReceived(string name, string world) => InviteReceived?.Invoke(name, world);
    private void OnLickResult(string name, string world, string part, string result) => LickResult?.Invoke(name, world, part, result);
    private void OnMessage(string channel, string payload, string name, string world) => Message?.Invoke(channel, payload, name, world);
    private void OnOverlayFailed(string owner, string key, string reason) => OverlayFailed?.Invoke(owner, key, reason);
    private void OnOverlayRemoved(string owner, string key) => OverlayRemoved?.Invoke(owner, key);
    private void OnCreampieChanged(string hole, bool active, int secondsLeft, string cause, string name, string world)
        => CreampieChanged?.Invoke(hole, active, secondsLeft, cause, name, world);

    /// Cummy's API version, or 0 while Cummy isn't loaded. Safe from any thread.
    public int ApiVersion()
    {
        try { return apiVersion.InvokeFunc(); }
        catch (Exception) { return 0; }
    }

    /// Cummy is loaded and speaks at least this client's version.
    public bool IsAvailable => ApiVersion() >= ClientVersion;

    private static T Call<T>(Func<T> call, T unavailable)
    {
        try { return call(); }
        catch (IpcNotReadyError) { return unavailable; }
        catch (IpcTypeMismatchError) { return unavailable; }
    }

    private const string Na = CummyStrings.Unavailable;

    // ── Layering (versions 1-5) ──────────────────────────────────────────────

    /// Layers a partner (or in Public mode anyone in range) with the normal tell. sent, not-paired, blacklisted,
    /// out-of-range, blocked, queue-full or unknown-part.
    public string SendLayer(string name, string world, string partId) => Call(() => sendLayer.InvokeFunc(name, world, partId), Na);

    /// What SendLayer would answer (ok instead of sent), sending nothing.
    public string CanLayer(string name, string world, string partId) => Call(() => canLayer.InvokeFunc(name, world, partId), Na);

    /// Layers the wearer as if that player had (Full Body "fb" too). layered, advanced, full, blocked, blacklisted,
    /// unknown-part or no-character.
    public string LayerSelf(string partId, string sourceName, string sourceWorld)
        => Call(() => layerSelf.InvokeFunc(partId, sourceName, sourceWorld), Na);

    public int GetLayerCount() => Call(() => getLayerCount.InvokeFunc(), 0);

    // ── Reads ────────────────────────────────────────────────────────────────

    /// Every layer target: (id, name, stages, facial, isTarget). Full Body is the one target that isn't a stored part.
    public List<(string Id, string Name, int Stages, bool Facial, bool IsTarget)> GetParts()
        => Call(() => getParts.InvokeFunc(), []).ConvertAll(p => (p.Item1, p.Item2, p.Item3, p.Item4, p.Item5));

    /// Layered parts' stages by part id (clean parts left out).
    public Dictionary<string, int> GetStages() => Call(() => getStages.InvokeFunc(), new Dictionary<string, int>());

    /// One part's stage: 0 clean, -1 unknown id.
    public int GetStage(string partId) => Call(() => getStage.InvokeFunc(partId), -1);

    public List<string> GetBlockedParts() => Call(() => getBlockedParts.InvokeFunc(), []);

    /// pairing or public ("" while Cummy isn't loaded).
    public string GetAccessMode() => Call(() => getAccessMode.InvokeFunc(), "");

    public bool IsBlacklisted(string name, string world) => Call(() => isBlacklisted.InvokeFunc(name, world), false);

    public List<(string Name, string World, List<string> Blocked)> GetPartners()
        => Call(() => getPartners.InvokeFunc(), []).ConvertAll(p => (p.Item1, p.Item2, p.Item3));

    public (bool DryOff, int DryMinutes, bool WashWater, bool WashRain, bool Drip, bool ManualClean) GetCleaning()
        => Call(() => getCleaning.InvokeFunc(), default);

    /// Players in layer range the wearer could layer: (name, world, partner).
    public List<(string Name, string World, bool Partner)> GetNearby()
        => Call(() => getNearby.InvokeFunc(), []).ConvertAll(p => (p.Item1, p.Item2, p.Item3));

    // ── Actions ──────────────────────────────────────────────────────────────

    /// sent, not-paired, blacklisted, out-of-range, queue-full or unknown-part; the result arrives as LickResult.
    public string SendLick(string name, string world, string partId) => Call(() => sendLick.InvokeFunc(name, world, partId), Na);

    /// sent, already-paired, pending, blacklisted, queue-full or no-character.
    public string Invite(string name, string world) => Call(() => invite.InvokeFunc(name, world), Na);

    /// A part id, "pussy"/"ass" (version 6: ends that creampie), or "all" (also ends both creampies). cleaned,
    /// already-clean, locked, unknown-part or no-character.
    public string CleanSelf(string partId) => Call(() => cleanSelf.InvokeFunc(partId), Na);

    /// rinsed, already-clean, locked or no-character.
    public string RinseSelf() => Call(() => rinseSelf.InvokeFunc(), Na);

    /// set, unchanged, blocked, blacklisted, locked, invalid-stage, unknown-part or no-character.
    public string SetStage(string partId, int stage, string sourceName, string sourceWorld)
        => Call(() => setStage.InvokeFunc(partId, stage, sourceName, sourceWorld), Na);

    // ── Creampies (version 6) ────────────────────────────────────────────────

    /// Creampies a partner (or in Public mode anyone in range) with the normal tell. sent, not-paired, blacklisted,
    /// out-of-range, blocked, queue-full or unknown-hole.
    public string SendCreampie(string name, string world, string hole) => Call(() => sendCreampie.InvokeFunc(name, world, hole), Na);

    /// Creampies the wearer as if that player had (no pairing, tell or range). creampied, refreshed, blocked,
    /// blacklisted, unknown-hole or no-character.
    public string CreampieSelf(string hole, string sourceName, string sourceWorld)
        => Call(() => creampieSelf.InvokeFunc(hole, sourceName, sourceWorld), Na);

    /// Seconds left per dripping hole.
    public Dictionary<string, int> GetCreampies() => Call(() => getCreampies.InvokeFunc(), new Dictionary<string, int>());

    // ── Messaging ────────────────────────────────────────────────────────────

    /// To a paired partner's copy of your plugin. Channel: 1-16 of [a-z0-9]. Payload: space-separated [a-z0-9] tokens,
    /// at most 200 characters. sent, not-paired, blacklisted, invalid, disabled, queue-full or no-character.
    public string SendMessage(string name, string world, string channel, string payload)
        => Call(() => sendMessage.InvokeFunc(name, world, channel, payload), Na);

    // ── Overlays ─────────────────────────────────────────────────────────────

    /// Shows (or replaces) your overlay <paramref name="key"/>. <paramref name="owner"/> is your plugin's internal
    /// name. <paramref name="target"/> is a part id (Bibo layout for body parts, face-paint layout for Face) or "full"
    /// (a whole Bibo body image). <paramref name="normalPath"/> may be "" for no relief. Opacity 1-100.
    /// accepted, invalid, unknown-part, too-many or disabled; decode failures arrive as OverlayFailed.
    public string SetOverlay(string owner, string key, string target, string diffusePath, string normalPath, int opacity)
        => Call(() => setOverlay.InvokeFunc(owner, key, target, diffusePath, normalPath, opacity), Na);

    /// removed or unchanged.
    public string RemoveOverlay(string owner, string key) => Call(() => removeOverlay.InvokeFunc(owner, key), Na);

    /// removed or unchanged.
    public string ClearOverlays(string owner) => Call(() => clearOverlays.InvokeFunc(owner), Na);

    /// (owner, key, target) of every plugin overlay showing.
    public List<(string Owner, string Key, string Target)> GetOverlays()
        => Call(() => getOverlays.InvokeFunc(), []).ConvertAll(o => (o.Item1, o.Item2, o.Item3));
}
