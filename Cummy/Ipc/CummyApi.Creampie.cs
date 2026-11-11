using System.Collections.Generic;
using System.Linq;
using Cummy.Pairing;
using Cummy.State;
using Dalamud.Plugin.Ipc;

namespace Cummy.Ipc;

/// Creampies (version 6): send one to another player, creampie the wearer as another plugin decided, read the running
/// drips, and hear when one starts or ends. Holes are "pussy" and "ass".
public sealed partial class CummyApi
{
    private ICallGateProvider<string, bool, int, string, string, string, object> onCreampieChanged = null!;

    private void RegisterCreampie()
    {
        Outcome<string, string, string>("Cummy.SendCreampie", SendCreampie);
        Outcome<string, string, string>("Cummy.CreampieSelf", CreampieSelf);
        Read("Cummy.GetCreampies", GetCreampies, new Dictionary<string, int>());
        onCreampieChanged = Plugin.PluginInterface.GetIpcProvider<string, bool, int, string, string, string, object>(
            "Cummy.OnCreampieChanged");
        creampies.Changed += OnCreampieChanged;
    }

    private void UnhookCreampie() => creampies.Changed -= OnCreampieChanged;

    /// Creampies `name@world` with the normal tell, under the same rules as SendLayer. One of sent, not-paired,
    /// blacklisted, out-of-range, blocked, queue-full or unknown-hole; a tell is queued only for sent.
    public string SendCreampie(string name, string world, string hole)
    {
        if (!Holes.TryParseApiName(hole, out var h)) return ApiStrings.UnknownHole;
        switch (pairing.CheckSend(name ?? "", world ?? "", Proximity.LayerRange, out var peer))
        {
            case SendRefusal.Blacklisted: return ApiStrings.Blacklisted;
            case SendRefusal.NotPaired: return ApiStrings.NotPaired;
            case SendRefusal.OutOfRange: return ApiStrings.OutOfRange;
        }
        // A partner's known blocks; a stranger's are unknown (their Cummy still enforces its own).
        if (h.IsBlockedBy(peer!.BlockMask)) return ApiStrings.Blocked;
        return pairing.SendCreampie(peer, h) ? ApiStrings.Sent : ApiStrings.QueueFull;
    }

    /// Creampies the wearer as if that player had, with no pairing, tell or range, under the wearer's blocks and
    /// blacklist. One of creampied, refreshed, blocked, blacklisted, unknown-hole or no-character.
    public string CreampieSelf(string hole, string sourceName, string sourceWorld)
    {
        if (!Holes.TryParseApiName(hole, out var h)) return ApiStrings.UnknownHole;
        var (name, world) = Source(sourceName, sourceWorld);
        return pairing.CreampieSelf(h, name, world);
    }

    /// Seconds left per dripping hole ("pussy"/"ass"); empty when nothing drips or logged out.
    public Dictionary<string, int> GetCreampies()
        => creampies.Active.ToDictionary(h => h.ApiName(), h => (int)(creampies.Remaining(h)?.TotalSeconds ?? 0));

    private void OnCreampieChanged(Hole hole, bool active, int secondsLeft, CreampieCause cause, string name, string world)
        => Send("Cummy.OnCreampieChanged",
                () => onCreampieChanged.SendMessage(hole.ApiName(), active, secondsLeft, ApiStrings.Cause(cause), name, world));
}
