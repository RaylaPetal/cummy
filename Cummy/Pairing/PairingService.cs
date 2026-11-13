using System;
using System.Collections.Generic;
using System.Linq;
using Cummy.Ipc;
using Cummy.Messaging;
using Cummy.State;

namespace Cummy.Pairing;

/// What sending a pairing invite did; only <see cref="Sent"/> queued a tell.
public enum InviteOutcome { Sent, AlreadyPaired, Pending, Blacklisted, QueueFull, NoCharacter }

/// Why a send to a player can't go out, checked before sending (null from the checks = it may).
public enum SendRefusal { NotPaired, Blacklisted, OutOfRange }

public sealed record IncomingInvite(string Name, string World, string Nonce, int BlockMask, DateTime ExpiresUtc)
{
    public string Key => $"{Name}@{World}";
}

/// Tell-handshake pairing (invite → accept, unpair either way) and the layer and lick commands that paired peers, or
/// anyone while the wearer is in Public mode, may send from close by (<see cref="Proximity"/>); the blacklist refuses a
/// player everything (<see cref="MayAct"/>). Outgoing tells are the direct result of a user click, except the
/// `lkd` reply to a partner's lick that cleaned something, and layers, licks and invites another plugin requests through
/// <see cref="Cummy.Ipc.CummyApi"/>.
public sealed class PairingService : IDisposable
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(5);

    /// How long a lick waits for the partner's `lkd` before it counts as "nothing to lick there".
    public static readonly TimeSpan LickTimeout = TimeSpan.FromSeconds(30);

    private readonly Configuration config;
    private readonly LayerState layers;
    private readonly ChatSender sender;
    private readonly ChatListener listener;
    private readonly CreampieService creampies;

    /// Invites we sent, by peer key, awaiting their accept.
    private readonly Dictionary<string, (string Nonce, DateTime ExpiresUtc)> outgoing = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<IncomingInvite> incoming = new();

    /// Licks we sent, by peer key and part, awaiting their `lkd`. Memory only.
    private readonly Dictionary<(string PeerKey, BodyPart Part), DateTime> pendingLicks = new();

    /// Raised when an invite that isn't ignored arrives, with who sent it, so the prompt window can open.
    public event Action<string, string>? InviteReceived;

    /// A partner was added or removed, a partner's known blocks changed, Public mode was switched, or the blacklist
    /// changed.
    public event Action? AccessChanged;

    /// The wearer's own blocks changed.
    public event Action? BlocksChanged;

    /// A lick we sent got its answer: (name, world, part, licked). False when no `lkd` came within
    /// <see cref="LickTimeout"/>, i.e. there was nothing to lick.
    public event Action<string, string, BodyPart, bool>? LickResult;

    /// Raised when a partner's layer landed on a clean part (not for blocked or already-covered parts, nor on login).
    public event Action<BodyPart>? LayerAccepted;

    /// Raised with <see cref="LayerAccepted"/>, with who the layer came from: (part, source name, source world).
    public event Action<BodyPart, string, string>? LayerReceived;

    public PairingService(Configuration config, LayerState layers, ChatSender sender, ChatListener listener,
                          CreampieService creampies)
    {
        this.creampies = creampies;
        this.config = config;
        this.layers = layers;
        this.sender = sender;
        this.listener = listener;
        listener.Received += OnReceived;
    }

    public void Dispose() => listener.Received -= OnReceived;

    private CharacterData? Me => layers.Current;

    public IReadOnlyList<PeerRecord> Peers => Me?.Peers ?? (IReadOnlyList<PeerRecord>)Array.Empty<PeerRecord>();

    public PeerRecord? FindPeer(string name, string world) => Me?.FindPeer(name, world);

    /// Public mode on the logged-in character: unpaired players may layer and lick it, and it may layer them.
    public bool PublicLayering => Me?.PublicLayering ?? false;

    public IReadOnlyList<BlacklistEntry> BlacklistEntries
        => Me?.Blacklist ?? (IReadOnlyList<BlacklistEntry>)Array.Empty<BlacklistEntry>();

    public bool IsBlacklisted(string name, string world) => Me?.IsBlacklisted(name, world) ?? false;

    /// Whether `name@world` may layer or lick <paramref name="me"/>: never when blacklisted; otherwise a paired peer
    /// always, and anyone in Public mode. Range is checked separately.
    private static bool MayAct(CharacterData me, string name, string world)
        => !me.IsBlacklisted(name, world) && (me.PublicLayering || me.FindPeer(name, world) is not null);

    /// A target for the send paths: the paired peer, else (only in Public mode) a stranger that is never stored, with
    /// no known blocks. Null for a blacklisted player or a stranger in Pairing mode.
    public PeerRecord? TargetFor(string name, string world)
    {
        if (Me is not { } me || me.IsBlacklisted(name, world)) return null;
        if (me.FindPeer(name, world) is { } peer) return peer;
        return me.PublicLayering ? new PeerRecord { Name = name, World = world } : null;
    }

    public IReadOnlyList<IncomingInvite> PendingInvites
    {
        get
        {
            incoming.RemoveAll(i => i.ExpiresUtc < DateTime.UtcNow);
            return incoming;
        }
    }

    public bool IsInvitePending(string name, string world)
        => outgoing.TryGetValue($"{name}@{world}", out var o) && o.ExpiresUtc > DateTime.UtcNow;

    // ── Actions (user clicks) ──────────────────────────────────────────────────

    /// The UI's Pair button: <see cref="TryInvite"/> with the outcome told to the wearer.
    public void Invite(string name, string world)
    {
        switch (TryInvite(name, world))
        {
            case InviteOutcome.Sent:
                Notifier.Info($"Pairing invite sent to {name}@{world}.");
                break;
            case InviteOutcome.QueueFull:
                Notifier.Error($"Couldn't send the pairing invite to {name}.");
                break;
            case InviteOutcome.Blacklisted:
                Notifier.Info($"{name}@{world} is on your blacklist.");
                break;
        }
    }

    /// Sends a pairing invite to `name@world` unless they're paired, already invited, or blacklisted. Quiet: the
    /// caller tells the user.
    public InviteOutcome TryInvite(string name, string world)
    {
        if (Me is not { } me) return InviteOutcome.NoCharacter;
        if (me.IsBlacklisted(name, world)) return InviteOutcome.Blacklisted;
        if (FindPeer(name, world) is not null) return InviteOutcome.AlreadyPaired;
        if (IsInvitePending(name, world)) return InviteOutcome.Pending;
        var nonce = Protocol.NewNonce();
        if (!sender.SendTell(name, world, Protocol.Invite(nonce, me.BlockMask))) return InviteOutcome.QueueFull;
        outgoing[$"{name}@{world}"] = (nonce, DateTime.UtcNow + InviteLifetime);
        return InviteOutcome.Sent;
    }

    /// Whether a layer, lick or creampie to `name@world` may go out from here, within <paramref name="range"/>: null
    /// when it may, with the target to send to (a partner, or in Public mode a transient stranger). A stranger whose
    /// presence tag says Pairing mode would ignore it (not paired); one with a tag carries its blocks.
    public SendRefusal? CheckSend(string name, string world, float range, out PeerRecord? target)
    {
        target = null;
        if (IsBlacklisted(name, world)) return SendRefusal.Blacklisted;
        if (TargetFor(name, world) is not { } peer) return SendRefusal.NotPaired;
        if (FindPeer(name, world) is null && Proximity.TagOf(name, world) is { } tag)
        {
            if (!tag.Public) return SendRefusal.NotPaired;
            peer.BlockMask = tag.BlockMask;
        }
        if (!Proximity.Within(peer, range)) return SendRefusal.OutOfRange;
        target = peer;
        return null;
    }

    public void AcceptInvite(IncomingInvite invite)
    {
        incoming.Remove(invite);
        if (Me is not { } me || invite.ExpiresUtc < DateTime.UtcNow) return;
        if (!sender.SendTell(invite.Name, invite.World, Protocol.Accept(invite.Nonce, me.BlockMask)))
        {
            Notifier.Error($"Couldn't send the accept to {invite.Name}.");
            return;
        }
        AddPeer(invite.Name, invite.World, invite.BlockMask);
        SendFullBlocks(invite.Name, invite.World, me.BlockMask);
        Notifier.Info($"You are now paired with {invite.Key}.");
    }

    public void DeclineInvite(IncomingInvite invite) => incoming.Remove(invite);

    public void SetPublicLayering(bool on)
    {
        if (Me is not { } me || me.PublicLayering == on) return;
        me.PublicLayering = on;
        config.Save();
        AccessChanged?.Invoke();
    }

    /// Refuse `name@world` everything from now on: a partner is unpaired (one `unp`), and invites either way are
    /// dropped so a late accept can't pair them again.
    public void Blacklist(string name, string world)
    {
        if (Me is not { } me) return;
        var key = $"{name}@{world}";
        outgoing.Remove(key);
        incoming.RemoveAll(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (me.FindPeer(name, world) is { } peer) Unpair(peer);
        if (me.IsBlacklisted(name, world)) return;
        me.Blacklist.Add(new BlacklistEntry { Name = name, World = world, AddedAtUtc = DateTime.UtcNow });
        config.Save();
        AccessChanged?.Invoke();
        Notifier.Info($"Blacklisted {key}.");
    }

    public void Unblacklist(BlacklistEntry entry)
    {
        if (Me is not { } me || !me.Blacklist.Remove(entry)) return;
        config.Save();
        AccessChanged?.Invoke();
        Notifier.Info($"Removed {entry.Key} from your blacklist.");
    }

    public void Unpair(PeerRecord peer)
    {
        if (Me is not { } me) return;
        me.Peers.Remove(peer);
        config.Save();
        AccessChanged?.Invoke();
        // Best effort: the peer is gone locally even if the tell can't be delivered.
        sender.SendTell(peer.Name, peer.World, Protocol.Unpair());
        Notifier.Info($"Unpaired from {peer.Key}.");
    }

    public bool SendLayer(PeerRecord peer, BodyPart part)
    {
        if (!sender.SendTell(peer.Name, peer.World, Protocol.Layer(part)))
        {
            Notifier.Error("Slow down - too many layers waiting to send.");
            return false;
        }
        Notifier.Info($"You came on {peer.Name}'s {part.DisplayName().ToLowerInvariant()}.");
        return true;
    }

    /// Creampie a partner's (or, in Public mode, a stranger's) <paramref name="hole"/>. Their Cummy applies its own
    /// rules when it arrives.
    public bool SendCreampie(PeerRecord peer, Hole hole)
    {
        if (!sender.SendTell(peer.Name, peer.World, Protocol.Creampie(hole)))
        {
            Notifier.Error("Slow down - too many messages waiting to send.");
            return false;
        }
        Notifier.Info($"You filled {peer.Name}'s {hole.DisplayName().ToLowerInvariant()} with cum.");
        return true;
    }

    /// Lick <paramref name="part"/> off a partner. What lands on you is decided when their `lkd` arrives
    /// (<see cref="OnLickConfirmed"/>); without one within <see cref="LickTimeout"/> there was nothing to lick.
    public bool SendLick(PeerRecord peer, BodyPart part)
    {
        if (!sender.SendTell(peer.Name, peer.World, Protocol.Lick(part)))
        {
            Notifier.Error("Slow down - too many messages waiting to send.");
            return false;
        }
        var key = (peer.Key, part);
        var expires = DateTime.UtcNow + LickTimeout;
        pendingLicks[key] = expires;
        Plugin.Framework.RunOnTick(() =>
        {
            // A later lick of the same part refreshed the entry: its own check reports it.
            if (!pendingLicks.TryGetValue(key, out var current) || current != expires) return;
            pendingLicks.Remove(key);
            Notifier.Info($"Nothing to lick on {peer.Name}'s {part.DisplayName().ToLowerInvariant()}.");
            LickResult?.Invoke(peer.Name, peer.World, part, false);
        }, LickTimeout + TimeSpan.FromSeconds(1));
        return true;
    }

    /// The wearer changed whether <paramref name="changed"/> is blocked: tell every peer, with each block message
    /// whose bits cover that part (`blk` for every version, `blx` for versions with facial parts, `bly` for current).
    public void BroadcastBlocks(BodyPart changed)
    {
        if (Me is not { } me) return;
        BlocksChanged?.Invoke();
        foreach (var peer in me.Peers)
        {
            if ((changed.Bit() & BodyParts.LegacyBodyMask) != 0) sender.SendTell(peer.Name, peer.World, Protocol.Block(me.BlockMask));
            if ((changed.Bit() & BodyParts.ExtendedMask) != 0) sender.SendTell(peer.Name, peer.World, Protocol.BlockExtended(me.BlockMask));
            sender.SendTell(peer.Name, peer.World, Protocol.BlockAll(me.BlockMask));
        }
    }

    /// The wearer changed a block only `bly` carries (a creampie block): tell every peer with it alone.
    public void BroadcastAllBlocks()
    {
        if (Me is not { } me) return;
        BlocksChanged?.Invoke();
        foreach (var peer in me.Peers)
            sender.SendTell(peer.Name, peer.World, Protocol.BlockAll(me.BlockMask));
    }

    /// After pairing: the blocks `inv`/`acc` could not carry, for versions that understand them (others ignore them).
    private void SendFullBlocks(string name, string world, int blockMask)
    {
        sender.SendTell(name, world, Protocol.BlockExtended(blockMask));
        sender.SendTell(name, world, Protocol.BlockAll(blockMask));
    }

    // ── Incoming ───────────────────────────────────────────────────────────────

    private void OnReceived(string name, string world, ProtocolMessage message)
    {
        if (Me is not { } me) return;
        switch (message.Verb)
        {
            case Verb.Invite:
                if (me.IsBlacklisted(name, world))
                {
                    Plugin.Log.Information($"Ignored an invite from blacklisted {name}@{world}.");
                    return;
                }
                if (me.FindPeer(name, world) is not null) return;
                incoming.RemoveAll(i => i.Key.Equals($"{name}@{world}", StringComparison.OrdinalIgnoreCase));
                incoming.Add(new IncomingInvite(name, world, message.Nonce!, message.BlockMask, DateTime.UtcNow + InviteLifetime));
                Notifier.Info($"{name}@{world} wants to pair with you.");
                InviteReceived?.Invoke(name, world);
                break;

            case Verb.Accept:
                var key = $"{name}@{world}";
                if (!outgoing.TryGetValue(key, out var pending)
                    || pending.ExpiresUtc < DateTime.UtcNow
                    || pending.Nonce != message.Nonce)
                {
                    Plugin.Log.Information($"Ignored an accept from {key} that matches no pending invite.");
                    return;
                }
                outgoing.Remove(key);
                AddPeer(name, world, message.BlockMask);
                // Now that they know us, send the blocks `inv` could not carry; older versions ignore them.
                SendFullBlocks(name, world, me.BlockMask);
                Notifier.Info($"{key} accepted - you are now paired.");
                break;

            case Verb.Unpair:
                if (me.FindPeer(name, world) is not { } gone) return;
                me.Peers.Remove(gone);
                config.Save();
                Notifier.Info($"{gone.Key} unpaired from you.");
                AccessChanged?.Invoke();
                break;

            case Verb.Block or Verb.BlockExtended or Verb.BlockAll:
                // Each message replaces only the bits it covers, keeping what another block message told us, so a
                // `blk` arriving after a `bly` doesn't forget blocks it can't express.
                if (me.FindPeer(name, world) is not { } blocker) return;
                var covers = Protocol.Covers(message.Verb);
                var known = (blocker.BlockMask & ~covers) | (message.BlockMask & covers);
                if (known == blocker.BlockMask) return;
                blocker.BlockMask = known;
                config.Save();
                AccessChanged?.Invoke();
                break;

            case Verb.Layer:
                if (!MayAct(me, name, world))
                {
                    Plugin.Log.Information($"Ignored a layer from {Refused(me, name, world)} {name}@{world}.");
                    return;
                }
                if (!Proximity.Within(name, world, Proximity.LayerRange + Proximity.ReceiveSlack))
                {
                    Plugin.Log.Information($"Ignored a layer from {name}@{world}: not within {Proximity.LayerRange} yalms.");
                    return;
                }
                ApplyLayer(me, name, world, message.Part);
                break;

            case Verb.Lick:
                if (!MayAct(me, name, world))
                {
                    Plugin.Log.Information($"Ignored a lick from {Refused(me, name, world)} {name}@{world}.");
                    return;
                }
                if (!Proximity.Within(name, world, Proximity.LickRange + Proximity.ReceiveSlack))
                {
                    Plugin.Log.Information($"Ignored a lick from {name}@{world}: not within {Proximity.LickRange} yalms.");
                    return;
                }
                if (BeLicked(name, world, message.Part) && !sender.SendTell(name, world, Protocol.Licked(message.Part)))
                    Plugin.Log.Warning($"Couldn't confirm {name}@{world}'s lick of {message.Part}: send queue full.");
                break;

            case Verb.Creampie:
                if (!MayAct(me, name, world))
                {
                    Plugin.Log.Information($"Ignored a creampie from {Refused(me, name, world)} {name}@{world}.");
                    return;
                }
                if (!Proximity.Within(name, world, Proximity.LayerRange + Proximity.ReceiveSlack))
                {
                    Plugin.Log.Information($"Ignored a creampie from {name}@{world}: not within {Proximity.LayerRange} yalms.");
                    return;
                }
                ApplyCreampie(me, name, world, message.Hole);
                break;

            case Verb.Licked:
                if (!MayAct(me, name, world))
                {
                    Plugin.Log.Information($"Ignored a lick confirmation from {Refused(me, name, world)} {name}@{world}.");
                    return;
                }
                // Keyed like SendLick's pending entry (PeerRecord.Key), so a stranger's reply matches their own lick.
                OnLickConfirmed(me, $"{name}@{world}", name, world, message.Part);
                break;
        }
    }

    /// Why <see cref="MayAct"/> refused, for the log.
    private static string Refused(CharacterData me, string name, string world)
        => me.IsBlacklisted(name, world) ? "blacklisted" : "unpaired";

    /// For another plugin that decided a layer itself (Corruption, when a scene partner's climax counts): layers the local
    /// player as if <paramref name="name"/>@<paramref name="world"/> had, with no pairing and no tell. The wearer's blocks,
    /// stages, notification and events are the same as for a partner's layer. Full Body layers every unblocked part.
    /// Returns layered, advanced, full, blocked, blacklisted or no-character.
    public string LayerSelf(BodyPart part, string name, string world)
    {
        if (Me is not { } me) return ApiStrings.NoCharacter;
        if (me.IsBlacklisted(name, world)) return ApiStrings.Blacklisted;
        if (part == BodyPart.FullBody)
        {
            // Like a partner's Full Body: every unblocked part one stage; only blocking them all refuses it.
            if ((BodyParts.LayerTargetsMask & ~me.BlockMask) == 0) return ApiStrings.Blocked;
            var total = BodyParts.LayerTargets.Sum(layers.Stage);
            ApplyFullBody(me, name, world);
            return BodyParts.LayerTargets.Sum(layers.Stage) == total ? ApiStrings.Full : ApiStrings.Layered;
        }
        if (part.IsBlockedBy(me.BlockMask)) return ApiStrings.Blocked;
        var before = layers.Stage(part);
        ApplyLayer(me, name, world, part);
        var after = layers.Stage(part);
        return after == before ? ApiStrings.Full : before == 0 ? ApiStrings.Layered : ApiStrings.Advanced;
    }

#if DEBUG
    /// Debug builds only: behave as if a paired partner's `cmy1 lay` tell for <paramref name="part"/> had arrived.
    /// The real wire text is built and parsed, then handled exactly like a verified tell (blocks included); only the
    /// chat transport and the paired-sender check are skipped.
    public void SimulateLayerFromPartner(BodyPart part)
    {
        if (Me is not { } me) return;
        if (Protocol.Parse(Protocol.Layer(part)) is not { Verb: Verb.Layer } message)
        {
            Notifier.Error($"[debug] {part} did not survive the wire format.");
            return;
        }
        if (message.Part.IsBlockedBy(me.BlockMask))
            Notifier.Info($"[debug] {message.Part.DisplayName()} is blocked in your settings, so a partner's layer would be ignored.");
        ApplyLayer(me, "Debug Partner", "Debug", message.Part);
    }

    /// Debug builds only: behave as if `name@world`'s `cmy1 lay` tell had arrived from close by, through the real
    /// pairing, Public-mode and blacklist checks (only the chat transport and the range check are skipped).
    public void SimulateLayerFrom(string name, string world, BodyPart part)
    {
        if (Me is not { } me) return;
        if (!MayAct(me, name, world))
        {
            Notifier.Info($"[debug] Ignored: {name}@{world} is {Refused(me, name, world)}.");
            return;
        }
        ApplyLayer(me, name, world, part);
    }

    /// Debug builds only: behave as if `name@world`'s creampie tell had arrived from close by, through the real wire
    /// format, access and block checks (only the chat transport and the range check are skipped).
    public void SimulateCreampieFrom(string name, string world, Hole hole)
    {
        if (Me is not { } me) return;
        if (Protocol.Parse(Protocol.Creampie(hole)) is not { Verb: Verb.Creampie } message)
        {
            Notifier.Error($"[debug] {hole} did not survive the wire format.");
            return;
        }
        if (!MayAct(me, name, world))
        {
            Notifier.Info($"[debug] Ignored: {name}@{world} is {Refused(me, name, world)}.");
            return;
        }
        Notifier.Info($"[debug] Creampie: {ApplyCreampie(me, name, world, message.Hole)}.");
    }

    /// Debug builds only: behave as if `name@world` had sent a pairing invite (a fresh nonce), through the real checks.
    public void SimulateInviteFrom(string name, string world)
    {
        var before = incoming.Count;
        OnReceived(name, world, new ProtocolMessage(Verb.Invite, Nonce: Protocol.NewNonce()));
        if (incoming.Count == before && Me is { } me && me.IsBlacklisted(name, world))
            Notifier.Info($"[debug] Ignored: {name}@{world} is blacklisted.");
    }

    /// Debug builds only: behave as if a paired, close partner had licked <paramref name="part"/>. The `lkd` reply
    /// is shown instead of sent.
    public void SimulateLickFromPartner(BodyPart part)
    {
        if (Me is null) return;
        if (Protocol.Parse(Protocol.Lick(part)) is not { Verb: Verb.Lick } message)
        {
            Notifier.Error($"[debug] {part} did not survive the wire format.");
            return;
        }
        Notifier.Info(BeLicked("Debug Partner", "Debug", message.Part)
            ? $"[debug] Would reply: {Protocol.Licked(message.Part)}"
            : $"[debug] Your {part.DisplayName().ToLowerInvariant()} is clean: no reply.");
    }

    /// Debug builds only: behave as if Debug Partner confirmed a lick of <paramref name="part"/>. With
    /// <paramref name="solicited"/> a matching pending lick is recorded first; without, it must be ignored.
    public void SimulateLickConfirmation(BodyPart part, bool solicited)
    {
        if (Me is not { } me) return;
        if (Protocol.Parse(Protocol.Licked(part)) is not { Verb: Verb.Licked } message)
        {
            Notifier.Error($"[debug] {part} did not survive the wire format.");
            return;
        }
        const string key = "Debug Partner@Debug";
        if (solicited) pendingLicks[(key, message.Part)] = DateTime.UtcNow + LickTimeout;
        else Notifier.Info("[debug] Unsolicited confirmation: should change nothing.");
        OnLickConfirmed(me, key, "Debug Partner", "Debug", message.Part);
    }
#endif

    /// A partner's verified lick: cleans the whole part (every stage) if it is layered. True when it did, so the
    /// caller confirms with `lkd`. Blocks don't apply: they only stop layering.
    private bool BeLicked(string name, string world, BodyPart part)
    {
        var cleaned = layers.Clean(part, new LayerChange(ChangeCause.Lick, name, world));
        // Licking Groin/Butt also licks out a creampie, whether or not that part had a layer.
        var licked = Holes.ForPart(part) is { } hole && creampies.End(hole, CreampieCause.Lick, name, world);
        if (!cleaned && !licked) return false;
        Notifier.Info(licked
            ? $"{name} licked your {Holes.ForPart(part)!.Value.DisplayName().ToLowerInvariant()} clean - no more dripping."
            : $"{name} licked your {part.DisplayName().ToLowerInvariant()} clean.");
        return true;
    }

    /// A creampie from `name@world` that passed the access and range checks (or another plugin's, through
    /// <see cref="CreampieSelf"/>): unless the hole is blocked, starts or restarts its drip and layers Groin/Butt one
    /// step as a layer from them would (skipped when that part is blocked). Returns creampied, refreshed or blocked.
    private string ApplyCreampie(CharacterData me, string name, string world, Hole hole)
    {
        if (hole.IsBlockedBy(me.BlockMask))
        {
            Plugin.Log.Information($"Ignored a blocked {hole.ApiName()} creampie from {name}@{world}.");
            return ApiStrings.Blocked;
        }
        if (!creampies.Start(hole, name, world, out var wasRunning)) return ApiStrings.NoCharacter;

        var part = hole.Part();
        var note = "";
        if (!part.IsBlockedBy(me.BlockMask))
        {
            var outcome = layers.Layer(part, new LayerChange(ChangeCause.Creampie, name, world));
            if (outcome is LayerOutcome.Layered or LayerOutcome.Advanced)
            {
                note = $" ({part.DisplayName().ToLowerInvariant()} at stage {layers.Stage(part)} of {part.StageCount()})";
                LayerAccepted?.Invoke(part);
                LayerReceived?.Invoke(part, name, world);
            }
        }
        var minutes = (int)config.CreampieDuration.TotalMinutes;
        Notifier.Info(wasRunning
            ? $"{name} pumped another load into your {hole.DisplayName().ToLowerInvariant()} - leaking for {minutes} more minutes{note}."
            : $"{name} filled your {hole.DisplayName().ToLowerInvariant()} with cum - leaking for {minutes} minutes{note}.");
        return wasRunning ? ApiStrings.Refreshed : ApiStrings.Creampied;
    }

    /// For another plugin that decided a creampie itself: as if `name@world` had creampied the wearer, with no pairing,
    /// tell or range, under the wearer's blocks and blacklist. Returns creampied, refreshed, blocked, blacklisted or
    /// no-character.
    public string CreampieSelf(Hole hole, string name, string world)
    {
        if (Me is not { } me) return ApiStrings.NoCharacter;
        if (me.IsBlacklisted(name, world)) return ApiStrings.Blacklisted;
        return ApplyCreampie(me, name, world, hole);
    }

    /// A partner's `lkd`: only for a lick we sent them of that part, still waiting. Then it lands on our face: a
    /// clean face gets layered at stage 1, a covered one (any stage; licks never advance it) spills a stage down the
    /// chest; licking their face (a kiss) cleans ours instead. A blocked face (or chest, for the spill) takes nothing.
    private void OnLickConfirmed(CharacterData me, string peerKey, string name, string world, BodyPart part)
    {
        if (!pendingLicks.Remove((peerKey, part), out var expires) || expires < DateTime.UtcNow)
        {
            Plugin.Log.Information($"Ignored a lick confirmation from {peerKey} for {part} that matches no pending lick.");
            return;
        }
        LickResult?.Invoke(name, world, part, true);

        var partName = part.DisplayName().ToLowerInvariant();
        if (part == BodyPart.Face)
        {
            layers.Clean(BodyPart.Face, new LayerChange(ChangeCause.Kiss, name, world));
            Notifier.Info($"You kissed {name} - both your faces are clean.");
            return;
        }
        if (BodyPart.Face.IsBlockedBy(me.BlockMask))
        {
            Notifier.Info($"You licked {name}'s {partName} clean.");
            return;
        }
        if (!layers.IsLayered(BodyPart.Face))
        {
            layers.Layer(BodyPart.Face, new LayerChange(ChangeCause.Lick, name, world));
            Notifier.Info($"You licked {name}'s {partName} clean - it's on your face now.");
            return;
        }

        layers.RestartDrying(BodyPart.Face);
        var spilled = !BodyPart.Chest.IsBlockedBy(me.BlockMask)
                      && layers.Layer(BodyPart.Chest, new LayerChange(ChangeCause.Lick, name, world)) is LayerOutcome.Layered or LayerOutcome.Advanced;
        Notifier.Info(spilled
            ? $"You licked {name}'s {partName} clean - your face is already covered, so it spills down your chest."
            : $"You licked {name}'s {partName} clean.");
    }

    private void ApplyLayer(CharacterData me, string name, string world, BodyPart part)
    {
        if (part == BodyPart.FullBody)
        {
            ApplyFullBody(me, name, world);
            return;
        }
        if (part.IsBlockedBy(me.BlockMask))
        {
            Plugin.Log.Information($"Ignored a blocked {part} layer from {name}@{world}.");
            return;
        }
        var partName = part.DisplayName().ToLowerInvariant();
        var outcome = layers.Layer(part, new LayerChange(ChangeCause.Layer, name, world));
        var stage = layers.Stage(part);
        switch (outcome)
        {
            case LayerOutcome.Layered:
                Notifier.Info(part.StageCount() > 1
                    ? $"{name} came all over your {partName} (stage {stage} of {part.StageCount()})."
                    : $"{name} came all over your {partName}.");
                break;
            case LayerOutcome.Advanced:
                Notifier.Info($"{name} added another load to your {partName} - now at stage {stage} of {part.StageCount()}.");
                break;
            case LayerOutcome.Full:
                Notifier.Info($"{name} tried to cum on your {partName}, but it's already completely plastered.");
                break;
        }
        // Every visible change, so the wearer's moodle (and its VFX) lands with each new stage too.
        if (outcome is LayerOutcome.Layered or LayerOutcome.Advanced)
        {
            LayerAccepted?.Invoke(part);
            LayerReceived?.Invoke(part, name, world);
        }
    }

    /// A Full Body layer: one layer on every unblocked regional part, with one notification and one moodle.
    private void ApplyFullBody(CharacterData me, string name, string world)
    {
        if ((BodyParts.LayerTargetsMask & ~me.BlockMask) == 0)
        {
            Plugin.Log.Information($"Ignored a Full Body layer from {name}@{world}: every part is blocked.");
            return;
        }
        var (changed, stage) = layers.LayerAll(me.BlockMask, new LayerChange(ChangeCause.FullBody, name, world));
        if (changed.Count == 0)
        {
            Notifier.Info($"{name} tried to paint your whole body, but you're already completely plastered.");
            return;
        }
        Notifier.Info(stage is > 1
            ? $"{name} painted your whole body again - now at stage {stage} of {BodyParts.MaxStages}."
            : $"{name} painted your whole body with cum.");
        LayerAccepted?.Invoke(BodyPart.FullBody);
        LayerReceived?.Invoke(BodyPart.FullBody, name, world);
    }

    private void AddPeer(string name, string world, int blockMask)
    {
        if (Me is not { } me || me.FindPeer(name, world) is not null) return;
        me.Peers.Add(new PeerRecord { Name = name, World = world, PairedAtUtc = DateTime.UtcNow, BlockMask = blockMask });
        config.Save();
        AccessChanged?.Invoke();
    }

    public IEnumerable<PeerRecord> PeersSorted() => Peers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
}
