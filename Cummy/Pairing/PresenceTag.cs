using Cummy.Messaging;

namespace Cummy.Pairing;

/// What a Cummy client publishes about its character in the SimpleHeels tag `Cummy`, which sync services (Lightless,
/// Mare) carry to synced players: having it is how another client knows this player runs Cummy, without any tell.
/// Like `K1|p|80000`: version, mode (`p` Public, `-` Pairing), and the block mask `cmy1 bly` carries.
public readonly record struct PresenceTag(bool Public, int BlockMask)
{
    public const string Key = "Cummy";

    private const string Prefix = "K1";

    public string Format() => $"{Prefix}|{(Public ? "p" : "-")}|{Protocol.Mask(BlockMask)}";

    /// Null for anything that isn't a well-formed tag of this version. Block bits this version doesn't know are dropped.
    public static PresenceTag? Parse(string? text)
    {
        var parts = text?.Split('|');
        if (parts is not { Length: 3 } || parts[0] != Prefix || parts[1] is not ("p" or "-")) return null;
        if (!Protocol.TryMask(parts[2], out var mask, Protocol.MaxMaskDigits)) return null;
        return new PresenceTag(parts[1] == "p", mask & BodyParts.WireMask);
    }
}
