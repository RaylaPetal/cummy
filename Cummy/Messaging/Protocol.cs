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
}

public sealed record ProtocolMessage(Verb Verb, string? Nonce = null, int BlockMask = 0, BodyPart Part = default);

/// The `cmy1` tell format: `cmy1 inv <nonce> <mask>`, `cmy1 acc <nonce> <mask>`, `cmy1 unp`, `cmy1 lay <part>`,
/// `cmy1 blk <mask>`, `cmy1 blx <mask>`. Short lowercase ASCII only, so ChatSender can whitelist it exactly.
/// `inv`/`acc`/`blk` carry only the body parts' bits, because older versions reject masks with other bits (and
/// would refuse to pair); `blx` carries the full mask, facial parts included, and older versions ignore it.
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

    public static string Invite(string nonce, int blockMask) => $"{Prefix} inv {nonce} {Mask(blockMask & BodyParts.BodyMask)}";
    public static string Accept(string nonce, int blockMask) => $"{Prefix} acc {nonce} {Mask(blockMask & BodyParts.BodyMask)}";
    public static string Unpair() => $"{Prefix} unp";
    public static string Layer(BodyPart part) => $"{Prefix} lay {part.Id()}";
    public static string Block(int blockMask) => $"{Prefix} blk {Mask(blockMask & BodyParts.BodyMask)}";
    public static string BlockExtended(int blockMask) => $"{Prefix} blx {Mask(blockMask)}";

    private static string Mask(int mask) => (mask & BodyParts.AllMask).ToString("x", CultureInfo.InvariantCulture);

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
            case "blk" when parts.Length == 3 && TryMask(parts[2], out var blkMask):
                return new ProtocolMessage(Verb.Block, BlockMask: blkMask & BodyParts.BodyMask);
            case "blx" when parts.Length == 3 && TryMask(parts[2], out var blxMask):
                return new ProtocolMessage(Verb.BlockExtended, BlockMask: blxMask);
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

    private static bool TryMask(string s, out int mask)
    {
        mask = 0;
        if (s.Length is 0 or > 3) return false;
        if (!int.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) return false;
        if ((value & ~BodyParts.AllMask) != 0) return false;
        mask = value;
        return true;
    }
}
