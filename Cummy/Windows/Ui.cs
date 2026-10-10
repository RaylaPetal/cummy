using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Cummy.Windows;

public enum PillTone { Neutral, Green, Amber, Accent }

public enum CardStyle { Normal, Selected, Banner, MutedBanner }

/// Small shared pieces so the windows look alike: the palette, the theme every Cummy window pushes, and the card,
/// pill and banner widgets.
internal static class Ui
{
    // ── Palette ──────────────────────────────────────────────────────────────

    public static readonly Vector4 WindowBg = Hex(0x1C1622);
    public static readonly Vector4 CardBg = Hex(0x2A2131);
    public static readonly Vector4 CardHover = Hex(0x352A3E);
    public static readonly Vector4 FrameBg = Hex(0x241C2A);
    public static readonly Vector4 Border = Hex(0x3D3046);
    public static readonly Vector4 Accent = Hex(0xED8CBF);
    public static readonly Vector4 AccentStrong = Hex(0xE0558F);
    public static readonly Vector4 MutedText = Hex(0x9A8FA3);
    public static readonly Vector4 Warning = ImGuiColors.DalamudOrange;

    private static readonly Vector4 TitleBg = Hex(0x17121C);
    private static readonly Vector4 TitleBgActive = Hex(0x2A1F31);
    private static readonly Vector4 PopupBg = Hex(0x1F1826);
    private static readonly Vector4 ButtonBg = Hex(0x2E2436);
    private static readonly Vector4 ButtonHover = Hex(0x3E2F48);
    private static readonly Vector4 HeaderBg = Hex(0x3A2D43);
    private static readonly Vector4 HeaderHover = Hex(0x463650);

    private static readonly (Vector4 Bg, Vector4 Text) PillNeutral = (Hex(0x3A2D43), Hex(0xD9CFE0));
    private static readonly (Vector4 Bg, Vector4 Text) PillGreen = (Hex(0x2E4A3A), Hex(0x7FD6A0));
    private static readonly (Vector4 Bg, Vector4 Text) PillAmber = (Hex(0x4A3A22), Hex(0xE8B36A));
    private static readonly (Vector4 Bg, Vector4 Text) PillAccent = (Hex(0x4A2338), Accent);

    private static Vector4 Hex(uint rgb, float alpha = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    private static Vector4 WithAlpha(Vector4 c, float alpha) => c with { W = alpha };

    // ── Theme ────────────────────────────────────────────────────────────────

    /// Pushes Cummy's colours and style vars; dispose to pop them. Windows push it in PreDraw and pop it in PostDraw,
    /// so nothing leaks into other windows.
    public static IDisposable Theme()
    {
        var colors = ImRaii.PushColor(ImGuiCol.WindowBg, WindowBg)
            .Push(ImGuiCol.ChildBg, Vector4.Zero)
            .Push(ImGuiCol.PopupBg, PopupBg)
            .Push(ImGuiCol.Border, Border)
            .Push(ImGuiCol.TitleBg, TitleBg)
            .Push(ImGuiCol.TitleBgActive, TitleBgActive)
            .Push(ImGuiCol.TitleBgCollapsed, TitleBg)
            .Push(ImGuiCol.FrameBg, FrameBg)
            .Push(ImGuiCol.FrameBgHovered, CardHover)
            .Push(ImGuiCol.FrameBgActive, HeaderBg)
            .Push(ImGuiCol.Button, ButtonBg)
            .Push(ImGuiCol.ButtonHovered, ButtonHover)
            .Push(ImGuiCol.ButtonActive, WithAlpha(AccentStrong, 0.7f))
            .Push(ImGuiCol.Header, HeaderBg)
            .Push(ImGuiCol.HeaderHovered, HeaderHover)
            .Push(ImGuiCol.HeaderActive, WithAlpha(AccentStrong, 0.6f))
            .Push(ImGuiCol.CheckMark, Accent)
            .Push(ImGuiCol.SliderGrab, Accent)
            .Push(ImGuiCol.SliderGrabActive, AccentStrong)
            .Push(ImGuiCol.Tab, FrameBg)
            .Push(ImGuiCol.TabHovered, HeaderHover)
            .Push(ImGuiCol.TabActive, HeaderBg)
            .Push(ImGuiCol.TabUnfocused, FrameBg)
            .Push(ImGuiCol.TabUnfocusedActive, HeaderBg)
            .Push(ImGuiCol.Separator, Border)
            .Push(ImGuiCol.SeparatorHovered, HeaderHover)
            .Push(ImGuiCol.SeparatorActive, Accent)
            .Push(ImGuiCol.ResizeGrip, WithAlpha(Border, 0.5f))
            .Push(ImGuiCol.ResizeGripHovered, HeaderHover)
            .Push(ImGuiCol.ResizeGripActive, Accent)
            .Push(ImGuiCol.ScrollbarBg, Vector4.Zero)
            .Push(ImGuiCol.ScrollbarGrab, Border)
            .Push(ImGuiCol.ScrollbarGrabHovered, HeaderHover)
            .Push(ImGuiCol.ScrollbarGrabActive, Accent)
            .Push(ImGuiCol.TableHeaderBg, HeaderBg)
            .Push(ImGuiCol.TableBorderLight, Border)
            .Push(ImGuiCol.TableBorderStrong, Border)
            .Push(ImGuiCol.TableRowBg, Vector4.Zero)
            .Push(ImGuiCol.TableRowBgAlt, WithAlpha(CardBg, 0.6f))
            .Push(ImGuiCol.TextDisabled, MutedText);

        var scale = ImGuiHelpers.GlobalScale;
        var style = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 8f * scale)
            .Push(ImGuiStyleVar.ChildRounding, 6f * scale)
            .Push(ImGuiStyleVar.FrameRounding, 6f * scale)
            .Push(ImGuiStyleVar.PopupRounding, 6f * scale)
            .Push(ImGuiStyleVar.GrabRounding, 4f * scale)
            .Push(ImGuiStyleVar.TabRounding, 6f * scale)
            .Push(ImGuiStyleVar.ScrollbarRounding, 6f * scale)
            .Push(ImGuiStyleVar.WindowPadding, new Vector2(10f, 10f) * scale)
            .Push(ImGuiStyleVar.FramePadding, new Vector2(8f, 5f) * scale)
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(6f, 6f) * scale);

