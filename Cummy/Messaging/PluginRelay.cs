using System;
using Cummy.State;

namespace Cummy.Messaging;

/// What sending a plugin message did; only <see cref="Sent"/> queued a tell.
public enum RelayOutcome { Sent, NotPaired, Blacklisted, Invalid, Disabled, QueueFull, NoCharacter }

/// Carries other plugins' messages (`cmy1 ext <channel> <payload>`) between paired partners. Only partners, never anyone
/// blacklisted (Public mode doesn't widen it: that is consent to be layered, not to plugin traffic), and only while
/// the wearer leaves <see cref="Configuration.RelayPluginMessages"/> on. The sender is always the game-reported one.
/// Framework thread.
public sealed class PluginRelay : IDisposable
{
    private readonly Configuration config;
    private readonly LayerState layers;
    private readonly ChatSender sender;
    private readonly ChatListener listener;

    /// (channel, payload, sender name, sender world).
    public event Action<string, string, string, string>? MessageReceived;

    public PluginRelay(Configuration config, LayerState layers, ChatSender sender, ChatListener listener)
    {
        this.config = config;
        this.layers = layers;
        this.sender = sender;
        this.listener = listener;
        listener.Received += OnReceived;
    }

    public void Dispose() => listener.Received -= OnReceived;

    /// Queues one message to paired partner `name@world`.
    public RelayOutcome Send(string name, string world, string channel, string payload)
    {
        if (layers.Current is not { } me) return RelayOutcome.NoCharacter;
        if (!config.RelayPluginMessages) return RelayOutcome.Disabled;
        if (Protocol.Message(channel, payload) is not { } text) return RelayOutcome.Invalid;
        if (me.IsBlacklisted(name, world)) return RelayOutcome.Blacklisted;
        if (me.FindPeer(name, world) is not { } peer) return RelayOutcome.NotPaired;
        return sender.SendTell(peer.Name, peer.World, text, relay: true) ? RelayOutcome.Sent : RelayOutcome.QueueFull;
    }

    private void OnReceived(string name, string world, ProtocolMessage message)
    {
        if (message.Verb != Verb.Extension) return;
        Handle(name, world, message);
    }

    private void Handle(string name, string world, ProtocolMessage message)
    {
        if (layers.Current is not { } me) return;
        if (!config.RelayPluginMessages)
        {
            Plugin.Log.Debug($"Dropped a plugin message from {name}@{world}: the relay is off.");
            return;
        }
        if (me.IsBlacklisted(name, world) || me.FindPeer(name, world) is null)
        {
            Plugin.Log.Information($"Ignored a plugin message from unpaired or blacklisted {name}@{world}.");
            return;
        }
        MessageReceived?.Invoke(message.Channel!, message.Payload!, name, world);
    }

#if DEBUG
    /// Debug builds only: behave as if `name@world` had sent this message, through the real wire format and checks.
    public string SimulateFrom(string name, string world, string channel, string payload)
    {
        if (Protocol.Message(channel, payload) is not { } text) return "invalid (format)";
        if (Protocol.Parse(text) is not { Verb: Verb.Extension } message) return "did not survive the wire format";
        Handle(name, world, message);
        return $"handled: {text}";
    }
#endif
}
