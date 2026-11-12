using System.Collections.Generic;
using System.Linq;
using Cummy.Pairing;

namespace Cummy.Ipc;

/// Reads (version 5): the part catalogue, the wearer's stages and consent settings, partners, cleaning settings,
/// who is nearby, and a dry-run of SendLayer. Each returns its empty value off the framework thread.
public sealed partial class CummyApi
{
    private void RegisterState()
    {
        Read("Cummy.GetParts", GetParts, []);
        Read("Cummy.GetStages", GetStages, new Dictionary<string, int>());
        Read<string, int>("Cummy.GetStage", GetStage, 0);
        Read("Cummy.GetBlockedParts", GetBlockedParts, []);
        Read("Cummy.GetAccessMode", GetAccessMode, "");
        Read<string, string, bool>("Cummy.IsBlacklisted", IsBlacklisted, false);
        Read("Cummy.GetPartners", GetPartners, []);
        Read("Cummy.GetCleaning", GetCleaning, default);
        Read("Cummy.GetNearby", GetNearby, []);
        Outcome<string, string, string>("Cummy.CanLayer", CanLayer);
    }

    /// Every layer target in display order, then Full Body: (id, name, stages, facial, isTarget). Full Body is a target
    /// (it layers every part) and is never stored, so it has no stage of its own.
    public List<(string Id, string Name, int Stages, bool Facial, bool IsTarget)> GetParts()
        => BodyParts.DisplayOrder
            .Select(p => (p.Id(), p.DisplayName(), p.StageCount(), p.IsFacial(), false))
            .Append((BodyPart.FullBody.Id(), BodyPart.FullBody.DisplayName(), 0, false, true))
            .ToList();

    /// Layered parts' stages by part id; clean parts are left out. Empty when logged out.
    public Dictionary<string, int> GetStages()
        => layers.Snapshot().ToDictionary(p => p.Id(), layers.Stage);

    /// One part's stage: 0 when clean (or logged out), -1 for an id that isn't a stored part (Full Body included).
    public int GetStage(string partId)
        => BodyParts.TryParseId(partId ?? "", out var part) && part != BodyPart.FullBody ? layers.Stage(part) : -1;

    private static List<string> PartIds(int mask)
        => BodyParts.DisplayOrder.Where(p => p.IsBlockedBy(mask)).Select(p => p.Id()).ToList();

    /// The wearer's blocked part ids, in display order.
    public List<string> GetBlockedParts() => PartIds(layers.Current?.BlockMask ?? 0);

    /// pairing or public.
    public string GetAccessMode() => pairing.PublicLayering ? ApiStrings.Public : ApiStrings.Pairing;

    public bool IsBlacklisted(string name, string world) => pairing.IsBlacklisted(name ?? "", world ?? "");

    /// Paired partners with the part ids each is last known to block.
    public List<(string Name, string World, List<string> Blocked)> GetPartners()
        => pairing.PeersSorted().Select(p => (p.Name, p.World, PartIds(p.BlockMask))).ToList();

    /// The wearer's cleaning settings, and whether cleaning by hand (or through CleanSelf) is allowed right now.
    public (bool DryOff, int DryMinutes, bool WashWater, bool WashRain, bool Drip, bool ManualClean) GetCleaning()
        => (config.DryOffEnabled, config.DryOffMinutes, config.WashInWater, config.WashInRain, config.DripEnabled,
            config.ManualCleanAllowed);

    /// The players the droplet lists within layer range: partners (marked) always, and other Cummy users (their
    /// SimpleHeels presence tag), never anyone blacklisted. CanLayer says whether one can be layered.
    public List<(string Name, string World, bool Partner)> GetNearby()
    {
        return Proximity.NearbyPlayers(pairing)
            .Select(n => (n.Target.Name, n.Target.World, n.IsPartner))
            .ToList();
    }

    /// What SendLayer would answer, with ok for sent, and nothing sent.
    public string CanLayer(string name, string world, string partId) => CanLayer(name, world, partId, out _, out _);

    private string CanLayer(string name, string world, string partId, out PeerRecord? target, out BodyPart part)
    {
        target = null;
        if (!BodyParts.TryParseId(partId ?? "", out part)) return ApiStrings.UnknownPart;
        // Logged out reads as not-paired, as SendLayer always answered.
        switch (pairing.CheckSend(name ?? "", world ?? "", Proximity.LayerRange, out var peer))
        {
            case SendRefusal.Blacklisted: return ApiStrings.Blacklisted;
            case SendRefusal.NotPaired: return ApiStrings.NotPaired;
            case SendRefusal.OutOfRange: return ApiStrings.OutOfRange;
        }
        // A partner, or in Public mode a stranger with no known blocks (their Cummy still enforces its own).
        if (IsBlocked(part, peer!.BlockMask)) return ApiStrings.Blocked;
        target = peer;
        return ApiStrings.Ok;
    }
}
