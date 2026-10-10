using System;
using System.Collections.Generic;

namespace Cummy;

/// The layerable regions. The numeric value is the bit in a block mask and the order layers are painted in
/// (Full Body first so regional layers sit on top of it). Never renumber: block masks travel on the wire.
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
}

public static class BodyParts
{
    public const int Count = 12;
    public const int AllMask = (1 << Count) - 1;

    /// The body parts' bits: what `inv`/`acc`/`blk` carry, since older versions reject any other bit.
    public const int BodyMask = (1 << 10) - 1;

    /// Paint order: whole-body coat first, then torso, then limbs.
    public static readonly IReadOnlyList<BodyPart> PaintOrder =
    [
        BodyPart.FullBody, BodyPart.Back, BodyPart.Stomach, BodyPart.Chest, BodyPart.Butt, BodyPart.Groin,
        BodyPart.ArmLeft, BodyPart.ArmRight, BodyPart.LegLeft, BodyPart.LegRight,
        BodyPart.Forehead, BodyPart.Mouth,
    ];

    /// Picker/menu order: body parts, then facial parts (UIs group them with <see cref="IsFacial"/>).
    public static readonly IReadOnlyList<BodyPart> DisplayOrder =
    [
        BodyPart.Chest, BodyPart.Back, BodyPart.Stomach, BodyPart.Butt, BodyPart.ArmLeft, BodyPart.ArmRight,
        BodyPart.LegLeft, BodyPart.LegRight, BodyPart.Groin, BodyPart.FullBody,
        BodyPart.Forehead, BodyPart.Mouth,
    ];

    public static bool IsFacial(this BodyPart part) => part is BodyPart.Forehead or BodyPart.Mouth;

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
