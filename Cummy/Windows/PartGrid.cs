using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Cummy.Pairing;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Cummy.Windows;

/// The body-part buttons for layering or licking one peer, and who is in layering range: shared by the picker and
/// the quick-layer button so the two never drift apart.
internal static class PartGrid
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(500);
    private static DateTime scannedAt = DateTime.MinValue;
    private static List<NearbyPlayer> nearby = [];

    /// Unpaired players' stand-in records, kept between scans so one stranger stays the same object while nearby.
    /// Never stored in the configuration; they carry no known blocks.
    private static readonly Dictionary<string, PeerRecord> strangers = new(StringComparer.OrdinalIgnoreCase);

    /// One button per part in one list (Face first), plus Full Body last when layering (it isn't a part, so it can't
    /// be licked). Blocked parts are disabled unless <paramref name="lick"/> (blocks only stop layering);
    /// everything is disabled unless <paramref name="enabled"/>. A click calls <paramref name="send"/>. A stranger's
    /// (<paramref name="isPartner"/> false) blocks are unknown, so nothing is shown blocked for them.
    public static void Draw(PeerRecord? peer, bool enabled, Action<BodyPart> send, string id, bool lick = false,
                            bool isPartner = true)
    {
        var size = new Vector2(130 * ImGuiHelpers.GlobalScale, 0);
        ImGuiHelpers.ScaledDummy(2f);
        using var table = ImRaii.Table($"##parts{id}", 2);
        if (!table) return;
        var parts = BodyParts.DisplayOrder.ToList();
        if (!lick) parts.Add(BodyPart.FullBody);
        foreach (var part in parts)
        {
            ImGui.TableNextColumn();
            var blocked = !lick && isPartner && peer != null && IsBlocked(part, peer.BlockMask);
            using (ImRaii.Disabled(!enabled || peer == null || blocked))
            {
                if (ImGui.Button(blocked ? $"{part.DisplayName()} (blocked)##{id}{part.Id()}" : $"{part.DisplayName()}##{id}{part.Id()}", size))
                    send(part);
            }
            if (peer != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                if (blocked)
                    ImGui.SetTooltip(part == BodyPart.FullBody && !part.IsBlockedBy(peer.BlockMask)
                        ? $"{peer.Name} has blocked every part."
                        : $"{peer.Name} has blocked this part.");
                else if (part == BodyPart.FullBody)
                    ImGui.SetTooltip($"Layers every part {peer.Name} hasn't blocked, face included, one stage each.");
                else if (!lick && part.NeedsRecentPartner())
                    ImGui.SetTooltip($"{part.DisplayName()} needs an up-to-date Cummy on {peer.Name}'s side too.");
            }
        }
    }

    /// Full Body is blocked when an older partner blocks it outright, or when every body part it would layer is.
    private static bool IsBlocked(BodyPart part, int blockMask)
        => part.IsBlockedBy(blockMask)
           || (part == BodyPart.FullBody && (BodyParts.LayerTargetsMask & ~blockMask) == 0);

    /// Whether <paramref name="peer"/> is within <paramref name="range"/> yalms of you.
    public static bool InRange(PeerRecord peer, float range) => Proximity.Within(peer, range);

    /// The players within layering range other than you, without blacklisted ones: paired partners first (their own
    /// records, so blocks stay current), then everyone else, each by name. Rescanned at most every 500 ms.
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
                if (pairing.FindPeer(name, world) is { } peer)
                {
                    found.Add(new NearbyPlayer(peer, true));
                    continue;
                }
                var key = $"{name}@{world}";
                if (!strangers.TryGetValue(key, out var stranger))
                    strangers[key] = stranger = new PeerRecord { Name = name, World = world };
                found.Add(new NearbyPlayer(stranger, false));
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
    public static void Invalidate() => scannedAt = DateTime.MinValue;
}

/// Someone within layering range: a paired partner's own record, or an unpaired player's stand-in.
internal sealed record NearbyPlayer(PeerRecord Target, bool IsPartner);
