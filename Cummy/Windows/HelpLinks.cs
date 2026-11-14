using Dalamud.Bindings.ImGui;

namespace Cummy.Windows;

/// Plugin help: a popup with the GitHub issue tracker and the developer's Discord handle.
internal static class HelpLinks
{
    private const string PopupId = "cummy_help";
    private const string IssuesUrl = "https://github.com/RaylaPetal/corruption/issues";
    private const string DiscordHandle = "raylapetal";

    /// Title-bar clicks run outside the window's ID stack, so the click only arms this and Draw opens the popup.
    private static bool openRequested;

    public static void RequestOpen() => openRequested = true;

    /// Called inside the window's Draw.
    public static void Draw()
    {
        if (openRequested)
        {
            openRequested = false;
            ImGui.OpenPopup(PopupId);
        }

        if (!ImGui.BeginPopup(PopupId))
            return;

        ImGui.TextUnformatted("Need help or found a bug?");
        ImGui.Spacing();

        if (ImGui.Button("Report an issue on GitHub"))
            Dalamud.Utility.Util.OpenLink(IssuesUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("github.com/RaylaPetal/corruption/issues");

        if (ImGui.Button($"Discord: {DiscordHandle}"))
            ImGui.SetClipboardText(DiscordHandle);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Click to copy the username, then add or message it on Discord");

        ImGui.EndPopup();
    }
}
