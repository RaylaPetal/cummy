namespace Cummy;

/// Short local feedback in the chat log, tagged so it reads as the plugin and not a tell.
public static class Notifier
{
    private const string Tag = "Cummy";

    public static void Info(string text) => Plugin.ChatGui.Print(text, Tag);

    public static void Error(string text) => Plugin.ChatGui.PrintError(text, Tag);
}
