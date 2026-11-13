using System;
using System.Linq;
using System.Numerics;
using Cummy.Compositor;
using Cummy.Pairing;
using Cummy.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Cummy.Windows;

/// What the plugin is doing for you right now: your layers (and cleaning them) and your partners.
public sealed class MainWindow : ThemedWindow
{
    private readonly Configuration configuration;
    private readonly LayerState layers;
    private readonly PairingService pairing;
    private readonly CompositorService compositor;
    private readonly DripService drip;
    private readonly LayerPickerWindow picker;

    private readonly CreampieService creampies;

    public MainWindow(Configuration configuration, LayerState layers, PairingService pairing, CompositorService compositor,
                      DripService drip, LayerPickerWindow picker, Action openSettings, CreampieService creampies)
        : base("Cummy###CummyMain")
    {
        this.creampies = creampies;
        this.drip = drip;
        this.configuration = configuration;
        this.layers = layers;
        this.pairing = pairing;
        this.compositor = compositor;
        this.picker = picker;

        Size = new Vector2(360, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(300, 260), MaximumSize = new Vector2(900, 1200) };
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(2, 1),
            Click = _ => openSettings(),
            ShowTooltip = () => ImGui.SetTooltip("Settings"),
        });
    }

    protected override void DrawContents()
    {
        if (layers.Current is null)
        {
            Ui.Muted("Log in to a character to use Cummy.");
            return;
        }

        DrawStatus();
        ImGuiHelpers.ScaledDummy(2f);
        DrawBanner();
        ImGuiHelpers.ScaledDummy(4f);
        DrawLayers();
        DrawCreampies();
        ImGuiHelpers.ScaledDummy(6f);
        DrawPartners();
    }

    /// Your state at a glance: highlighted with Clean everything (or how you'll get clean, while manual cleaning is
    /// off) while layered, muted while clean.
    private void DrawBanner()
    {
        var count = BodyParts.DisplayOrder.Count(layers.IsLayered);
        var dripping = creampies.Active;
        if (count == 0 && dripping.Count == 0)
        {
            Ui.Card("banner", CardStyle.MutedBanner, () =>
            {
                Ui.Icon(FontAwesomeIcon.Check, Ui.MutedText);
                ImGui.SameLine();
                Ui.Muted("Clean... for now.");
            });
            return;
        }

        Ui.Card("banner", CardStyle.Banner, () =>
        {
            // One line per kind the wearer actually has: sticky layers, leaking creampies.
            ImGui.BeginGroup();
            if (count > 0)
                BannerLine(FontAwesomeIcon.Splotch, "Covered in cum", count == 1 ? "1 spot glazed" : $"{count} spots glazed");
            if (dripping.Count > 0)
            {
                var holes = string.Join(" and ", dripping.Select((h, i) => i == 0 ? h.DisplayName() : h.DisplayName().ToLowerInvariant()));
                BannerLine(FontAwesomeIcon.Tint, "Creampied", $"{holes} leaking");
            }
            ImGui.EndGroup();

            // With drying off or washing off on, only those (and a partner's lick) clean you: say how instead.
            if (configuration.HowYouGetClean() is { } how)
            {
                Ui.MutedWrapped(how);
                return;
            }

            var label = "Clean everything";
            Ui.AlignRight(Ui.IconButtonWithTextWidth(FontAwesomeIcon.Broom, label));
            using (Ui.AccentButton())
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Broom, label)
                    && (layers.CleanAll(LayerChange.Of(ChangeCause.Clean)) | creampies.EndAll(CreampieCause.Clean) > 0))
                    Notifier.Info("All wiped clean... for now.");
            }
        });
    }

    private static void BannerLine(FontAwesomeIcon icon, string headline, string detail)
    {
        Ui.Icon(icon, Ui.Accent);
        ImGui.SameLine();
        ImGui.TextColored(Ui.Accent, headline);
        ImGui.SameLine();
        ImGui.TextUnformatted(detail);
    }

    private void DrawStatus()
    {
        var status = compositor.Status;
        var (icon, color) = status switch
        {
            _ when status.StartsWith("Failed", StringComparison.Ordinal) => (FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudRed),
            "No Bibo body" or "Waiting for Penumbra" or "Bibo material has no usable textures"
                or "Can't set up the Cummy mod in Penumbra" => (FontAwesomeIcon.ExclamationTriangle, Ui.Warning),
            "Compositing..." or "Checking your skin..." => (FontAwesomeIcon.Sync, ImGuiColors.DalamudYellow),
            _ when status.StartsWith("Waiting", StringComparison.Ordinal) => (FontAwesomeIcon.Hourglass, Ui.MutedText),
            _ => (FontAwesomeIcon.CheckCircle, ImGuiColors.HealerGreen),
        };
        Ui.Icon(icon, color);
        ImGui.SameLine();
        ImGui.TextColored(color, status);

        if (compositor.BodyViaProteus == true)
        {
            Ui.Icon(FontAwesomeIcon.LayerGroup, Ui.Accent);
            ImGui.SameLine();
            ImGui.TextColored(Ui.Accent, "Body layers via Proteus");
            if (compositor.ProteusStatus is { } packStatus)
            {
                ImGui.SameLine();
                Ui.Muted($"- {packStatus}");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Proteus is loaded, so it layers your body together with its own overlays. "
                               + "Facial layers still come from Cummy. Change this in Settings > General.");
        }
    }

    /// Running creampie drips with the time left, each with Stop while cleaning by hand is allowed.
    private void DrawCreampies()
    {
        var active = creampies.Active;
        if (active.Count == 0) return;

        ImGuiHelpers.ScaledDummy(4f);
        Ui.Heading(FontAwesomeIcon.Water, "Leaking cum");
        foreach (var hole in active)
        {
            Ui.Card($"creampie{hole.Id()}", CardStyle.Normal, () =>
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(hole.DisplayName());
                var left = creampies.Remaining(hole) ?? TimeSpan.Zero;
                var pill = $"{(int)left.TotalMinutes}:{left.Seconds:D2} left";
                var canClean = configuration.ManualCleanAllowed;
                var spacing = ImGui.GetStyle().ItemSpacing.X;
                Ui.AlignRight(Ui.PillWidth(pill) + (canClean ? spacing + Ui.IconButtonWidth(FontAwesomeIcon.Broom) : 0));
                Ui.Pill(pill, PillTone.Accent);
                if (!canClean) return;
                ImGui.SameLine();
                if (ImGuiComponents.IconButton($"stop{hole.Id()}", FontAwesomeIcon.Broom)
                    && creampies.End(hole, CreampieCause.Clean))
                    Notifier.Info($"Scooped the cum out of your {hole.DisplayName().ToLowerInvariant()}.");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Scoop it out (stops the leaking)");
            });
        }
    }

    private void DrawLayers()
    {
        var layered = BodyParts.DisplayOrder.Where(layers.IsLayered).ToList();
        if (layered.Count == 0) return;

        Ui.Heading(FontAwesomeIcon.Splotch, "Glazed in cum");
        var data = layers.Current!;
        foreach (var part in layered)
        {
            Ui.Card($"layer{part.Id()}", CardStyle.Normal, () =>
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(part.DisplayName());

                var pills = data.LayeredAt.TryGetValue(part.Id(), out var at) ? Pills(at) : [];
                if (part.StageCount() > 1)
                    pills = [($"stage {layers.Stage(part)}/{part.StageCount()}", PillTone.Accent), .. pills];
                var spacing = ImGui.GetStyle().ItemSpacing.X;
                var canClean = configuration.ManualCleanAllowed;
                var width = pills.Sum(p => Ui.PillWidth(p.Text) + spacing)
                          + (canClean ? Ui.IconButtonWidth(FontAwesomeIcon.Broom) : -spacing);
                Ui.AlignRight(width);
                for (var i = 0; i < pills.Length; i++)
                {
                    Ui.Pill(pills[i].Text, pills[i].Tone);
                    if (canClean || i < pills.Length - 1) ImGui.SameLine();
                }
                if (canClean)
                {
                    if (ImGuiComponents.IconButton($"clean{part.Id()}", FontAwesomeIcon.Broom))
                        Clean(part);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Wipe the cum off your {part.DisplayName().ToLowerInvariant()}");
                }
            });
        }

        // Where the layers are about to drip (only while dripping is on: the service keeps no countdowns otherwise).
        foreach (var (part, wait) in drip.Pending())
            Ui.Muted($"{part.DisplayName()}: starts dripping in {(wait < TimeSpan.FromMinutes(1) ? "<1m" : Ui.Span(wait))}");
    }

    private void Clean(BodyPart part)
    {
        if (layers.Clean(part, LayerChange.Of(ChangeCause.Clean))) Notifier.Info($"Wiped the cum off your {part.DisplayName().ToLowerInvariant()}.");
    }

    /// "12m ago", plus the time left before it dries when drying off is on (amber in its last 5 minutes).
    private (string Text, PillTone Tone)[] Pills(DateTime layeredAtUtc)
    {
        var ago = ($"{Ui.Span(DateTime.UtcNow - layeredAtUtc)} ago", PillTone.Fresh);
        if (!configuration.DryOffEnabled) return [ago];
        var dryAt = layeredAtUtc + TimeSpan.FromMinutes(Math.Max(Configuration.MinDryMinutes, configuration.DryOffMinutes));
        var left = dryAt - DateTime.UtcNow;
        var dry = left > TimeSpan.Zero
            ? ($"dries in {Ui.Span(left)}", left < TimeSpan.FromMinutes(5) ? PillTone.Drying : PillTone.Neutral)
            : ("drying", PillTone.Drying);
        return [ago, dry];
    }

    private void DrawPartners()
    {
        var peers = pairing.PeersSorted().ToList();
        Ui.Heading("Partners", peers.Count);
        if (peers.Count == 0)
        {
            Ui.MutedWrapped("Nobody yet. Stand near someone, click the droplet button and choose Pair.");
            return;
        }

        var ctrl = ImGui.GetIO().KeyCtrl;
        foreach (var peer in peers)
        {
            Ui.Card($"peer{peer.Key}", CardStyle.Normal, () =>
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(peer.Name);
                ImGui.SameLine();
                Ui.Muted($"@{peer.World}");

                var spacing = ImGui.GetStyle().ItemSpacing.X;
                Ui.AlignRight(Ui.IconButtonWidth(FontAwesomeIcon.Tint) + spacing + Ui.IconButtonWidth(FontAwesomeIcon.GrinTongue)
                              + spacing + Ui.IconButtonWidth(FontAwesomeIcon.UserTimes));
                if (ImGuiComponents.IconButton($"layer{peer.Key}", FontAwesomeIcon.Tint))
                    picker.Open(peer);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Layer {peer.Name}...");
                ImGui.SameLine();
                var close = Proximity.Within(peer, Proximity.LickRange);
                using (ImRaii.Disabled(!close))
                {
                    if (ImGuiComponents.IconButton($"lick{peer.Key}", FontAwesomeIcon.GrinTongue))
                        picker.OpenLick(peer);
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(close ? $"Lick {peer.Name}..." : $"{peer.Name} is too far away to lick.");
                ImGui.SameLine();
                using (ImRaii.Disabled(!ctrl))
                {
                    if (ImGuiComponents.IconButton($"unpair{peer.Key}", FontAwesomeIcon.UserTimes))
                        pairing.Unpair(peer);
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(ctrl ? $"Unpair {peer.Name}" : $"Hold Ctrl to unpair {peer.Name}");
            });
        }

        Ui.FooterHint("Droplet to layer - tongue to lick - hold Ctrl to unpair", pinToBottom: true);
    }
}
