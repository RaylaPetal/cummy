using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace Cummy.Windows;

/// A "Support me" label and two small Ko-fi and Patreon icon buttons, on one line.
internal static class SupportLinks
{
    private const string KofiUrl = "https://ko-fi.com/raylapetal";
    private const string PatreonUrl = "https://www.patreon.com/cw/RaylaPetal";
    private const string Label = "Support me";

    private static readonly Vector4 White = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 KofiBlue = new(0.161f, 0.671f, 0.878f, 1f);
    private static readonly Vector4 KofiBlueHovered = new(0.29f, 0.75f, 0.93f, 1f);
    private static readonly Vector4 PatreonCoral = new(1f, 0.259f, 0.302f, 1f);
    private static readonly Vector4 PatreonCoralHovered = new(1f, 0.4f, 0.44f, 1f);

    private static float ButtonSize => ImGui.GetFrameHeight();
    private static float IconsWidth => ButtonSize * 2 + ImGui.GetStyle().ItemSpacing.X;

    public static float Width => ImGui.CalcTextSize(Label).X + ImGui.GetStyle().ItemSpacing.X + IconsWidth;
    public static float Height => ButtonSize;

    /// Drawn at the cursor, label then icons on one line.
    public static void Draw()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(Label);
        ImGui.SameLine();
        if (IconButton("kofi", FontAwesomeIcon.MugHot, KofiBlue, KofiBlueHovered))
            Dalamud.Utility.Util.OpenLink(KofiUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Support me on Ko-fi\nko-fi.com/raylapetal");

        ImGui.SameLine();
        if (IconButton("patreon", FontAwesomeIcon.HandHoldingHeart, PatreonCoral, PatreonCoralHovered))
            Dalamud.Utility.Util.OpenLink(PatreonUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Support me on Patreon\npatreon.com/cw/RaylaPetal");
    }

    private static bool IconButton(string id, FontAwesomeIcon icon, Vector4 bg, Vector4 bgHovered)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, bg).Push(ImGuiCol.ButtonHovered, bgHovered).Push(ImGuiCol.ButtonActive, bgHovered);
        return Ui.CenteredIconButton($"support_{id}", icon, ButtonSize, White);
    }
}
