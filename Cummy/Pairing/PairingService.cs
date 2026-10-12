using System;
using System.Collections.Generic;
using System.Linq;
using Cummy.Messaging;
using Cummy.State;

namespace Cummy.Pairing;

public sealed record IncomingInvite(string Name, string World, string Nonce, int BlockMask, DateTime ExpiresUtc)
{
    public string Key => $"{Name}@{World}";
}

/// Tell-handshake pairing (invite → accept, unpair either way) and the layer and lick commands that only paired peers
/// may send, from close by (<see cref="Proximity"/>). Every outgoing tell here is the direct result of a user click,
/// except the `lkd` reply to a partner's lick that cleaned something.
public sealed class PairingService : IDisposable
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(5);

    /// How long a lick waits for the partner's `lkd` before it counts as "nothing to lick there".
    public static readonly TimeSpan LickTimeout = TimeSpan.FromSeconds(30);

    private readonly Configuration config;
    private readonly LayerState layers;
    private readonly ChatSender sender;
    private readonly ChatListener listener;

    /// Invites we sent, by peer key, awaiting their accept.
    private readonly Dictionary<string, (string Nonce, DateTime ExpiresUtc)> outgoing = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<IncomingInvite> incoming = new();

    /// Licks we sent, by peer key and part, awaiting their `lkd`. Memory only.
    private readonly Dictionary<(string PeerKey, BodyPart Part), DateTime> pendingLicks = new();

    /// Raised when an invite arrives, so the prompt window can open.
    public event Action? InviteReceived;

    /// Raised when a partner's layer landed on a clean part (not for blocked or already-covered parts, nor on login).
    public event Action<BodyPart>? LayerAccepted;

    public PairingService(Configuration config, LayerState layers, ChatSender sender, ChatListener listener)
    {
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

    public void Invite(string name, string world)
    {
        if (Me is not { } me || FindPeer(name, world) is not null) return;
        var nonce = Protocol.NewNonce();
        if (!sender.SendTell(name, world, Protocol.Invite(nonce, me.BlockMask)))
        {
            Notifier.Error($"Couldn't send the pairing invite to {name}.");
            return;
        }
        outgoing[$"{name}@{world}"] = (nonce, DateTime.UtcNow + InviteLifetime);
        Notifier.Info($"Pairing invite sent to {name}@{world}.");
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

    public void Unpair(PeerRecord peer)
    {
        if (Me is not { } me) return;
        me.Peers.Remove(peer);
        config.Save();
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
        Notifier.Info($"Layered {peer.Name}'s {part.DisplayName().ToLowerInvariant()}.");
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
        }, LickTimeout + TimeSpan.FromSeconds(1));
        return true;
    }

    /// The wearer changed whether <paramref name="changed"/> is blocked: tell every peer, with each block message
    /// whose bits cover that part (`blk` for every version, `blx` for versions with facial parts, `bly` for current).
    public void BroadcastBlocks(BodyPart changed)
    {
        if (Me is not { } me) return;
        foreach (var peer in me.Peers)
        {
            if ((changed.Bit() & BodyParts.LegacyBodyMask) != 0) sender.SendTell(peer.Name, peer.World, Protocol.Block(me.BlockMask));
            if ((changed.Bit() & BodyParts.ExtendedMask) != 0) sender.SendTell(peer.Name, peer.World, Protocol.BlockExtended(me.BlockMask));
            sender.SendTell(peer.Name, peer.World, Protocol.BlockAll(me.BlockMask));
        }
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
                if (me.FindPeer(name, world) is not null) return;
                incoming.RemoveAll(i => i.Key.Equals($"{name}@{world}", StringComparison.OrdinalIgnoreCase));
                incoming.Add(new IncomingInvite(name, world, message.Nonce!, message.BlockMask, DateTime.UtcNow + InviteLifetime));
                Notifier.Info($"{name}@{world} wants to pair with you.");
                InviteReceived?.Invoke();
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
                break;

            case Verb.Block or Verb.BlockExtended or Verb.BlockAll:
                // Each message replaces only the bits it covers, keeping what another block message told us, so a
                // `blk` arriving after a `bly` doesn't forget blocks it can't express.
                if (me.FindPeer(name, world) is not { } blocker) return;
                var covers = Protocol.Covers(message.Verb);
                blocker.BlockMask = (blocker.BlockMask & ~covers) | (message.BlockMask & covers);
                config.Save();
                break;

            case Verb.Layer:
                if (me.FindPeer(name, world) is null)
                {
                    Plugin.Log.Information($"Ignored a layer from unpaired {name}@{world}.");
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
                if (me.FindPeer(name, world) is null)
                {
                    Plugin.Log.Information($"Ignored a lick from unpaired {name}@{world}.");
                    return;
                }
                if (!Proximity.Within(name, world, Proximity.LickRange + Proximity.ReceiveSlack))
                {
                    Plugin.Log.Information($"Ignored a lick from {name}@{world}: not within {Proximity.LickRange} yalms.");
                    return;
                }
                if (BeLicked(name, message.Part) && !sender.SendTell(name, world, Protocol.Licked(message.Part)))
                    Plugin.Log.Warning($"Couldn't confirm {name}@{world}'s lick of {message.Part}: send queue full.");
                break;

            case Verb.Licked:
                if (me.FindPeer(name, world) is not { } licked) return;
                OnLickConfirmed(me, licked.Key, licked.Name, message.Part);
                break;
        }
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
        Notifier.Info(BeLicked("Debug Partner", message.Part)
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
        OnLickConfirmed(me, key, "Debug Partner", message.Part);
    }
#endif

    /// A partner's verified lick: cleans the whole part (every stage) if it is layered. True when it did, so the
    /// caller confirms with `lkd`. Blocks don't apply: they only stop layering.
    private bool BeLicked(string name, BodyPart part)
    {
        if (!layers.Clean(part)) return false;
        Notifier.Info($"{name} licked your {part.DisplayName().ToLowerInvariant()} clean.");
        return true;
    }

    /// A partner's `lkd`: only for a lick we sent them of that part, still waiting. Then it lands on our mouth: a
    /// clean mouth gets layered, a covered one spills a stage down the chest; licking their mouth (a kiss) cleans
    /// ours instead. A blocked mouth (or chest, for the spill) takes nothing.
    private void OnLickConfirmed(CharacterData me, string peerKey, string name, BodyPart part)
    {
        if (!pendingLicks.Remove((peerKey, part), out var expires) || expires < DateTime.UtcNow)
        {
            Plugin.Log.Information($"Ignored a lick confirmation from {peerKey} for {part} that matches no pending lick.");
            return;
        }

        var partName = part.DisplayName().ToLowerInvariant();
        if (part == BodyPart.Mouth)
        {
            layers.Clean(BodyPart.Mouth);
            Notifier.Info($"You kissed {name} - both your mouths are clean.");
            return;
        }
        if (BodyPart.Mouth.IsBlockedBy(me.BlockMask))
        {
            Notifier.Info($"You licked {name}'s {partName} clean.");
            return;
        }
        if (!layers.IsLayered(BodyPart.Mouth))
        {
            layers.Layer(BodyPart.Mouth);
            Notifier.Info($"You licked {name}'s {partName} clean - it's on your mouth now.");
            return;
        }

        layers.RestartDrying(BodyPart.Mouth);
        var spilled = !BodyPart.Chest.IsBlockedBy(me.BlockMask)
                      && layers.Layer(BodyPart.Chest) is LayerOutcome.Layered or LayerOutcome.Advanced;
        Notifier.Info(spilled
            ? $"You licked {name}'s {partName} clean - your mouth is full, so it spills down your chest."
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
        var outcome = layers.Layer(part);
        var stage = layers.Stage(part);
        switch (outcome)
        {
            case LayerOutcome.Layered:
                Notifier.Info(part.StageCount() > 1
                    ? $"{name} layered your {partName} (stage {stage} of {part.StageCount()})."
                    : $"{name} layered your {partName}.");
                break;
            case LayerOutcome.Advanced:
                Notifier.Info($"{name} layered your {partName}, now at stage {stage} of {part.StageCount()}.");
                break;
            case LayerOutcome.Full:
                Notifier.Info($"{name} tried to layer your {partName}, but it's already fully covered.");
                break;
        }
        // Every visible change, so the wearer's moodle (and its VFX) lands with each new stage too.
        if (outcome is LayerOutcome.Layered or LayerOutcome.Advanced) LayerAccepted?.Invoke(part);
    }

    /// A Full Body layer: one layer on every unblocked regional part, with one notification and one moodle.
    private void ApplyFullBody(CharacterData me, string name, string world)
    {
        if ((BodyParts.RegionalMask & ~me.BlockMask) == 0)
        {
            Plugin.Log.Information($"Ignored a Full Body layer from {name}@{world}: every body part is blocked.");
            return;
        }
        var (changed, stage) = layers.LayerAll(me.BlockMask);
        if (changed.Count == 0)
        {
            Notifier.Info($"{name} tried to layer your whole body, but it's already fully covered.");
            return;
        }
        Notifier.Info(stage is > 1
            ? $"{name} layered your whole body, now at stage {stage} of {BodyParts.MaxStages}."
            : $"{name} layered your whole body.");
        LayerAccepted?.Invoke(BodyPart.FullBody);
    }

    private void AddPeer(string name, string world, int blockMask)
    {
        if (Me is not { } me || me.FindPeer(name, world) is not null) return;
        me.Peers.Add(new PeerRecord { Name = name, World = world, PairedAtUtc = DateTime.UtcNow, BlockMask = blockMask });
        config.Save();
    }

    public IEnumerable<PeerRecord> PeersSorted() => Peers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
}
