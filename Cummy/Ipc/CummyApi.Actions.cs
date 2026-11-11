using Cummy.Pairing;
using Cummy.State;

namespace Cummy.Ipc;

/// Actions (version 5): lick and invite another player, and clean (version 6: also `pussy`/`ass`), rinse or set a stage
/// on the wearer, each under the
/// same rules as the UI. Cleaning or lowering a stage is locked while drying off or washing off is on, in every build.
public sealed partial class CummyApi
{
    private void RegisterActions()
    {
        Outcome<string, string, string>("Cummy.SendLick", SendLick);
        Outcome<string, string>("Cummy.Invite", Invite);
        Outcome<string>("Cummy.CleanSelf", CleanSelf);
        Outcome("Cummy.RinseSelf", RinseSelf);
        Outcome<string, int, string, string>("Cummy.SetStage", SetStage);
    }

    private static LayerChange ByPlugin(string name = "", string world = "") => new(ChangeCause.Plugin, name, world);

    /// One of sent, not-paired, blacklisted, out-of-range, queue-full or unknown-part. The result arrives later as
    /// OnLickResult (licked, or nothing once the lick times out).
    public string SendLick(string name, string world, string partId)
    {
        // Full Body isn't a part, so it can't be licked.
        if (!BodyParts.TryParseId(partId ?? "", out var part) || part == BodyPart.FullBody) return ApiStrings.UnknownPart;
        switch (pairing.CheckSend(name ?? "", world ?? "", Proximity.LickRange, out var peer))
        {
            case SendRefusal.Blacklisted: return ApiStrings.Blacklisted;
            case SendRefusal.NotPaired: return ApiStrings.NotPaired;
            case SendRefusal.OutOfRange: return ApiStrings.OutOfRange;
        }
        return pairing.SendLick(peer!, part) ? ApiStrings.Sent : ApiStrings.QueueFull;
    }

    /// One of sent, already-paired, pending, blacklisted, queue-full or no-character.
    public string Invite(string name, string world)
        => pairing.TryInvite(name?.Trim() ?? "", world?.Trim() ?? "") switch
        {
            InviteOutcome.Sent => ApiStrings.Sent,
            InviteOutcome.AlreadyPaired => ApiStrings.AlreadyPaired,
            InviteOutcome.Pending => ApiStrings.Pending,
            InviteOutcome.Blacklisted => ApiStrings.Blacklisted,
            InviteOutcome.QueueFull => ApiStrings.QueueFull,
            _ => ApiStrings.NoCharacter,
        };

    /// Cleans one part, or everything for "all". One of cleaned, already-clean, locked, unknown-part or no-character.
    public string CleanSelf(string partId)
    {
        var all = partId == ApiStrings.AllParts;
        var isHole = Holes.TryParseApiName(partId, out var hole);
        var part = default(BodyPart);
        if (!all && !isHole && (!BodyParts.TryParseId(partId ?? "", out part) || part == BodyPart.FullBody))
            return ApiStrings.UnknownPart;
        if (layers.Current is null) return ApiStrings.NoCharacter;
        if (!config.ManualCleanAllowed) return ApiStrings.Locked;
        bool cleaned;
        if (isHole)
            cleaned = creampies.End(hole, CreampieCause.Plugin);
        else if (all)
            cleaned = layers.CleanAll(ByPlugin()) | creampies.EndAll(CreampieCause.Plugin) > 0;   // and both creampies
        else
            cleaned = layers.Clean(part, ByPlugin());
        return cleaned ? ApiStrings.Cleaned : ApiStrings.AlreadyClean;
    }

    /// Every layered part one stage down, as washing off does. Allowed when cleaning by hand is, or while washing in
    /// water is on (the caller is the water). One of rinsed, already-clean, locked or no-character.
    public string RinseSelf()
    {
        if (layers.Current is null) return ApiStrings.NoCharacter;
        if (!config.ManualCleanAllowed && !config.WashInWater) return ApiStrings.Locked;
        return layers.Rinse() ? ApiStrings.Rinsed : ApiStrings.AlreadyClean;
    }

    /// Sets a stored part to <paramref name="stage"/> (0 to its stage count). Raising follows layering rules (blocks,
    /// the source's blacklisting); lowering follows cleaning rules (locked). One of set, unchanged, blocked,
    /// blacklisted, locked, invalid-stage, unknown-part or no-character.
    public string SetStage(string partId, int stage, string sourceName, string sourceWorld)
    {
        if (!BodyParts.TryParseId(partId ?? "", out var part) || part == BodyPart.FullBody) return ApiStrings.UnknownPart;
        if (layers.Current is not { } me) return ApiStrings.NoCharacter;
        if (stage < 0 || stage > part.StageCount()) return ApiStrings.InvalidStage;
        var current = layers.Stage(part);
        if (stage == current) return ApiStrings.Unchanged;

        var (name, world) = (sourceName?.Trim() ?? "", sourceWorld?.Trim() ?? "");
        if (stage > current)
        {
            if (part.IsBlockedBy(me.BlockMask)) return ApiStrings.Blocked;
            if (name.Length > 0 && me.IsBlacklisted(name, world)) return ApiStrings.Blacklisted;
        }
        else if (!config.ManualCleanAllowed)
        {
            return ApiStrings.Locked;
        }
        return layers.SetStage(part, stage, ByPlugin(name, world)) ? ApiStrings.Set : ApiStrings.Unchanged;
    }
}
