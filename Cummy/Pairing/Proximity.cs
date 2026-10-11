using System;
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
}
