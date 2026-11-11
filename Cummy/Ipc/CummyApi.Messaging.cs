using Cummy.Messaging;
using Dalamud.Plugin.Ipc;

namespace Cummy.Ipc;

/// Plugin messaging (version 5): short messages between the same plugin on paired partners' clients, carried over
/// Cummy's hidden, rate-limited tells (<see cref="PluginRelay"/>).
public sealed partial class CummyApi
{
    private ICallGateProvider<string, string, string, string, object> onMessage = null!;

    private void RegisterMessaging()
    {
        Outcome<string, string, string, string>("Cummy.SendMessage", SendMessage);
        onMessage = Plugin.PluginInterface.GetIpcProvider<string, string, string, string, object>("Cummy.OnMessage");
        relay.MessageReceived += OnRelayMessage;
    }

    private void UnhookMessaging() => relay.MessageReceived -= OnRelayMessage;

    /// One of sent, not-paired, blacklisted, invalid, disabled, queue-full or no-character.
    public string SendMessage(string name, string world, string channel, string payload)
        => relay.Send(name?.Trim() ?? "", world?.Trim() ?? "", channel ?? "", payload ?? "") switch
        {
            RelayOutcome.Sent => ApiStrings.Sent,
            RelayOutcome.NotPaired => ApiStrings.NotPaired,
            RelayOutcome.Blacklisted => ApiStrings.Blacklisted,
            RelayOutcome.Invalid => ApiStrings.Invalid,
            RelayOutcome.Disabled => ApiStrings.Disabled,
            RelayOutcome.QueueFull => ApiStrings.QueueFull,
            _ => ApiStrings.NoCharacter,
        };

    private void OnRelayMessage(string channel, string payload, string name, string world)
        => Send("Cummy.OnMessage", () => onMessage.SendMessage(channel, payload, name, world));
}
