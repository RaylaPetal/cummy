using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Cummy.Messaging;

/// The one place that can transmit chat (the Oathbound discipline). Sends come from a user click (one press, one tell
/// per recipient), a reply to a partner's lick, or another plugin's call over IPC (a layer, lick or invite, or a relayed
/// message, which only ever holds <see cref="MaxRelayQueued"/> places). It only ever emits
/// `/tell Name Surname@World cmy1 ...`; anything else is refused, so a bug can never put text into the active chat
/// channel.
public sealed class ChatSender : IDisposable
{
    private static readonly Regex ValidTell = new(
        @"^/tell [A-Za-z'\-]+ [A-Za-z'\-]+@[A-Za-z]+ cmy1( [a-z0-9]+)+$", RegexOptions.Compiled);

    /// The game drops messages sent too fast.
    private static readonly TimeSpan MessageSpacing = TimeSpan.FromSeconds(1.1);

    /// Clicks beyond this many waiting tells are refused instead of piling up.
    private const int MaxQueued = 5;

    /// Other plugins' relayed messages never hold more of the queue than this, so the wearer's own layers and licks
    /// still go out while a plugin is busy.
    public const int MaxRelayQueued = 2;

    private readonly Queue<(string Text, bool Relay)> queue = new();
    private DateTime nextSendAt = DateTime.MinValue;

    public ChatSender() => Plugin.Framework.Update += OnUpdate;

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    public int Queued => queue.Count;

    /// Queues one protocol tell to `name@world`. False when refused (malformed, or too many queued); the caller
    /// tells the user.
    public bool SendTell(string name, string world, string protocolText, bool relay = false)
    {
        var text = $"/tell {name}@{world} {protocolText}";
        if (!ValidTell.IsMatch(text))
        {
            Plugin.Log.Warning($"Refused to send a message that is not a {Protocol.Prefix} tell: \"{text}\"");
            return false;
        }
        if (queue.Count >= MaxQueued)
        {
            Plugin.Log.Information("Refused to queue a tell: too many already waiting.");
            return false;
        }
        if (relay && queue.Count(q => q.Relay) >= MaxRelayQueued)
        {
            Plugin.Log.Information("Refused to queue a plugin message: the relay's share of the queue is full.");
            return false;
        }
        queue.Enqueue((text, relay));
        return true;
    }

    private void OnUpdate(IFramework framework)
    {
        if (queue.Count == 0 || DateTime.UtcNow < nextSendAt) return;
        if (!Plugin.ClientState.IsLoggedIn)
        {
            queue.Clear();
            return;
        }
        nextSendAt = DateTime.UtcNow + MessageSpacing;
        Transmit(queue.Dequeue().Text);
    }

    private static unsafe void Transmit(string text)
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null) return;
        var str = Utf8String.FromString(text);
        try
        {
            uiModule->ProcessChatBoxEntry(str);
        }
        finally
        {
            str->Dtor(true);
        }
    }
}
