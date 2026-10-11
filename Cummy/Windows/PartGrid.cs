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

    /// One button per part, Body then Face. Blocked parts are disabled unless <paramref name="lick"/> (blocks only
    /// stop layering); everything is disabled unless <paramref name="enabled"/>. A click calls <paramref name="send"/>.
    public static void Draw(PeerRecord? peer, bool enabled, Action<BodyPart> send, string id, bool lick = false)
    {
        var size = new Vector2(130 * ImGuiHelpers.GlobalScale, 0);
        foreach (var facial in new[] { false, true })
        {
            ImGuiHelpers.ScaledDummy(2f);
            Ui.Muted(facial ? "Face" : "Body");
            using var table = ImRaii.Table($"##parts{id}{facial}", 2);
            if (!table) continue;
            foreach (var part in BodyParts.DisplayOrder)
            {
                if (part.IsFacial() != facial) continue;
                ImGui.TableNextColumn();
                var blocked = !lick && peer != null && part.IsBlockedBy(peer.BlockMask);
                using (ImRaii.Disabled(!enabled || peer == null || blocked))
                {
                    if (ImGui.Button(blocked ? $"{part.DisplayName()} (blocked)##{id}{part.Id()}" : $"{part.DisplayName()}##{id}{part.Id()}", size))
                        send(part);
                }
                if (peer != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    if (blocked) ImGui.SetTooltip($"{peer.Name} has blocked this part.");
                    else if (!lick && part.NeedsRecentPartner())
                        ImGui.SetTooltip($"{part.DisplayName()} needs an up-to-date Cummy on {peer.Name}'s side too.");
                }
            }
        }
    }

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