        return new Scope(style, colors);
    }

    private sealed class Scope(IDisposable style, IDisposable colors) : IDisposable
    {
        public void Dispose()
        {
            style.Dispose();
            colors.Dispose();
        }
    }

    /// Colours for the one primary (pink) button of a group, such as Clean everything or Accept.
    public static IDisposable AccentButton()
        => ImRaii.PushColor(ImGuiCol.Button, AccentStrong)
            .Push(ImGuiCol.ButtonHovered, Accent)
            .Push(ImGuiCol.ButtonActive, Accent)
            .Push(ImGuiCol.Text, WindowBg);

    // ── Text ─────────────────────────────────────────────────────────────────

    /// A section title with a rule under it.
    public static void Heading(string text)
    {
        ImGui.TextColored(Accent, text);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);
    }

    /// A section title with a count pill next to it, and a rule under it.
    public static void Heading(string text, int count)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Accent, text);
        ImGui.SameLine();
        Pill(count.ToString(), PillTone.Accent);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);
    }

    public static void Muted(string text) => ImGui.TextColored(MutedText, text);

    public static void MutedWrapped(string text)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Text, MutedText);
        ImGui.TextWrapped(text);
    }

    public static void Error(string text)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
        ImGui.TextWrapped(text);
    }

    public static void Icon(FontAwesomeIcon icon, Vector4 color)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(color, icon.ToIconString());
    }

    /// A muted hint with a pointer icon. With <paramref name="pinToBottom"/> it is pushed to the bottom of the window
    /// when there is room; never pin in auto-resizing windows, which would then keep their grown size.
    public static void FooterHint(string text, bool pinToBottom = false)
    {
        var room = ImGui.GetContentRegionAvail().Y - ImGui.GetTextLineHeightWithSpacing() - ImGui.GetStyle().ItemSpacing.Y;
        if (pinToBottom && room > 0) ImGui.Dummy(new Vector2(0, room));
        else ImGuiHelpers.ScaledDummy(4f);
        Icon(FontAwesomeIcon.MousePointer, MutedText);
        ImGui.SameLine();
        Muted(text);
    }

    // ── Pills ────────────────────────────────────────────────────────────────

    private static Vector2 PillPadding => new Vector2(7f, 2f) * ImGuiHelpers.GlobalScale;

    public static float PillWidth(string text) => ImGui.CalcTextSize(text).X + PillPadding.X * 2;

    /// A small rounded badge, vertically centred on a frame-height line.
    public static void Pill(string text, PillTone tone)
    {
        var (bg, fg) = tone switch
        {
            PillTone.Green => PillGreen,
            PillTone.Amber => PillAmber,
            PillTone.Accent => PillAccent,
            _ => PillNeutral,
        };
        var textSize = ImGui.CalcTextSize(text);
        var size = textSize + PillPadding * 2;
        var lineHeight = Math.Max(ImGui.GetFrameHeight(), size.Y);
        var pos = ImGui.GetCursorScreenPos() + new Vector2(0, (lineHeight - size.Y) / 2);

        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + size, ImGui.GetColorU32(bg), size.Y / 2);
        dl.AddText(pos + PillPadding, ImGui.GetColorU32(fg), text);
        ImGui.Dummy(new Vector2(size.X, lineHeight));
    }

    // ── Cards ────────────────────────────────────────────────────────────────

    private static Vector2 CardPadding => new Vector2(10f, 7f) * ImGuiHelpers.GlobalScale;

    /// Left and right edges (screen space) of the content area of the card being drawn, for <see cref="AlignRight"/>.
    private static float cardContentLeft, cardContentRight;

    /// A rounded card the full width of the window around <paramref name="body"/>. Its background is drawn behind the
    /// content afterwards (draw-list channels), so the card fits whatever the body drew. Cards must not be nested.
    /// Returns true when the card itself (not a widget in it) was clicked.
    public static bool Card(string id, CardStyle style, Action body)
    {
        var dl = ImGui.GetWindowDrawList();
        var pad = CardPadding;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        cardContentLeft = start.X + pad.X;
        cardContentRight = start.X + width - pad.X;

        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        using (ImRaii.PushId(id))
        {
            ImGui.SetCursorScreenPos(start + pad);
            ImGui.BeginGroup();
            body();
            ImGui.EndGroup();
        }
        var end = new Vector2(start.X + width, ImGui.GetItemRectMax().Y + pad.Y);

        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) && ImGui.IsMouseHoveringRect(start, end);
        var clicked = hovered && !ImGui.IsAnyItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        var rounding = 6f * ImGuiHelpers.GlobalScale;
        var (bg, border) = style switch
        {
            CardStyle.Selected => (hovered ? CardHover : CardBg, (Vector4?)Accent),
            CardStyle.Banner => (WithAlpha(AccentStrong, 0.18f), (Vector4?)WithAlpha(Accent, 0.8f)),
            CardStyle.MutedBanner => (FrameBg, (Vector4?)Border),
            _ => (hovered ? CardHover : CardBg, null),
        };
        dl.ChannelsSetCurrent(0);
        dl.AddRectFilled(start, end, ImGui.GetColorU32(bg), rounding);
        if (border is { } b) dl.AddRect(start, end, ImGui.GetColorU32(b), rounding, ImDrawFlags.None, 1.5f * ImGuiHelpers.GlobalScale);
        if (style == CardStyle.Selected)
            dl.AddRectFilled(start, new Vector2(start.X + 3f * ImGuiHelpers.GlobalScale, end.Y), ImGui.GetColorU32(Accent), rounding,
                             ImDrawFlags.RoundCornersLeft);
        dl.ChannelsMerge();

        // Claim the card's area so the next item goes below it.
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(end - start);
        return clicked;
    }

    /// Inside a card: continue the line so an item <paramref name="width"/> wide ends at the card's right padding,
    /// or right after the previous item when the card is too narrow. SameLine offsets count from the card's group.
    public static void AlignRight(float width)
    {
        var desired = cardContentRight - width;
        var minimum = ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X;
        ImGui.SameLine(Math.Max(desired, minimum) - cardContentLeft + ImGui.GetScrollX());
    }

    /// Width of an ImGuiComponents.IconButton for <paramref name="icon"/>.
    public static float IconButtonWidth(FontAwesomeIcon icon)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        return ImGui.CalcTextSize(icon.ToIconString()).X + ImGui.GetStyle().FramePadding.X * 2;
    }

    /// Width of an ImGuiComponents.IconButtonWithText.
    public static float IconButtonWithTextWidth(FontAwesomeIcon icon, string text)
    {
        float iconWidth;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            iconWidth = ImGui.CalcTextSize(icon.ToIconString()).X;
        return iconWidth + ImGui.CalcTextSize(text).X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemInnerSpacing.X;
    }

    // ── Misc ─────────────────────────────────────────────────────────────────

    /// "45s", "12m", "3h 05m".
    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds}s";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
        return $"{(int)span.TotalHours}h {span.Minutes:00}m";
    }
}

/// A Cummy window: draws with <see cref="Ui.Theme"/>, pushed before the window begins and popped after it ends.
public abstract class ThemedWindow : Window
{
    private IDisposable? theme;

    protected ThemedWindow(string name, ImGuiWindowFlags flags = ImGuiWindowFlags.None) : base(name, flags) { }

    public override void PreDraw()
    {
        theme?.Dispose();
        theme = Ui.Theme();
    }

    public override void PostDraw()
    {
        theme?.Dispose();
        theme = null;
    }
}
