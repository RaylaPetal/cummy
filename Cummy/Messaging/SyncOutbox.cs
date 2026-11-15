using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Cummy.Ipc;
using Cummy.State;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;

namespace Cummy.Messaging;

/// One event in an outbox: a protocol message (`lay ch`, `lck mo`, `crp pu`, `lkd ch`) addressed to the character whose
/// <see cref="SyncOutbox.Hash"/> is <paramref name="Target"/>, numbered within the sender's session.
public sealed record OutboxEvent(string Target, int Seq, string Text);

/// A parsed `CummyMx` tag: the sender's session token and its most recent events, oldest first.
public sealed record OutboxTag(string Token, IReadOnlyList<OutboxEvent> Events);

/// The sync transport: layers, licks, creampies and lick confirmations carried in a SimpleHeels tag (`CummyMx`) that
/// sync services (Lightless, Mare) deliver to synced players, instead of a tell. Like
/// `M1|k3f9q2ab|a1b2c3d4:42:lay ch;a1b2c3d4:43:lay st`: version, session token, then up to <see cref="MaxEvents"/>
/// events of target hash, sequence number and the `cmy1` verb and argument. Publishing the tag, even empty, tells
/// other clients this one reads outboxes. Incoming events are raised through <see cref="Received"/> with the
/// character carrying the tag as the sender, for the same handling as tells. Framework thread.
public sealed class SyncOutbox : IDisposable
{
    public const string Key = "CummyMx";
    public const string Prefix = "M1";
    public const int MaxEvents = 8;
    public const int MaxLength = 200;
    private const int HashLength = 8;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly Configuration config;
    private readonly LayerState layers;
    private readonly SimpleHeelsIpc heels;

    private string token = Protocol.NewNonce();
    private int seq;
    private readonly List<OutboxEvent> events = new();
    private string? written;

    /// Per sender (`name@world`): the token and highest sequence number seen. Memory only; dropped when they unload.
    private readonly Dictionary<string, (string Token, int Seq)> seen = new(StringComparer.OrdinalIgnoreCase);
    private DateTime nextPoll = DateTime.MinValue;

    /// (sender name, sender home world, message): an event addressed to the local player, seen for the first time.
    public event Action<string, string, ProtocolMessage>? Received;

    public SyncOutbox(Configuration config, LayerState layers, SimpleHeelsIpc heels)
    {
        this.config = config;
        this.layers = layers;
        this.heels = heels;
        layers.CharacterChanged += OnCharacterChanged;
        heels.BecameAvailable += Republish;
        Plugin.Framework.Update += OnUpdate;
        if (Plugin.ClientState.IsLoggedIn) Plugin.Framework.RunOnTick(Publish, TimeSpan.FromSeconds(3));   // plugin reload
    }

    public void Dispose()
    {
        layers.CharacterChanged -= OnCharacterChanged;
        heels.BecameAvailable -= Republish;
        Plugin.Framework.Update -= OnUpdate;
        Remove();
    }

    /// Whether this client's outbox is on the local character now.
    public bool IsPublished => written != null;

    // ── Format ───────────────────────────────────────────────────────────────

