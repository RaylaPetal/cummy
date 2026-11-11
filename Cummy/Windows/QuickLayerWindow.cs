using System;
using System.Linq;
using System.Numerics;
using Cummy.Pairing;
using Cummy.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Cummy.Windows;

/// A small movable droplet button, shown while someone is within layering range. Clicking it opens a dropdown: pick a
/// nearby player (your target, if listed), then layer or lick them (partners always, anyone in Public mode), pair with
/// them, or blacklist them.
public sealed class QuickLayerWindow : ThemedWindow
{
    private const string PopupId = "##cummyQuickLayer";
    private const ImGuiWindowFlags BaseFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar
                                             | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize
                                             | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBackground;

    private readonly Configuration configuration;
    private readonly PairingService pairing;
    private readonly LayerState layers;

    /// `Name@World` of the player chosen in the open dropdown; set when it opens, never changed behind the user's back.
    private string? selectedKey;
    private bool dragging;

    private enum Mode { Layer, Lick }

    /// The dropdown's Layer / Lick toggle; Layer each time it opens. Layer also offers Creampie under the parts.
    private Mode mode;

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
        && PartGrid.NearbyPlayers(pairing).Count > 0;

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
        if (ImGui.IsItemHovered() && !dragging) ImGui.SetTooltip("Layer, lick or pair with someone nearby");

        if (clicked && !wasDragging)
        {
            selectedKey = TargetedPlayer();
            mode = Mode.Layer;
            ImGui.OpenPopup(PopupId);
        }

        using var popup = ImRaii.Popup(PopupId);
        if (popup) DrawDropdown();
    }

    private void DrawDropdown()
    {
        var players = PartGrid.NearbyPlayers(pairing);
        Ui.Heading("Nearby", players.Count);
        foreach (var p in players)
        {
            var isSelected = p.Target.Key.Equals(selectedKey, StringComparison.OrdinalIgnoreCase);
            var clicked = Ui.Card($"quick{p.Target.Key}", isSelected ? CardStyle.Selected : CardStyle.Normal, () =>
            {
                ImGui.TextColored(isSelected ? Ui.Accent : ImGuiColors.DalamudWhite, p.Target.Name);
                ImGui.SameLine();
                Ui.Muted(p.IsPartner ? $"@{p.Target.World} - partner" : $"@{p.Target.World}");
            });
            if (clicked) selectedKey = p.Target.Key;
        }

        // Looked up again every frame: they may have left, been paired or been blacklisted since.
        var selected = players.FirstOrDefault(p => p.Target.Key.Equals(selectedKey, StringComparison.OrdinalIgnoreCase));
        ImGuiHelpers.ScaledDummy(2f);
        if (selected == null)
        {
            Ui.Muted(selectedKey == null ? "Choose someone first." : "They're not nearby any more.");
            PartGrid.Draw(null, false, _ => { }, "quick");
            return;
        }

        var target = selected.Target;
        DrawPlayerActions(selected);
        if (!selected.IsPartner && !pairing.PublicLayering)
        {
            Ui.Muted("Pair with them, or switch to Public in settings, to layer them.");
            return;
        }

        if (ImGui.RadioButton("Layer", mode == Mode.Layer)) mode = Mode.Layer;
        ImGui.SameLine();
        if (ImGui.RadioButton("Lick", mode == Mode.Lick)) mode = Mode.Lick;

        var enabled = mode != Mode.Lick || PartGrid.InRange(target, Proximity.LickRange);
        if (!enabled) Ui.Muted($"{target.Name} is too far away to lick.");
        switch (mode)
        {
            case Mode.Lick:
                PartGrid.Draw(target, enabled, part => pairing.SendLick(target, part), "quicklick", lick: true, isPartner: selected.IsPartner);
                break;
            default:
                PartGrid.Draw(target, enabled, part => pairing.SendLayer(target, part), "quick", isPartner: selected.IsPartner);
                ImGuiHelpers.ScaledDummy(4f);
                Ui.Muted("Creampie");
                PartGrid.DrawHoles(target, enabled, hole => pairing.SendCreampie(target, hole), "quickcreampie", isPartner: selected.IsPartner);
                break;
        }
    }

    /// Pair (unpaired players only) and Blacklist (hold Ctrl) for the selected player.
    private void DrawPlayerActions(NearbyPlayer selected)
    {
        var target = selected.Target;
        if (!selected.IsPartner)
        {
            var pending = pairing.IsInvitePending(target.Name, target.World);
            using (ImRaii.Disabled(pending))
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.UserPlus, pending ? "Pair (invite sent)" : "Pair"))
                    pairing.Invite(target.Name, target.World);
            }
            ImGui.SameLine();
        }
        var ctrl = ImGui.GetIO().KeyCtrl;
        using (ImRaii.Disabled(!ctrl))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Ban, "Blacklist"))
            {
                pairing.Blacklist(target.Name, target.World);
                selectedKey = null;
                PartGrid.Invalidate();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(ctrl
                ? $"Never let {target.Name} layer, lick or invite you{(selected.IsPartner ? " (unpairs them)" : "")}"
                : $"Hold Ctrl to blacklist {target.Name}");
        ImGuiHelpers.ScaledDummy(2f);
    }

    /// The hard target's `Name@World`, when they're listed (nearby, not blacklisted); otherwise nobody.
    private string? TargetedPlayer()
    {
        if (Plugin.TargetManager.Target is not IPlayerCharacter pc) return null;
        var name = pc.Name.TextValue;
        var world = pc.HomeWorld.Value.Name.ExtractText();
        return PartGrid.NearbyPlayers(pairing).FirstOrDefault(p => p.Target.Matches(name, world))?.Target.Key;
    }
}
