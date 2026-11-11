using System.Collections.Generic;
using System.Linq;
using Cummy.Compositor;
using Dalamud.Plugin.Ipc;

namespace Cummy.Ipc;

/// Plugin overlays (version 5): other plugins' own art on the wearer, composited above Cummy's layers
/// (<see cref="PluginOverlays"/>).
public sealed partial class CummyApi
{
    private ICallGateProvider<string, string, string, object> onOverlayFailed = null!;
    private ICallGateProvider<string, string, object> onOverlayRemoved = null!;

    private void RegisterOverlays()
    {
        Outcome<string, string, string, string, string, int>("Cummy.SetOverlay", SetOverlay);
        Outcome<string, string>("Cummy.RemoveOverlay", RemoveOverlay);
        Outcome<string>("Cummy.ClearOverlays", ClearOverlays);
        Read("Cummy.GetOverlays", GetOverlays, []);
        onOverlayFailed = Plugin.PluginInterface.GetIpcProvider<string, string, string, object>("Cummy.OnOverlayFailed");
        onOverlayRemoved = Plugin.PluginInterface.GetIpcProvider<string, string, object>("Cummy.OnOverlayRemoved");
        overlays.Failed += OnOverlayFailed;
        overlays.Removed += OnOverlayRemoved;
    }

    private void UnhookOverlays()
    {
        overlays.Failed -= OnOverlayFailed;
        overlays.Removed -= OnOverlayRemoved;
    }

    /// Shows or replaces <paramref name="owner"/>'s overlay <paramref name="key"/>. One of accepted, invalid,
    /// unknown-part, too-many or disabled; decode problems arrive later as OnOverlayFailed.
    public string SetOverlay(string owner, string key, string target, string diffusePath, string normalPath, int opacity)
        => overlays.Set(owner, key, target, diffusePath, normalPath, opacity) switch
        {
            SetOverlayOutcome.Accepted => ApiStrings.Accepted,
            SetOverlayOutcome.UnknownPart => ApiStrings.UnknownPart,
            SetOverlayOutcome.TooMany => ApiStrings.TooMany,
            SetOverlayOutcome.Disabled => ApiStrings.Disabled,
            _ => ApiStrings.Invalid,
        };

    /// removed or unchanged.
    public string RemoveOverlay(string owner, string key)
        => overlays.Remove(owner ?? "", key ?? "") ? ApiStrings.Removed : ApiStrings.Unchanged;

    /// removed or unchanged.
    public string ClearOverlays(string owner) => overlays.Clear(owner ?? "") > 0 ? ApiStrings.Removed : ApiStrings.Unchanged;

    /// (owner, key, target) of every overlay showing, bottom first.
    public List<(string Owner, string Key, string Target)> GetOverlays()
        => overlays.Showing.Select(e => (e.Owner, e.Key, e.Target)).ToList();

    private void OnOverlayFailed(string owner, string key, string reason)
        => Send("Cummy.OnOverlayFailed", () => onOverlayFailed.SendMessage(owner, key, reason));

    private void OnOverlayRemoved(string owner, string key)
        => Send("Cummy.OnOverlayRemoved", () => onOverlayRemoved.SendMessage(owner, key));
}
