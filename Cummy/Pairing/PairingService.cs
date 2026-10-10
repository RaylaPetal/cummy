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

/// Tell-handshake pairing (invite → accept, unpair either way) and the layer commands that only paired peers may
/// send. Every outgoing tell here is the direct result of a user click.
public sealed class PairingService : IDisposable
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(5);

    private readonly Configuration config;
    private readonly LayerState layers;
    private readonly ChatSender sender;
    private readonly ChatListener listener;

    /// Invites we sent, by peer key, awaiting their accept.
    private readonly Dictionary<string, (string Nonce, DateTime ExpiresUtc)> outgoing = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<IncomingInvite> incoming = new();

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
        sender.SendTell(invite.Name, invite.World, Protocol.BlockExtended(me.BlockMask));
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

    /// The wearer changed their block list: tell every peer. `blk` (body bits, for every version) only when a body
    /// part changed; `blx` (full list, updated versions) always.
    public void BroadcastBlocks(bool bodyChanged)
    {
        if (Me is not { } me) return;
        foreach (var peer in me.Peers)
        {
            if (bodyChanged) sender.SendTell(peer.Name, peer.World, Protocol.Block(me.BlockMask));
            sender.SendTell(peer.Name, peer.World, Protocol.BlockExtended(me.BlockMask));
        }
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
                // Now that they know us, send the full block list (facial parts included); older versions ignore it.
                sender.SendTell(name, world, Protocol.BlockExtended(me.BlockMask));
                Notifier.Info($"{key} accepted - you are now paired.");
                break;

            case Verb.Unpair:
                if (me.FindPeer(name, world) is not { } gone) return;
                me.Peers.Remove(gone);
                config.Save();
                Notifier.Info($"{gone.Key} unpaired from you.");
                break;

            case Verb.Block:
                // Body bits only (what every version sends); keep the facial bits an earlier `blx` told us.
                if (me.FindPeer(name, world) is not { } blocker) return;
                blocker.BlockMask = (blocker.BlockMask & ~BodyParts.BodyMask) | message.BlockMask;
                config.Save();
                break;

            case Verb.BlockExtended:
                if (me.FindPeer(name, world) is not { } extended) return;
                extended.BlockMask = message.BlockMask;
                config.Save();
                break;

            case Verb.Layer:
                if (me.FindPeer(name, world) is null)
                {
                    Plugin.Log.Information($"Ignored a layer from unpaired {name}@{world}.");
                    return;
                }
                ApplyLayer(me, name, world, message.Part);
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
#endif

    private void ApplyLayer(CharacterData me, string name, string world, BodyPart part)
    {
        if (part.IsBlockedBy(me.BlockMask))
        {
            Plugin.Log.Information($"Ignored a blocked {part} layer from {name}@{world}.");
            return;
        }
        var partName = part.DisplayName().ToLowerInvariant();
        var added = layers.Add(part);
        Notifier.Info(added
            ? $"{name} layered your {partName}."
            : $"{name} tried to layer your {partName}, but it's already covered.");
        if (added) LayerAccepted?.Invoke(part);
    }

    private void AddPeer(string name, string world, int blockMask)
    {
        if (Me is not { } me || me.FindPeer(name, world) is not null) return;
        me.Peers.Add(new PeerRecord { Name = name, World = world, PairedAtUtc = DateTime.UtcNow, BlockMask = blockMask });
        config.Save();
    }

    public IEnumerable<PeerRecord> PeersSorted() => Peers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
}
