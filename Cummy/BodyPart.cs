using System;
using System.Collections.Generic;

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
    FullBody = 9,

    // Facial parts: composited onto the wearer's own face through the face-paint layout (FaceBaker).
    Forehead = 10,
    Mouth = 11,

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

    /// Stages of a staged part (regional body parts); Full Body and facial parts have one.
    public const int MaxStages = 3;

    /// Paint order: whole-body coat first, then torso, then limbs (feet after legs: their calf strokes overlap).
    public static readonly IReadOnlyList<BodyPart> PaintOrder =
    [
        BodyPart.FullBody, BodyPart.Back, BodyPart.Stomach, BodyPart.Chest, BodyPart.Butt, BodyPart.Groin,
        BodyPart.LegLeftBack, BodyPart.LegRightBack, BodyPart.ArmLeft, BodyPart.ArmRight, BodyPart.HandLeft,
        BodyPart.HandRight, BodyPart.LegLeft, BodyPart.LegRight, BodyPart.FeetLeft, BodyPart.FeetRight,
        BodyPart.Forehead, BodyPart.Mouth,
    ];

    /// Picker/menu order: body parts, then facial parts (UIs group them with <see cref="IsFacial"/>).
    public static readonly IReadOnlyList<BodyPart> DisplayOrder =
    [
        BodyPart.Chest, BodyPart.Back, BodyPart.Stomach, BodyPart.Butt, BodyPart.ArmLeft, BodyPart.ArmRight,
        BodyPart.HandLeft, BodyPart.HandRight, BodyPart.LegLeft, BodyPart.LegRight, BodyPart.LegLeftBack,
        BodyPart.LegRightBack, BodyPart.FeetLeft, BodyPart.FeetRight, BodyPart.Groin, BodyPart.FullBody,
        BodyPart.Forehead, BodyPart.Mouth,
    ];

    public static bool IsFacial(this BodyPart part) => part is BodyPart.Forehead or BodyPart.Mouth;

    /// Parts older partners ignore: facial parts and the body parts added after them.
    public static bool NeedsRecentPartner(this BodyPart part) => (part.Bit() & LegacyBodyMask) == 0;

    /// How many stages the part builds up through: <see cref="MaxStages"/> for regional body parts, else 1.
    public static int StageCount(this BodyPart part)
        => part == BodyPart.FullBody || part.IsFacial() ? 1 : MaxStages;

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
        BodyPart.Mouth => "mo",
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
        BodyPart.Mouth => "Mouth",
        BodyPart.HandLeft => "Hand Left",
        BodyPart.HandRight => "Hand Right",
        BodyPart.FeetLeft => "Feet Left",
        BodyPart.FeetRight => "Feet Right",
        BodyPart.LegLeftBack => "Leg Left Back",
        BodyPart.LegRightBack => "Leg Right Back",
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    public static bool TryParseId(string id, out BodyPart part)
    {
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
