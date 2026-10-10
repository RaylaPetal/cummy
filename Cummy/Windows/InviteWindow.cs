using System.Linq;
using System.Numerics;
using Cummy.Pairing;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Cummy.Windows;

/// Accept/decline prompt for incoming pairing invites; opens when one arrives, closes when none are left.
public sealed class InviteWindow : ThemedWindow
{
    private readonly PairingService pairing;

    public InviteWindow(PairingService pairing) : base("Cummy pairing###CummyInvites")
    {
        this.pairing = pairing;
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse;
        pairing.InviteReceived += () => IsOpen = true;
    }

    protected override void DrawContents()
    {
        var invites = pairing.PendingInvites;
        if (invites.Count == 0)
        {
            IsOpen = false;
            return;
        }

        ImGui.Dummy(new Vector2(300 * ImGuiHelpers.GlobalScale, 0));
        foreach (var invite in invites.ToArray())
        {
            Ui.Card($"invite{invite.Key}", CardStyle.Banner, () =>
            {
                Ui.Icon(FontAwesomeIcon.UserPlus, Ui.Accent);
                ImGui.SameLine();
                ImGui.TextUnformatted(invite.Name);
                ImGui.SameLine();
                Ui.Muted($"@{invite.World}");
                Ui.Muted("wants to pair with you.");

                using (Ui.AccentButton())
                {
                    if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Check, "Accept"))
                        pairing.AcceptInvite(invite);
                }
                ImGui.SameLine();
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Times, "Decline"))
                    pairing.DeclineInvite(invite);
            });
        }
    }
}
