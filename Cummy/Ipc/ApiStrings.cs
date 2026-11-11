using Cummy.State;

namespace Cummy.Ipc;

/// The strings the plugin API returns and raises: outcomes, change causes and access modes. Mirrored by
/// Cummy.Api/CummyIpc.cs (CummyStrings) for other plugins; never rename one, callers compare against them.
public static class ApiStrings
{
    // Shared by every endpoint.
    public const string WrongThread = "wrong-thread";
    public const string Error = "error";
    public const string NoCharacter = "no-character";
    public const string UnknownPart = "unknown-part";

    // Sending (SendLayer, CanLayer, SendLick, Invite, SendMessage).
    public const string Sent = "sent";
    public const string Ok = "ok";
    public const string NotPaired = "not-paired";
    public const string OutOfRange = "out-of-range";
    public const string Blocked = "blocked";
    public const string QueueFull = "queue-full";
    public const string Blacklisted = "blacklisted";
    public const string AlreadyPaired = "already-paired";
    public const string Pending = "pending";
    public const string Invalid = "invalid";
    public const string Disabled = "disabled";

    // Changing the wearer (LayerSelf, CleanSelf, RinseSelf, SetStage).
    public const string Layered = "layered";
    public const string Advanced = "advanced";
    public const string Full = "full";
    public const string Cleaned = "cleaned";
    public const string AlreadyClean = "already-clean";
    public const string Locked = "locked";
    public const string Rinsed = "rinsed";
    public const string Set = "set";
    public const string Unchanged = "unchanged";
    public const string InvalidStage = "invalid-stage";

    // Overlays (SetOverlay).
    public const string Accepted = "accepted";
    public const string TooMany = "too-many";
    public const string Removed = "removed";

    // OnLickResult.
    public const string Licked = "licked";
    public const string Nothing = "nothing";

    // GetAccessMode.
    public const string Pairing = "pairing";
    public const string Public = "public";

    // Arguments.
    public const string AllParts = "all";
    public const string FullBodyImage = "full";

    // OnPartChanged causes.
    public const string CauseLayer = "layer";
    public const string CauseFullBody = "full-body";
    public const string CauseDrip = "drip";
    public const string CauseRinse = "rinse";
    public const string CauseLick = "lick";
    public const string CauseKiss = "kiss";
    public const string CauseDry = "dry";
    public const string CauseClean = "clean";
    public const string CausePlugin = "plugin";

    public static string Cause(ChangeCause cause) => cause switch
    {
        ChangeCause.Layer => CauseLayer,
        ChangeCause.FullBody => CauseFullBody,
        ChangeCause.Drip => CauseDrip,
        ChangeCause.Rinse => CauseRinse,
        ChangeCause.Lick => CauseLick,
        ChangeCause.Kiss => CauseKiss,
        ChangeCause.Dry => CauseDry,
        ChangeCause.Clean => CauseClean,
        _ => CausePlugin,
    };
}
