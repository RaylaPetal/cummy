using System;
using System.Collections.Generic;
using System.Linq;

namespace Cummy;

/// The layerable regions. The numeric value is the bit in a block mask and the pixel value in the region maps.
/// Never renumber: block masks travel on the wire.
public enum BodyPart
{
    ArmLeft = 0,
    ArmRight = 1,
    Back = 2,
    Butt = 3,
    Chest = 4,
    Groin = 5,
    LegLeft = 6,
    LegRight = 7,
    Stomach = 8,
    /// Not a part: a layer target that layers every regional part once. Kept for its wire id and block bit.
    FullBody = 9,

    /// Not a part: the wire id of a facial part older versions have. A layer or lick of it is taken as Face.
    Forehead = 10,

    /// The facial part: composited onto the wearer's own face through the face-paint layout (FaceBaker). Older versions
    /// call it Mouth; it keeps Mouth's value and wire id so they understand it.
    Face = 11,

    // Body parts added after the facial ones; older versions don't know them (see BodyParts.ExtendedMask).
    HandLeft = 12,
    HandRight = 13,
    FeetLeft = 14,
    FeetRight = 15,
    LegLeftBack = 16,
    LegRightBack = 17,
}

public static class BodyParts
{
    public const int Count = 18;
    public const int AllMask = (1 << Count) - 1;

    /// The bits `inv`/`acc`/`blk` carry: the parts of the first versions, which reject any other bit.
    public const int LegacyBodyMask = (1 << 10) - 1;

    /// The bits `blx` carries: legacy body parts and facial parts. Versions with facial parts reject any other bit.
    public const int ExtendedMask = (1 << 12) - 1;

    /// Stages of every part.
    public const int MaxStages = 3;

    /// Paint order: torso first, then limbs (feet after legs: their calf strokes overlap), then Face (its own material).
    /// Full Body is not a part that is painted or stored: it layers every part (<see cref="LayerTargets"/>).
    public static readonly IReadOnlyList<BodyPart> PaintOrder =
    [
        BodyPart.Back, BodyPart.Stomach, BodyPart.Chest, BodyPart.Butt, BodyPart.Groin,
        BodyPart.LegLeftBack, BodyPart.LegRightBack, BodyPart.ArmLeft, BodyPart.ArmRight, BodyPart.HandLeft,
        BodyPart.HandRight, BodyPart.LegLeft, BodyPart.LegRight, BodyPart.FeetLeft, BodyPart.FeetRight,
        BodyPart.Face,
    ];

    /// Picker/menu order, one list: Face, then the body parts. Full Body is not listed: the layer picker offers it on
    /// its own.
    public static readonly IReadOnlyList<BodyPart> DisplayOrder =
    [
        BodyPart.Face, BodyPart.Chest, BodyPart.Back, BodyPart.Stomach, BodyPart.Butt, BodyPart.ArmLeft, BodyPart.ArmRight,
        BodyPart.HandLeft, BodyPart.HandRight, BodyPart.LegLeft, BodyPart.LegRight, BodyPart.LegLeftBack,
        BodyPart.LegRightBack, BodyPart.FeetLeft, BodyPart.FeetRight, BodyPart.Groin,
    ];

    /// The regional body parts (Bibo layout, region map), in display order.
    public static readonly IReadOnlyList<BodyPart> Regional = [.. DisplayOrder.Where(p => !p.IsFacial())];

    /// What a Full Body layer layers: every part, Face included.
    public static readonly IReadOnlyList<BodyPart> LayerTargets = DisplayOrder;

    /// The block-mask bits of <see cref="LayerTargets"/>.
    public static readonly int LayerTargetsMask = LayerTargets.Aggregate(0, (mask, p) => mask | p.Bit());

    /// Composited onto the face material rather than the body.
    public static bool IsFacial(this BodyPart part) => part == BodyPart.Face;

    /// Parts older partners ignore: Face (on versions before facial parts) and the body parts added after it.
    public static bool NeedsRecentPartner(this BodyPart part) => (part.Bit() & LegacyBodyMask) == 0;

    /// How many stages the part builds up through.
    public static int StageCount(this BodyPart part) => MaxStages;

    /// Wire id; also the asset folder name (tools/build-assets.py).
    public static string Id(this BodyPart part) => part switch
    {
        BodyPart.ArmLeft => "al",
        BodyPart.ArmRight => "ar",
        BodyPart.Back => "bk",
        BodyPart.Butt => "bt",
        BodyPart.Chest => "ch",
        BodyPart.Groin => "gr",
        BodyPart.LegLeft => "ll",
        BodyPart.LegRight => "lr",
        BodyPart.Stomach => "st",
        BodyPart.FullBody => "fb",
        BodyPart.Forehead => "fh",
        BodyPart.Face => "mo",
        BodyPart.HandLeft => "hl",
        BodyPart.HandRight => "hr",
        BodyPart.FeetLeft => "fl",
        BodyPart.FeetRight => "fr",
        BodyPart.LegLeftBack => "bl",
        BodyPart.LegRightBack => "br",
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    public static string DisplayName(this BodyPart part) => part switch
    {
        BodyPart.ArmLeft => "Arm Left",
        BodyPart.ArmRight => "Arm Right",
        BodyPart.Back => "Back",
        BodyPart.Butt => "Butt",
        BodyPart.Chest => "Chest",
        BodyPart.Groin => "Groin",
        BodyPart.LegLeft => "Leg Left",
        BodyPart.LegRight => "Leg Right",
        BodyPart.Stomach => "Stomach",
        BodyPart.FullBody => "Full Body",
        BodyPart.Forehead => "Forehead",
        BodyPart.Face => "Face",
        BodyPart.HandLeft => "Hand Left",
        BodyPart.HandRight => "Hand Right",
        BodyPart.FeetLeft => "Feet Left",
        BodyPart.FeetRight => "Feet Right",
        BodyPart.LegLeftBack => "Leg Left Back",
        BodyPart.LegRightBack => "Leg Right Back",
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    /// A wire id: a part's, Full Body's (a layer target, not a part), or older versions' Forehead (taken as Face).
    public static bool TryParseId(string id, out BodyPart part)
    {
        if (id == BodyPart.FullBody.Id())
        {
            part = BodyPart.FullBody;
            return true;
        }
        if (id == BodyPart.Forehead.Id())
        {
            part = BodyPart.Face;
            return true;
        }
        foreach (var p in DisplayOrder)
        {
            if (p.Id() == id)
            {
                part = p;
                return true;
            }
        }
        part = default;
        return false;
    }

    /// A part typed by a user: its wire id ("ch") or its name with or without spaces ("Arm Left", "armleft").
    public static bool TryParseName(string text, out BodyPart part)
    {
        var wanted = text.Replace(" ", "").Trim();
        foreach (var p in DisplayOrder)
        {
            if (string.Equals(p.Id(), wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.DisplayName().Replace(" ", ""), wanted, StringComparison.OrdinalIgnoreCase))
            {
                part = p;
                return true;
            }
        }
        part = default;
        return false;
    }

    public static int Bit(this BodyPart part) => 1 << (int)part;

    public static bool IsBlockedBy(this BodyPart part, int blockMask) => (blockMask & part.Bit()) != 0;
}