    /// The first 8 hex digits of SHA-256 of the lower-cased `name@world`: names a target without spelling it out.
    public static string Hash(string name, string world)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{name}@{world}".ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, HashLength / 2).ToLowerInvariant();
    }

    /// The verbs the outbox carries; everything else stays on tells.
    public static bool Carries(Verb verb) => verb is Verb.Layer or Verb.Lick or Verb.Creampie or Verb.Licked;

    /// The tag value, dropping the oldest events until it fits <see cref="MaxLength"/>.
    public static string Format(string token, IReadOnlyList<OutboxEvent> events)
    {
        var kept = events.Skip(Math.Max(0, events.Count - MaxEvents)).ToList();
        while (true)
        {
            var text = $"{Prefix}|{token}|{string.Join(';', kept.Select(e => $"{e.Target}:{e.Seq}:{e.Text}"))}";
            if (text.Length <= MaxLength || kept.Count == 0) return text;
            kept.RemoveAt(0);
        }
    }

    /// Null for anything that isn't a well-formed outbox of this version. Each event's verb and argument must parse as a
    /// `cmy1` message of a carried verb.
    public static OutboxTag? Parse(string? text)
    {
        if (text is null || text.Length > MaxLength) return null;
        var parts = text.Split('|');
        if (parts.Length != 3 || parts[0] != Prefix || !IsToken(parts[1])) return null;
        var list = new List<OutboxEvent>();
        if (parts[2].Length > 0)
        {
            foreach (var raw in parts[2].Split(';'))
            {
                var fields = raw.Split(':');
                if (fields.Length != 3 || !IsHash(fields[0])
                    || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0
                    || ToMessage(fields[2]) is null) return null;
                list.Add(new OutboxEvent(fields[0], n, fields[2]));
            }
        }
        if (list.Count > MaxEvents) return null;
        return new OutboxTag(parts[1], list);
    }

    /// The protocol message an event's text stands for, or null when it isn't a carried verb.
    public static ProtocolMessage? ToMessage(string text)
        => Protocol.Parse($"{Protocol.Prefix} {text}") is { } m && Carries(m.Verb) ? m : null;

    private static bool IsToken(string s)
        => s.Length == Protocol.NonceLength && s.All(c => c is >= 'a' and <= 'z' || c is >= '2' and <= '7');

    private static bool IsHash(string s)
        => s.Length == HashLength && s.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    // ── Sending ──────────────────────────────────────────────────────────────

    /// Whether a message to `name@world` can go through the outbox now: the setting is on, this outbox is published,
    /// and they are loaded and publish an outbox of this version.
    public bool CanReach(string name, string world)
        => config.SyncTransport && IsPublished && FindPlayer(name, world) is { } pc && Parse(heels.Read(pc.ObjectIndex, Key)) is not null;

    /// Puts <paramref name="protocolText"/> (a `cmy1` message of a carried verb) in the outbox for `name@world`. False
    /// when the outbox can't reach them or the message isn't one it carries; the caller sends a tell instead.
    public bool TrySend(string name, string world, string protocolText)
    {
        if (!protocolText.StartsWith(Protocol.Prefix + " ", StringComparison.Ordinal)) return false;
        var text = protocolText[(Protocol.Prefix.Length + 1)..];
        if (ToMessage(text) is null || !CanReach(name, world)) return false;
        var added = new OutboxEvent(Hash(name, world), seq + 1, text);
        events.Add(added);
        if (!Write())
        {
            // Not published: the caller sends a tell, so the event must not go out later too.
            events.Remove(added);
            return false;
        }
        seq++;
        if (events.Count > MaxEvents) events.RemoveAt(0);
        Plugin.Log.Debug($"Outbox to {name}@{world}: #{seq} {text}");
        return true;
    }

    // ── Publishing ───────────────────────────────────────────────────────────

    /// The sync transport setting changed: publish or remove the outbox.
    public void SettingChanged()
    {
        if (config.SyncTransport) Publish();
        else Remove();
    }

    private void OnCharacterChanged(bool loggedIn)
    {
        if (loggedIn)
        {
            // A fresh session: receivers take a new baseline rather than comparing with the old numbers.
            token = Protocol.NewNonce();
            seq = 0;
            events.Clear();
            Remove();
            Publish();
        }
        else
        {
            Remove();
            seen.Clear();
        }
    }

    private void Republish()
    {
        written = null;
        Publish();
    }

    private void Publish()
    {
        if (!config.SyncTransport || !Plugin.ClientState.IsLoggedIn || layers.Current is null) return;
        Write();
    }

    private bool Write()
    {
        var value = Format(token, events);
        if (value == written) return true;
        if (!heels.Write(Key, value)) return false;
        written = value;
        return true;
    }

    private void Remove()
    {
        if (written == null) return;
        heels.Write(Key, null);
        written = null;
    }

    // ── Receiving ────────────────────────────────────────────────────────────

    /// Reads every loaded player's outbox (not only those in range, so the baseline is taken before they can act) and
    /// raises the new events addressed to the local player.
    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow + PollInterval;
        if (!config.SyncTransport || !heels.IsAvailable || Plugin.ObjectTable.LocalPlayer is not { } me || layers.Current is null)
        {
            seen.Clear();
            return;
        }
        var myHash = Hash(me.Name.TextValue, me.HomeWorld.Value.Name.ExtractText());
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter pc || pc.GameObjectId == me.GameObjectId) continue;
            var name = pc.Name.TextValue;
            var world = pc.HomeWorld.Value.Name.ExtractText();
            if (name.Length == 0 || world.Length == 0) continue;
            if (Parse(heels.Read(pc.ObjectIndex, Key)) is not { } tag) continue;
            var key = $"{name}@{world}";
            present.Add(key);
            Take(key, name, world, tag, myHash);
        }
        foreach (var gone in seen.Keys.Where(k => !present.Contains(k)).ToList())
            seen.Remove(gone);
    }

    /// Applies the replay rule to one sender's outbox: on first sight or a new token only the baseline is recorded;
    /// afterwards each event above the last seen number that is addressed to <paramref name="myHash"/> is raised once.
    /// Returns how many were raised.
    internal int Take(string key, string name, string world, OutboxTag tag, string myHash)
    {
        var top = tag.Events.Count == 0 ? 0 : tag.Events.Max(e => e.Seq);
        if (!seen.TryGetValue(key, out var last) || last.Token != tag.Token)
        {
            seen[key] = (tag.Token, top);
            return 0;
        }
        if (top <= last.Seq) return 0;
        seen[key] = (tag.Token, top);
        var raised = 0;
        foreach (var e in tag.Events.Where(e => e.Seq > last.Seq && e.Target == myHash).OrderBy(e => e.Seq))
        {
            if (ToMessage(e.Text) is not { } message) continue;
            raised++;
            try
            {
                Received?.Invoke(name, world, message);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, $"Failed to handle outbox event \"{e.Text}\" from {key}.");
            }
        }
        return raised;
    }

    private static IPlayerCharacter? FindPlayer(string name, string world)
    {
        foreach (var obj in Plugin.ObjectTable)
            if (obj is IPlayerCharacter pc
                && string.Equals(pc.Name.TextValue, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pc.HomeWorld.Value.Name.ExtractText(), world, StringComparison.OrdinalIgnoreCase))
                return pc;
        return null;
    }

    /// Whether `name@world` is loaded and publishes an outbox of this version: a current Cummy that reads blocks from
    /// presence tags.
    public bool Advertises(string name, string world)
        => FindPlayer(name, world) is { } pc && Parse(heels.Read(pc.ObjectIndex, Key)) is not null;

