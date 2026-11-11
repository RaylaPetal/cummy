using System.Collections.Generic;
using Cummy.State;
using Dalamud.Plugin.Ipc;

namespace Cummy.Ipc;

/// Events (version 5): every stage change with its cause, login/logout, the wearer's blocks, partners/mode/blacklist,
/// invites and lick results. OnLayerReceived and OnLayersChanged (version 1-2) are raised as before.
public sealed partial class CummyApi
{
    private ICallGateProvider<string, int, int, string, string, string, object> onPartChanged = null!;
    private ICallGateProvider<bool, object> onCharacterChanged = null!;
    private ICallGateProvider<List<string>, object> onBlocksChanged = null!;
    private ICallGateProvider<object> onAccessChanged = null!;
    private ICallGateProvider<string, string, object> onInviteReceived = null!;
    private ICallGateProvider<string, string, string, string, object> onLickResult = null!;

    private void RegisterEvents()
    {
        var pi = Plugin.PluginInterface;
        onPartChanged = pi.GetIpcProvider<string, int, int, string, string, string, object>("Cummy.OnPartChanged");
        onCharacterChanged = pi.GetIpcProvider<bool, object>("Cummy.OnCharacterChanged");
        onBlocksChanged = pi.GetIpcProvider<List<string>, object>("Cummy.OnBlocksChanged");
        onAccessChanged = pi.GetIpcProvider<object>("Cummy.OnAccessChanged");
        onInviteReceived = pi.GetIpcProvider<string, string, object>("Cummy.OnInviteReceived");
        onLickResult = pi.GetIpcProvider<string, string, string, string, object>("Cummy.OnLickResult");

        layers.PartsChanged += OnPartsChanged;
        layers.CharacterChanged += OnCharacterChanged;
        pairing.BlocksChanged += OnBlocksChanged;
        pairing.AccessChanged += OnAccessChanged;
        pairing.InviteReceived += OnInviteReceived;
        pairing.LickResult += OnLickResult;
    }

    private void UnhookEvents()
    {
        layers.PartsChanged -= OnPartsChanged;
        layers.CharacterChanged -= OnCharacterChanged;
        pairing.BlocksChanged -= OnBlocksChanged;
        pairing.AccessChanged -= OnAccessChanged;
        pairing.InviteReceived -= OnInviteReceived;
        pairing.LickResult -= OnLickResult;
        UnhookMessaging();
        UnhookOverlays();
        UnhookCreampie();
    }

    /// One OnPartChanged per changed part, in paint order.
    private void OnPartsChanged(IReadOnlyList<PartDelta> deltas, LayerChange change)
    {
        var cause = ApiStrings.Cause(change.Cause);
        foreach (var d in deltas)
            Send("Cummy.OnPartChanged",
                 () => onPartChanged.SendMessage(d.Part.Id(), d.Old, d.New, cause, change.SourceName, change.SourceWorld));
    }

    private void OnCharacterChanged(bool loggedIn)
        => Send("Cummy.OnCharacterChanged", () => onCharacterChanged.SendMessage(loggedIn));

    private void OnBlocksChanged()
    {
        var blocked = GetBlockedParts();
        Send("Cummy.OnBlocksChanged", () => onBlocksChanged.SendMessage(blocked));
    }

    private void OnAccessChanged() => Send("Cummy.OnAccessChanged", () => onAccessChanged.SendMessage());

    private void OnInviteReceived(string name, string world)
        => Send("Cummy.OnInviteReceived", () => onInviteReceived.SendMessage(name, world));

    private void OnLickResult(string name, string world, BodyPart part, bool licked)
        => Send("Cummy.OnLickResult",
                () => onLickResult.SendMessage(name, world, part.Id(), licked ? ApiStrings.Licked : ApiStrings.Nothing));
}
