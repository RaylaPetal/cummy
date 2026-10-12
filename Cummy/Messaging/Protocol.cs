using System;
using System.Globalization;
using System.Security.Cryptography;

namespace Cummy.Messaging;

public enum Verb
{
    Invite,
    Accept,
    Unpair,
    Layer,
    Block,
    BlockExtended,
    BlockAll,
    Lick,
    Licked,
}

public sealed record ProtocolMessage(Verb Verb, string? Nonce = null, int BlockMask = 0, BodyPart Part = default);

/// The `cmy1` tell format: `cmy1 inv <nonce> <mask>`, `cmy1 acc <nonce> <mask>`, `cmy1 unp`, `cmy1 lay <part>`,
/// `cmy1 blk <mask>`, `cmy1 blx <mask>`, `cmy1 bly <mask>`, `cmy1 lck <part>` (a lick of the receiver's
/// part), `cmy1 lkd <part>` (the licked wearer's reply that it cleaned that part). Short lowercase ASCII only, so ChatSender can whitelist
/// it exactly. Each block message carries only the bits every version that understands it accepts, because those
/// versions reject the whole message otherwise (and would refuse to pair): `inv`/`acc`/`blk` the first body parts
/// (<see cref="BodyParts.LegacyBodyMask"/>), `blx` those plus the facial parts (<see cref="BodyParts.ExtendedMask"/>),
/// `bly` every part. Versions that don't know a verb ignore it. The receiver replaces only the bits a message covers.
public static class Protocol
{
    /// Every protocol tell starts with this, whatever its version; recognised ones are hidden from chat.
    public const string Family = "cmy";
    public const string Prefix = "cmy1";

    public const int NonceLength = 8;
    private const string NonceAlphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string NewNonce()
    {
        Span<char> chars = stackalloc char[NonceLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = NonceAlphabet[RandomNumberGenerator.GetInt32(NonceAlphabet.Length)];
        return new string(chars);
    }

    public static string Invite(string nonce, int blockMask) => $"{Prefix} inv {nonce} {Mask(blockMask & BodyParts.LegacyBodyMask)}";
    public static string Accept(string nonce, int blockMask) => $"{Prefix} acc {nonce} {Mask(blockMask & BodyParts.LegacyBodyMask)}";
    public static string Unpair() => $"{Prefix} unp";
    public static string Layer(BodyPart part) => $"{Prefix} lay {part.Id()}";
    public static string Lick(BodyPart part) => $"{Prefix} lck {part.Id()}";
    public static string Licked(BodyPart part) => $"{Prefix} lkd {part.Id()}";
    public static string Block(int blockMask) => $"{Prefix} blk {Mask(blockMask & BodyParts.LegacyBodyMask)}";
    public static string BlockExtended(int blockMask) => $"{Prefix} blx {Mask(blockMask & BodyParts.ExtendedMask)}";
    public static string BlockAll(int blockMask) => $"{Prefix} bly {Mask(blockMask)}";

    /// The bits a block message of <paramref name="verb"/> covers: the receiver replaces these and keeps the rest.
    public static int Covers(Verb verb) => verb switch
    {
        Verb.Block => BodyParts.LegacyBodyMask,
        Verb.BlockExtended => BodyParts.ExtendedMask,
        Verb.BlockAll => BodyParts.AllMask,
        _ => 0,
    };

    /// Full Body's bit is never sent: it isn't blockable on its own any more (blocking parts limits it), and a cleared
    /// bit tells older partners they may send it.
    private static string Mask(int mask)
        => (mask & BodyParts.AllMask & ~BodyPart.FullBody.Bit()).ToString("x", CultureInfo.InvariantCulture);

    /// Whether a tell belongs to this plugin at all: `cmy` followed by a version number. Such tells are hidden
    /// even when their version or content is not understood.
    public static bool IsProtocolText(string text)
    {
        if (!text.StartsWith(Family, StringComparison.Ordinal)) return false;
        var i = Family.Length;
        var digits = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i])) { i++; digits++; }
        return digits > 0 && (i == text.Length || text[i] == ' ');
    }

    /// Null for anything that is not a well-formed message of the supported version.
    public static ProtocolMessage? Parse(string text)
    {
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != Prefix) return null;

        switch (parts[1])
        {
            case "inv" when parts.Length == 4 && IsNonce(parts[2]) && TryMask(parts[3], out var invMask):
                return new ProtocolMessage(Verb.Invite, Nonce: parts[2], BlockMask: invMask);
            case "acc" when parts.Length == 4 && IsNonce(parts[2]) && TryMask(parts[3], out var accMask):
                return new ProtocolMessage(Verb.Accept, Nonce: parts[2], BlockMask: accMask);
            case "unp" when parts.Length == 2:
                return new ProtocolMessage(Verb.Unpair);
            case "lay" when parts.Length == 3 && BodyParts.TryParseId(parts[2], out var part):
                return new ProtocolMessage(Verb.Layer, Part: part);
            case "lck" when parts.Length == 3 && BodyParts.TryParseId(parts[2], out var licked):
                return new ProtocolMessage(Verb.Lick, Part: licked);
            case "lkd" when parts.Length == 3 && BodyParts.TryParseId(parts[2], out var confirmed):
                return new ProtocolMessage(Verb.Licked, Part: confirmed);
            case "blk" when parts.Length == 3 && TryMask(parts[2], out var blkMask):
                return new ProtocolMessage(Verb.Block, BlockMask: blkMask & BodyParts.LegacyBodyMask);
            case "blx" when parts.Length == 3 && TryMask(parts[2], out var blxMask):
                return new ProtocolMessage(Verb.BlockExtended, BlockMask: blxMask & BodyParts.ExtendedMask);
            case "bly" when parts.Length == 3 && TryMask(parts[2], out var blyMask, MaxMaskDigits):
                return new ProtocolMessage(Verb.BlockAll, BlockMask: blyMask & BodyParts.AllMask);
            default:
                return null;
        }
    }

    private static bool IsNonce(string s)
    {
        if (s.Length != NonceLength) return false;
        foreach (var c in s)
            if (NonceAlphabet.IndexOf(c) < 0) return false;
        return true;
    }

    /// `bly` masks may be this long, so a later version can add parts without this one rejecting the message; bits
    /// for parts this version doesn't know are dropped.
    private const int MaxMaskDigits = 8;

    /// Masks of the older verbs: at most 3 hex digits and no unknown bits, as every version has required.
    private static bool TryMask(string s, out int mask, int maxDigits = 3)
    {
        mask = 0;
        if (s.Length == 0 || s.Length > maxDigits) return false;
        if (!uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) return false;
        if (maxDigits == 3 && (value & ~(uint)BodyParts.AllMask) != 0) return false;
        mask = (int)(value & int.MaxValue);
        return true;
    }
}