#if DEBUG
    /// Debug builds only: the format round trip and rejection cases, then the replay rule run on a fake sender.
    /// Returns one line per check.
    public List<string> SelfTest()
    {
        var results = new List<string>();
        void Check(string what, bool ok) => results.Add($"{(ok ? "ok  " : "FAIL")} {what}");

        var h = Hash("Some One", "World");
        Check("hash ignores case", h == Hash("some one", "WORLD"));
        Check("hash is 8 hex", IsHash(h));
        string[] texts = ["lay ch", "lay fb", "lck mo", "crp pu", "crp as", "lkd st"];
        var evts = texts.Select((t, i) => new OutboxEvent(h, i + 1, t)).ToList();
        var tok = Protocol.NewNonce();
        var formatted = Format(tok, evts);
        var back = Parse(formatted);
        Check($"round trip ({formatted.Length} chars)", back is { } b && b.Token == tok && b.Events.SequenceEqual(evts));
        Check("empty outbox", Parse($"{Prefix}|{tok}|") is { Events.Count: 0 });
        var many = Enumerable.Range(1, 20).Select(i => new OutboxEvent(h, 99990 + i, "lay ch")).ToList();
        var capped = Format(tok, many);
        Check($"capped to {MaxEvents} events within {MaxLength} chars", capped.Length <= MaxLength && Parse(capped) is { Events.Count: <= MaxEvents });
        Check("rejects wrong prefix", Parse($"M2|{tok}|") is null);
        Check("rejects unknown verb", Parse($"{Prefix}|{tok}|{h}:1:zzz ch") is null);
        Check("rejects ext", Parse($"{Prefix}|{tok}|{h}:1:ext abc def") is null);
        Check("rejects inv", Parse($"{Prefix}|{tok}|{h}:1:inv abcdefgh 0") is null);
        Check("rejects bad seq", Parse($"{Prefix}|{tok}|{h}:x:lay ch") is null && Parse($"{Prefix}|{tok}|{h}:0:lay ch") is null);
        Check("rejects bad hash", Parse($"{Prefix}|{tok}|XYZ:1:lay ch") is null);
        Check("rejects too long", Parse($"{Prefix}|{tok}|" + new string('a', MaxLength)) is null);
        Check("rejects null", Parse(null) is null);

        // Replay rule, on a key no real player can have.
        const string key = "Debug Sender@Debug";
        var other = Hash("Not Me", "Debug");
        var t1 = Protocol.NewNonce();
        int Raise(OutboxTag tag) { var saved = Received; Received = null; try { return Take(key, "Debug Sender", "Debug", tag, h); } finally { Received = saved; } }
        seen.Remove(key);
        Check("first sight applies nothing", Raise(new OutboxTag(t1, [new(h, 5, "lay ch")])) == 0);
        Check("new event applies once", Raise(new OutboxTag(t1, [new(h, 5, "lay ch"), new(h, 6, "lay st")])) == 1);
        Check("same tag again applies nothing", Raise(new OutboxTag(t1, [new(h, 5, "lay ch"), new(h, 6, "lay st")])) == 0);
        Check("someone else's event ignored", Raise(new OutboxTag(t1, [new(h, 6, "lay st"), new(other, 7, "lay ch")])) == 0);
        Check("burst applies both", Raise(new OutboxTag(t1, [new(other, 7, "lay ch"), new(h, 8, "lay ch"), new(h, 9, "lay bk")])) == 2);
        Check("new token takes baseline", Raise(new OutboxTag(Protocol.NewNonce(), [new(h, 1, "lay ch")])) == 0);
        seen.Remove(key);
        return results;
    }

    /// Debug builds only: as if loaded player `name@world` had an empty outbox and then added one event addressed to
    /// the local player (<paramref name="eventText"/>, like `lay ch`), through the real format, parse, replay rule and
    /// handling (blacklist, pairing, blocks; the range check needs them loaded and close).
    public string SimulateFrom(string name, string world, string eventText)
    {
        if (Plugin.ObjectTable.LocalPlayer is not { } me) return "not logged in";
        var myHash = Hash(me.Name.TextValue, me.HomeWorld.Value.Name.ExtractText());
        var tok = Protocol.NewNonce();
        if (Parse(Format(tok, [])) is not { } empty) return "empty outbox did not parse";
        if (Parse(Format(tok, [new OutboxEvent(myHash, 1, eventText)])) is not { } one) return "invalid (format)";
        var key = $"{name}@{world}";
        seen.Remove(key);
        var baseline = Take(key, name, world, empty, myHash);
        var raised = Take(key, name, world, one, myHash);
        seen.Remove(key);
        return $"baseline raised {baseline}, event raised {raised}";
    }

    public string Token => token;
#endif
}
