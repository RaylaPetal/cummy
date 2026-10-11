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

    public MainWindow(Configuration configuration, LayerState layers, PairingService pairing, CompositorService compositor,
                      DripService drip, LayerPickerWindow picker, Action openSettings)
        : base("Cummy###CummyMain")
    {
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
        ImGuiHelpers.ScaledDummy(6f);
        DrawPartners();
    }

    /// Your state at a glance: highlighted with Clean everything while layered, muted while clean.
    private void DrawBanner()
    {
        var count = BodyParts.DisplayOrder.Count(layers.IsLayered);
        if (count == 0)
        {
            Ui.Card("banner", CardStyle.MutedBanner, () =>
            {
                Ui.Icon(FontAwesomeIcon.Check, Ui.MutedText);
                ImGui.SameLine();
                Ui.Muted("You're clean.");
            });
            return;
        }

        Ui.Card("banner", CardStyle.Banner, () =>
        {
            ImGui.BeginGroup();
            Ui.Icon(FontAwesomeIcon.Tint, Ui.Accent);
            ImGui.SameLine();
            ImGui.TextColored(Ui.Accent, "LAYERED");
            ImGui.TextUnformatted(count == 1 ? "1 part" : $"{count} parts");
            ImGui.EndGroup();

            var label = "Clean everything";
            Ui.AlignRight(Ui.IconButtonWithTextWidth(FontAwesomeIcon.Broom, label));
            using (Ui.AccentButton())
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Broom, label) && layers.CleanAll())
                    Notifier.Info("All cleaned up.");
            }
        });
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

    private void DrawLayers()
    {
        var layered = BodyParts.DisplayOrder.Where(layers.IsLayered).ToList();
        if (layered.Count == 0) return;

        Ui.Heading("Your layers");
        var data = layers.Current!;
        var grouped = layered.Any(p => p.IsFacial()) && layered.Any(p => !p.IsFacial());
        bool? group = null;
        foreach (var part in layered)
        {
            if (grouped && group != part.IsFacial())
            {
                group = part.IsFacial();
                Ui.Muted(part.IsFacial() ? "Face" : "Body");
            }
            Ui.Card($"layer{part.Id()}", CardStyle.Normal, () =>
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(part.DisplayName());

                var pills = data.LayeredAt.TryGetValue(part.Id(), out var at) ? Pills(at) : [];
                if (part.StageCount() > 1)
                    pills = [($"stage {layers.Stage(part)}/{part.StageCount()}", PillTone.Accent), .. pills];
                var spacing = ImGui.GetStyle().ItemSpacing.X;
                var width = Ui.IconButtonWidth(FontAwesomeIcon.Broom) + pills.Sum(p => Ui.PillWidth(p.Text) + spacing);
                Ui.AlignRight(width);
                foreach (var (text, tone) in pills)
                {
                    Ui.Pill(text, tone);
                    ImGui.SameLine();
                }
                if (ImGuiComponents.IconButton($"clean{part.Id()}", FontAwesomeIcon.Broom))
                    Clean(part);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Clean your {part.DisplayName().ToLowerInvariant()}");
            });
        }

        // Where the layers are about to drip (only while dripping is on: the service keeps no countdowns otherwise).
        foreach (var (part, wait) in drip.Pending())
            Ui.Muted($"{part.DisplayName()}: drip in {(wait < TimeSpan.FromMinutes(1) ? "<1m" : Ui.Span(wait))}");
    }

    private void Clean(BodyPart part)
    {
        if (layers.Clean(part)) Notifier.Info($"Cleaned your {part.DisplayName().ToLowerInvariant()}.");
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
            Ui.Muted("Nobody yet. Right-click a player and choose Pair.");
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
                Ui.AlignRight(Ui.IconButtonWidth(FontAwesomeIcon.Tint) + spacing + Ui.IconButtonWidth(FontAwesomeIcon.UserTimes));
                if (ImGuiComponents.IconButton($"layer{peer.Key}", FontAwesomeIcon.Tint))
                    picker.Open(peer);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Layer {peer.Name}...");
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

        Ui.FooterHint("Droplet to layer - hold Ctrl to unpair", pinToBottom: true);
    }
}
