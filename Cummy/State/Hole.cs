using Glamourer.Api.Enums;

namespace Cummy.State;

/// Where a creampie goes. Each has its own drip timer, block bit, ring slot and accompanying body part.
public enum Hole { Pussy, Ass }

public static class Holes
{
    public static readonly Hole[] All = [Hole.Pussy, Hole.Ass];

    /// Wire id (`cmy1 crp <id>`) and the key in CharacterData.Creampies.
    public static string Id(this Hole hole) => hole == Hole.Pussy ? "pu" : "as";

    /// The plugin API's name.
    public static string ApiName(this Hole hole) => hole == Hole.Pussy ? "pussy" : "ass";

    public static string DisplayName(this Hole hole) => hole == Hole.Pussy ? "Pussy" : "Ass";

    /// The body part a creampie also layers, and whose lick ends it.
    public static BodyPart Part(this Hole hole) => hole == Hole.Pussy ? BodyPart.Groin : BodyPart.Butt;

    /// The block-mask bit of "Creampie: pussy/ass" (<see cref="BodyParts.CreampieMask"/>).
    public static int BlockBit(this Hole hole) => hole == Hole.Pussy ? BodyParts.CreampiePussyBit : BodyParts.CreampieAssBit;

    public static bool IsBlockedBy(this Hole hole, int blockMask) => (blockMask & hole.BlockBit()) != 0;

    /// The ring slot its drip ring goes on: right for pussy, left for ass (each slot has its own VFX id).
    public static ApiEquipSlot RingSlot(this Hole hole) => hole == Hole.Pussy ? ApiEquipSlot.RFinger : ApiEquipSlot.LFinger;

    public static bool TryParseId(string id, out Hole hole)
    {
        hole = id == "as" ? Hole.Ass : Hole.Pussy;
        return id is "pu" or "as";
    }

    public static bool TryParseApiName(string? name, out Hole hole)
    {
        hole = name == "ass" ? Hole.Ass : Hole.Pussy;
        return name is "pussy" or "ass";
    }

    /// The hole whose creampie a lick of <paramref name="part"/> ends, if any.
    public static Hole? ForPart(BodyPart part)
        => part == BodyPart.Groin ? Hole.Pussy : part == BodyPart.Butt ? Hole.Ass : null;
}
