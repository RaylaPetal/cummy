using System;
using System.Linq;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace Cummy.Messaging;

/// Hides every `cmy` tell (incoming and our own outgoing echo) and hands well-formed incoming ones to
/// <see cref="Received"/> with the game-reported sender. Message text is never trusted for identity.
public sealed class ChatListener : IDisposable
{
    /// (sender name, sender home world, message).
    public event Action<string, string, ProtocolMessage>? Received;

    public ChatListener() => Plugin.ChatGui.ChatMessage += OnChatMessage;

    public void Dispose() => Plugin.ChatGui.ChatMessage -= OnChatMessage;

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (message.LogKind is not (XivChatType.TellIncoming or XivChatType.TellOutgoing)) return;

        var text = message.Message.TextValue.Trim();
        if (!Protocol.IsProtocolText(text)) return;

        message.PreventOriginal();
        if (message.LogKind == XivChatType.TellOutgoing) return;

        var parsed = Protocol.Parse(text);
        if (parsed is null)
        {
            Plugin.Log.Information($"Ignored an unsupported {Protocol.Family} tell: \"{text}\"");
            return;
        }

        var (name, world) = ExtractNameAndWorld(message.Sender);
        if (name is null || world is null)
        {
            Plugin.Log.Information("Ignored a protocol tell whose sender could not be identified.");
            return;
        }

        try
        {
            Received?.Invoke(name, world, parsed);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Failed to handle \"{text}\" from {name}@{world}.");
        }
    }

    /// The PlayerPayload the game attaches to a tell's sender; fall back to its "Name Surname@World" text.
    private static (string? Name, string? World) ExtractNameAndWorld(SeString sender)
    {
        var playerPayload = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (playerPayload is not null)
            return (playerPayload.PlayerName, playerPayload.World.Value.Name.ExtractText());

        var text = sender.TextValue.Trim();
        var at = text.IndexOf('@');
        return at > 0 ? (text[..at].Trim(), text[(at + 1)..].Trim()) : (null, null);
    }
}
