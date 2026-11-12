using System;
using Cummy.Ipc;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace Cummy.Pairing;

/// How close a partner must be to layer or lick them. Distance is centre to centre in 3D between the two characters,
/// measured the same way by the sender (to offer the action) and by the receiver (to accept it); the receiver allows
/// <see cref="ReceiveSlack"/> more for movement while the tell travels. A partner who isn't loaded is out of range.
public static class Proximity
{
    public const float LayerRange = 10f;
    public const float LickRange = 3f;
    public const float ReceiveSlack = 2f;

    /// Yalms between you and the player `name@world`; null when either of you isn't loaded.
    public static float? DistanceTo(string name, string world)
    {
        if (Plugin.ObjectTable.LocalPlayer is not { } me) return null;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is IPlayerCharacter pc
                && string.Equals(pc.Name.TextValue, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pc.HomeWorld.Value.Name.ExtractText(), world, StringComparison.OrdinalIgnoreCase))
                return Vector3.Distance(me.Position, pc.Position);
        }
        return null;
    }

    public static bool Within(string name, string world, float range) => DistanceTo(name, world) is { } d && d <= range;

    public static bool Within(PeerRecord peer, float range) => Within(peer.Name, peer.World, range);

    /// Reads other players' Cummy presence tags; set once by Plugin. Without it (or without SimpleHeels) only partners
    /// are ever listed nearby.
    public static SimpleHeelsIpc? Heels { get; set; }

    /// <paramref name="pc"/>'s Cummy presence tag, or null when SimpleHeels is missing or they have none.
    public static PresenceTag? TagOf(IPlayerCharacter pc)
        => Heels is { IsAvailable: true } heels ? PresenceTag.Parse(heels.Read(pc.ObjectIndex, PresenceTag.Key)) : null;

    /// The presence tag of `name@world` if they are loaded nearby; null otherwise.
    public static PresenceTag? TagOf(string name, string world)
    {
        foreach (var obj in Plugin.ObjectTable)
            if (obj is IPlayerCharacter pc
                && string.Equals(pc.Name.TextValue, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pc.HomeWorld.Value.Name.ExtractText(), world, StringComparison.OrdinalIgnoreCase))
                return TagOf(pc);
        return null;
    }

    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(500);
    private static DateTime scannedAt = DateTime.MinValue;
    private static List<NearbyPlayer> nearby = [];

    /// Unpaired players' stand-in records, kept between scans so one stranger stays the same object while nearby.
    /// Never stored in the configuration; they carry no known blocks.
    private static readonly Dictionary<string, PeerRecord> strangers = new(StringComparer.OrdinalIgnoreCase);

    /// The players within layering range other than you, without blacklisted ones: paired partners first (their own
    /// records, so blocks stay current), then other Cummy users - strangers whose SimpleHeels presence tag parses, with
    /// their blocks from it - each by name. Strangers without a tag (or anyone, without SimpleHeels) aren't listed.
    /// Rescanned at most every 500 ms.
    public static IReadOnlyList<NearbyPlayer> NearbyPlayers(PairingService pairing)
    {
        if (DateTime.UtcNow - scannedAt < ScanInterval) return nearby;
        scannedAt = DateTime.UtcNow;
        var found = new List<NearbyPlayer>();
        if (Plugin.ObjectTable.LocalPlayer is { } me)
        {
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj is not IPlayerCharacter pc || pc.GameObjectId == me.GameObjectId) continue;
                if (Vector3.Distance(me.Position, pc.Position) > Proximity.LayerRange) continue;
                var name = pc.Name.TextValue;
                var world = pc.HomeWorld.Value.Name.ExtractText();
                if (name.Length == 0 || world.Length == 0 || pairing.IsBlacklisted(name, world)) continue;
                if (found.Exists(n => n.Target.Matches(name, world))) continue;
                var tag = TagOf(pc);
                if (pairing.FindPeer(name, world) is { } peer)
                {
                    found.Add(new NearbyPlayer(peer, true, tag));
                    continue;
                }
                if (tag is not { } known) continue;   // no Cummy tag: not a Cummy user we can see
                var key = $"{name}@{world}";
                if (!strangers.TryGetValue(key, out var stranger))
                    strangers[key] = stranger = new PeerRecord { Name = name, World = world };
                stranger.BlockMask = known.BlockMask;
                found.Add(new NearbyPlayer(stranger, false, tag));
            }
        }
        // Forget strangers who left, so the cache doesn't grow.
        foreach (var key in strangers.Keys.Where(k => !found.Exists(n => !n.IsPartner && n.Target.Key.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList())
            strangers.Remove(key);
        nearby = found.OrderByDescending(n => n.IsPartner)
                      .ThenBy(n => n.Target.Name, StringComparer.OrdinalIgnoreCase)
                      .ToList();
        return nearby;
    }

    /// Forces the next <see cref="NearbyPlayers"/> call to rescan (after pairing or blacklisting someone).
    public static void InvalidateNearby() => scannedAt = DateTime.MinValue;
}

/// Someone within layering range: a paired partner's own record, or an unpaired player's stand-in.
public sealed record NearbyPlayer(PeerRecord Target, bool IsPartner, PresenceTag? Tag = null)
{
    /// Whether layering, licking and creampieing them can work: a partner, or a stranger in Public mode while you are.
    public bool CanAct(bool youArePublic) => IsPartner || (youArePublic && Tag is { Public: true });
}
