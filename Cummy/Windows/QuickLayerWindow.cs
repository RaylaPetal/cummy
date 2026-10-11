using System.Linq;
using System.Numerics;
using Cummy.Pairing;
using Cummy.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Cummy.Windows;

/// A small movable droplet button, shown while a paired partner is within layering range. Clicking it opens a
/// dropdown: pick a partner in range (your target, if paired), choose Layer or Lick, and click parts.
public sealed class QuickLayerWindow : ThemedWindow
{
    private const string PopupId = "##cummyQuickLayer";
    private const ImGuiWindowFlags BaseFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar
                                             | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize
                                             | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBackground;

    private readonly Configuration configuration;
    private readonly PairingService pairing;
    private readonly LayerState layers;

    /// Partner chosen in the open dropdown; set when it opens, never changed behind the user's back.
    private PeerRecord? selected;
    private bool dragging;

    /// The dropdown's Layer / Lick toggle; Layer each time it opens.
    private bool lickMode;

    public QuickLayerWindow(Configuration configuration, PairingService pairing, LayerState layers)
        : base("Cummy quick layer###CummyQuickLayer", BaseFlags)
    {
        this.configuration = configuration;
        this.pairing = pairing;
        this.layers = layers;
        RespectCloseHotkey = false;
        Position = new Vector2(300, 300);
        PositionCondition = ImGuiCond.FirstUseEver;
        IsOpen = true;
    }

    public override bool DrawConditions()
        => configuration.ShowQuickLayerButton
        && Plugin.ClientState.IsLoggedIn
        && layers.Current is not null
        && PartGrid.PartnersInRange(pairing).Count > 0;

    public override void PreDraw()
    {
        Flags = configuration.LockQuickLayerButton ? BaseFlags | ImGuiWindowFlags.NoMove : BaseFlags;
        base.PreDraw();
    }

    protected override void DrawContents()
    {
        var size = new Vector2(36 * ImGuiHelpers.GlobalScale);
        bool clicked;
        using (ImRaii.PushColor(ImGuiCol.Text, Ui.Accent))
        using (ImRaii.PushFont(UiBuilder.IconFont))
            clicked = ImGui.Button(FontAwesomeIcon.Tint.ToIconString(), size);

        // The button fills the window, so dragging it moves the window (ImGui only drags empty window space). A
        // release after a drag is not a click.
        var wasDragging = dragging;
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3f))
        {
            dragging = true;
            if (!configuration.LockQuickLayerButton)
                ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
        }
        if (!ImGui.IsItemActive()) dragging = false;
        if (ImGui.IsItemHovered() && !dragging) ImGui.SetTooltip("Layer or lick a partner");

        if (clicked && !wasDragging)
        {
            selected = TargetedPartner();
            lickMode = false;
            ImGui.OpenPopup(PopupId);
        }

        using var popup = ImRaii.Popup(PopupId);
        if (popup) DrawDropdown();
    }

    private void DrawDropdown()
    {
        var partners = PartGrid.PartnersInRange(pairing);
        var present = selected != null && partners.Contains(selected);

        Ui.Heading("Partner", partners.Count);
        foreach (var p in partners)
        {
            var isSelected = p == selected;
            var clicked = Ui.Card($"quick{p.Key}", isSelected ? CardStyle.Selected : CardStyle.Normal, () =>
            {
                ImGui.TextColored(isSelected ? Ui.Accent : ImGuiColors.DalamudWhite, p.Name);
                ImGui.SameLine();
                Ui.Muted($"@{p.World}");
            });
            if (clicked) selected = p;
        }
        present = selected != null && partners.Contains(selected);

        ImGuiHelpers.ScaledDummy(2f);
        if (ImGui.RadioButton("Layer", !lickMode)) lickMode = false;
        ImGui.SameLine();
        if (ImGui.RadioButton("Lick", lickMode)) lickMode = true;

        var enabled = present && (!lickMode || PartGrid.InRange(selected!, Proximity.LickRange));
        if (!present)
            Ui.Muted(selected == null ? "Choose a partner first." : $"{selected.Name} is too far away now.");
        else if (!enabled)
            Ui.Muted($"{selected!.Name} is too far away to lick.");

        if (lickMode)
            PartGrid.Draw(present ? selected : null, enabled, part => pairing.SendLick(selected!, part), "quicklick", lick: true);
        else
            PartGrid.Draw(present ? selected : null, enabled, part => pairing.SendLayer(selected!, part), "quick");
    }

    /// The hard target, when it is a paired partner within layering range; otherwise nobody.
    private PeerRecord? TargetedPartner()
    {
        if (Plugin.TargetManager.Target is not IPlayerCharacter pc) return null;
        var peer = pairing.FindPeer(pc.Name.TextValue, pc.HomeWorld.Value.Name.ExtractText());
        return peer != null && PartGrid.PartnersInRange(pairing).Contains(peer) ? peer : null;
    }
}
