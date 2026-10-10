using Cummy.Pairing;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace Cummy.Windows;

/// The body-part grid for one paired peer. Each click sends one layer tell; the window stays open.
public sealed class LayerPickerWindow : ThemedWindow
{
    private readonly PairingService pairing;
    private PeerRecord? peer;

    public LayerPickerWindow(PairingService pairing) : base("Layer###CummyLayerPicker")
    {
        this.pairing = pairing;
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse;
    }

    public void Open(PeerRecord target)
    {
        peer = target;
        WindowName = $"Layer {target.Name}###CummyLayerPicker";
        IsOpen = true;
    }

    protected override void DrawContents()
    {
        if (peer is null || pairing.FindPeer(peer.Name, peer.World) is not { } current)
        {
            Ui.Muted("You're no longer paired with them.");
            return;
        }

        var nearby = PartGrid.IsNearby(current);
        if (!nearby)
        {
            Ui.Icon(FontAwesomeIcon.ExclamationTriangle, Ui.Warning);
            ImGui.SameLine();
            ImGui.TextColored(Ui.Warning, $"{current.Name} isn't nearby.");
            ImGuiHelpers.ScaledDummy(2f);
        }

        PartGrid.Draw(current, nearby, part => pairing.SendLayer(current, part), "picker");
        Ui.FooterHint("Click a part to layer it - this stays open for more");
    }
}
