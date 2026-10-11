using Cummy.Pairing;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace Cummy.Windows;

/// The body-part grid for one paired peer, to layer them or (in lick mode) lick parts off them. Each click sends one
/// tell; the window stays open. Parts are disabled while the peer is out of range for the mode.
public sealed class LayerPickerWindow : ThemedWindow
{
    private readonly PairingService pairing;
    private PeerRecord? peer;
    private bool lick;

    public LayerPickerWindow(PairingService pairing) : base("Layer###CummyLayerPicker")
    {
        this.pairing = pairing;
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse;
    }

    public void Open(PeerRecord target) => Open(target, lick: false);

    public void OpenLick(PeerRecord target) => Open(target, lick: true);

    private void Open(PeerRecord target, bool lick)
    {
        peer = target;
        this.lick = lick;
        WindowName = $"{(lick ? "Lick" : "Layer")} {target.Name}###CummyLayerPicker";
        IsOpen = true;
    }

    protected override void DrawContents()
    {
        if (peer is null || pairing.FindPeer(peer.Name, peer.World) is not { } current)
        {
            Ui.Muted("You're no longer paired with them.");
            return;
        }

        var inRange = PartGrid.InRange(current, lick ? Proximity.LickRange : Proximity.LayerRange);
        if (!inRange)
        {
            Ui.Icon(FontAwesomeIcon.ExclamationTriangle, Ui.Warning);
            ImGui.SameLine();
            ImGui.TextColored(Ui.Warning, $"{current.Name} is too far away{(lick ? " to lick" : "")}.");
            ImGuiHelpers.ScaledDummy(2f);
        }

        if (lick)
        {
            PartGrid.Draw(current, inRange, part => pairing.SendLick(current, part), "lick", lick: true);
            Ui.FooterHint("Click a part to lick it off them - this stays open for more");
        }
        else
        {
            PartGrid.Draw(current, inRange, part => pairing.SendLayer(current, part), "picker");
            Ui.FooterHint("Click a part to layer it - this stays open for more");
        }
    }
}
