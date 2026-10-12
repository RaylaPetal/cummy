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
    private static List<PeerRecord> nearby = [];

    /// One button per part, Body then Face, plus Full Body after the body parts when layering (it isn't a part, so
    /// it can't be licked). Blocked parts are disabled unless <paramref name="lick"/> (blocks only stop layering);
    /// everything is disabled unless <paramref name="enabled"/>. A click calls <paramref name="send"/>.
    public static void Draw(PeerRecord? peer, bool enabled, Action<BodyPart> send, string id, bool lick = false)
    {
        var size = new Vector2(130 * ImGuiHelpers.GlobalScale, 0);
        foreach (var facial in new[] { false, true })
        {
            ImGuiHelpers.ScaledDummy(2f);
            Ui.Muted(facial ? "Face" : "Body");
            using var table = ImRaii.Table($"##parts{id}{facial}", 2);
            if (!table) continue;
            var parts = BodyParts.DisplayOrder.Where(p => p.IsFacial() == facial).ToList();
            if (!facial && !lick) parts.Add(BodyPart.FullBody);
            foreach (var part in parts)
            {
                ImGui.TableNextColumn();
                var blocked = !lick && peer != null && IsBlocked(part, peer.BlockMask);
                using (ImRaii.Disabled(!enabled || peer == null || blocked))
                {
                    if (ImGui.Button(blocked ? $"{part.DisplayName()} (blocked)##{id}{part.Id()}" : $"{part.DisplayName()}##{id}{part.Id()}", size))
                        send(part);
                }
                if (peer != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    if (blocked)
                        ImGui.SetTooltip(part == BodyPart.FullBody && !part.IsBlockedBy(peer.BlockMask)
                            ? $"{peer.Name} has blocked every body part."
                            : $"{peer.Name} has blocked this part.");
                    else if (part == BodyPart.FullBody)
                        ImGui.SetTooltip($"Layers every body part {peer.Name} hasn't blocked, one stage each.");
                    else if (!lick && part.NeedsRecentPartner())
                        ImGui.SetTooltip($"{part.DisplayName()} needs an up-to-date Cummy on {peer.Name}'s side too.");
                }
            }
        }
    }

    /// Full Body is blocked when an older partner blocks it outright, or when every body part it would layer is.
    private static bool IsBlocked(BodyPart part, int blockMask)
        => part.IsBlockedBy(blockMask)
           || (part == BodyPart.FullBody && (BodyParts.RegionalMask & ~blockMask) == 0);

    /// Whether <paramref name="peer"/> is within <paramref name="range"/> yalms of you.
    public static bool InRange(PeerRecord peer, float range) => Proximity.Within(peer, range);

    /// The paired peers within layering range, rescanned at most every 500 ms.
    public static IReadOnlyList<PeerRecord> PartnersInRange(PairingService pairing)
    {
        if (DateTime.UtcNow - scannedAt < ScanInterval) return nearby;
        scannedAt = DateTime.UtcNow;
        var me = Plugin.ObjectTable.LocalPlayer;
        var peers = pairing.Peers;
        var found = new List<PeerRecord>();
        if (me != null && peers.Count > 0)
        {
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj is not IPlayerCharacter pc) continue;
                if (Vector3.Distance(me.Position, pc.Position) > Proximity.LayerRange) continue;
                var name = pc.Name.TextValue;
                var world = pc.HomeWorld.Value.Name.ExtractText();
                if (peers.FirstOrDefault(p => p.Matches(name, world)) is { } peer && !found.Contains(peer))
                    found.Add(peer);
            }
        }
        nearby = found.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return nearby;
    }
}
