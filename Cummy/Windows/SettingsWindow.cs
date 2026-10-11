using System;
using System.IO;
using System.Numerics;
using Cummy.Compositor;
using Cummy.Ipc;
using Cummy.Pairing;
using Cummy.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Cummy.Windows;

/// Preferences: drying off, blocked parts, custom overlay art.
public sealed class SettingsWindow : ThemedWindow
{
    private const string ImageFilter = "Images{.png,.jpg,.jpeg,.tga,.bmp}";

    private readonly Configuration configuration;
    private readonly LayerState layers;
    private readonly PairingService pairing;
    private readonly OverlaySources overlaySources;
    private readonly CompositorService compositor;
    private readonly MoodlesIpc moodles;
    private readonly MoodleService moodleService;
    private readonly DripService drip;
    private readonly FileDialogManager fileDialog = new();
    private string? lastImageDir;
    /// The look being edited: shown while a control is dragged, saved and applied only when it is released.
    private LayerLook? lookDraft;

    public SettingsWindow(Configuration configuration, LayerState layers, PairingService pairing, OverlaySources overlaySources,
                          CompositorService compositor, MoodlesIpc moodles, MoodleService moodleService, DripService drip)
        : base("Cummy Settings###CummySettings")
    {
        this.drip = drip;
        this.compositor = compositor;
        this.moodles = moodles;
        this.moodleService = moodleService;
        this.configuration = configuration;
        this.layers = layers;
        this.pairing = pairing;
        this.overlaySources = overlaySources;

        Size = new Vector2(440, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(380, 300), MaximumSize = new Vector2(1000, 1400) };
    }

    protected override void DrawContents()
    {
        fileDialog.Draw();

        using var tabs = ImRaii.TabBar("##settingsTabs");
        if (!tabs) return;

        using (var tab = ImRaii.TabItem("General"))
            if (tab) DrawGeneral();
        using (var tab = ImRaii.TabItem("Blocked parts"))
            if (tab) DrawBlocked();
        using (var tab = ImRaii.TabItem("Custom overlays"))
            if (tab) DrawCustomOverlays();
#if DEBUG
        using (var tab = ImRaii.TabItem("Debug"))
            if (tab) DrawDebug();
#endif
    }

#if DEBUG
    // ── Debug (dev builds only) ──────────────────────────────────────────────

    /// Layer yourself as if a paired partner had sent the tell: same handling, blocks, notices and compositing;
    /// only the chat transport is skipped.
    private void DrawDebug()
    {
        ImGuiHelpers.ScaledDummy(4f);
        if (layers.Current is null)
        {
            Ui.Muted("Log in to a character to use this.");
            return;
        }
        Ui.MutedWrapped("Layer yourself as if a paired partner had sent the tell. Dev builds only.");
        foreach (var facial in new[] { false, true })
        {
            ImGuiHelpers.ScaledDummy(4f);
            Ui.Heading(facial ? "Face" : "Body");
            using var table = ImRaii.Table($"##debug{facial}", 2, ImGuiTableFlags.SizingStretchSame);
            if (!table) continue;
            foreach (var part in BodyParts.DisplayOrder)
            {
                if (part.IsFacial() != facial) continue;
                ImGui.TableNextColumn();
                var stage = layers.Stage(part);
                var shown = stage == 0 ? "" : part.StageCount() > 1 ? $" {stage}/{part.StageCount()}" : " *";
                if (ImGui.Button($"{part.DisplayName()}{shown}##debug{part.Id()}", new Vector2(-1, 0)))
                    pairing.SimulateLayerFromPartner(part);
                if (stage > 0 && ImGui.IsItemHovered())
                    ImGui.SetTooltip(stage >= part.StageCount() ? "Fully covered" : $"Stage {stage} of {part.StageCount()}: another layer advances it");
            }
        }
        ImGuiHelpers.ScaledDummy(4f);
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Broom, "Clean everything"))
            layers.CleanAll();
        ImGui.SameLine();
        using (ImRaii.Disabled(!configuration.DripEnabled))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Tint, "Drip now"))
                drip.DripNow();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(configuration.DripEnabled ? "Every pending drip lands now." : "Turn dripping on in General first.");
    }
#endif

    // ── General ──────────────────────────────────────────────────────────────

    private void DrawGeneral()
    {
        ImGuiHelpers.ScaledDummy(4f);
        Ui.Heading("Compositing");
        var own = configuration.UseOwnCompositorWithProteus;
        if (ImGui.Checkbox("Always use Cummy's own compositor", ref own))
        {
            configuration.UseOwnCompositorWithProteus = own;
            configuration.Save();
            compositor.Schedule();
        }
        ImGuiComponents.HelpMarker("When the Proteus plugin is loaded, Cummy normally hands your body layers to it as an "
                                 + "overlay pack (the \"Cummy Layers\" mod), so the two never fight over your skin. Tick "
                                 + "this to keep Cummy's own look instead; then keep Proteus below Cummy in Penumbra.");
        ImGuiHelpers.ScaledDummy(8f);

        DrawLook();
        ImGuiHelpers.ScaledDummy(8f);

        Ui.Heading("Drying off");

        var dryOff = configuration.DryOffEnabled;
        if (ImGui.Checkbox("Dry off over time", ref dryOff))
        {
            configuration.DryOffEnabled = dryOff;
            if (dryOff) layers.ResetDryTimers();
            configuration.Save();
        }
        ImGuiComponents.HelpMarker("Layered parts clean themselves this long after being layered.");

        using (ImRaii.Disabled(!configuration.DryOffEnabled))
        {
            var minutes = configuration.DryOffMinutes;
            ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderInt("##dryMinutes", ref minutes, Configuration.MinDryMinutes, 240, "%d minutes"))
            {
                configuration.DryOffMinutes = Math.Max(Configuration.MinDryMinutes, minutes);
                configuration.Save();
            }
        }
        ImGuiHelpers.ScaledDummy(8f);

        Ui.Heading("Dripping");
        var dripping = configuration.DripEnabled;
        if (ImGui.Checkbox("Drip onto the parts below", ref dripping))
        {
            configuration.DripEnabled = dripping;
            configuration.Save();
        }
        ImGuiComponents.HelpMarker("Layered parts drip one stage onto the parts below them every interval (chest onto stomach "
                                 + "and arms, stomach onto groin and legs, legs onto feet, mouth onto chest...), never past "
                                 + "the stage above. Blocked parts are skipped and stop the drip. Only while you're logged in.");

        using (ImRaii.Disabled(!configuration.DripEnabled))
        {
            var dripMinutes = Math.Clamp(configuration.DripMinutes, Configuration.MinDripMinutes, Configuration.MaxDripMinutes);
            ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderInt("##dripMinutes", ref dripMinutes, Configuration.MinDripMinutes, Configuration.MaxDripMinutes, "every %d minutes"))
            {
                configuration.DripMinutes = Math.Clamp(dripMinutes, Configuration.MinDripMinutes, Configuration.MaxDripMinutes);
                configuration.Save();
            }
        }
        if (configuration.DripEnabled && configuration.DryOffEnabled
            && configuration.DripInterval >= TimeSpan.FromMinutes(Math.Max(Configuration.MinDryMinutes, configuration.DryOffMinutes)))
        {
            Ui.Icon(FontAwesomeIcon.ExclamationTriangle, Ui.Warning);
            ImGui.SameLine();
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Ui.Warning, "Layers dry off before they drip far: make the drip interval shorter than the drying time.");
            ImGui.PopTextWrapPos();
        }
        ImGuiHelpers.ScaledDummy(8f);

        Ui.Heading("Washing off");
        var water = configuration.WashInWater;
        if (ImGui.Checkbox("Clean when swimming or diving", ref water))
        {
            configuration.WashInWater = water;
            configuration.Save();
        }
        ImGuiComponents.HelpMarker("Everything is cleaned when you start swimming or diving. Wading in shallow water doesn't count.");
        var rain = configuration.WashInRain;
        if (ImGui.Checkbox("Clean in the rain", ref rain))
        {
            configuration.WashInRain = rain;
            configuration.Save();
        }
        ImGuiComponents.HelpMarker("Everything is cleaned after this long outdoors in rain, showers or thunderstorms. "
                                 + "Indoors and duties don't count; the time pauses between showers and resets after "
                                 + "10 minutes without rain.");
        using (ImRaii.Disabled(!configuration.WashInRain))
        {
            var rainMinutes = Math.Clamp(configuration.RainWashMinutes, 1, 10);
            ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderInt("##rainMinutes", ref rainMinutes, 1, 10, rainMinutes == 1 ? "%d minute of rain" : "%d minutes of rain"))
            {
                configuration.RainWashMinutes = Math.Clamp(rainMinutes, 1, 10);
                configuration.Save();
            }
        }
        ImGuiHelpers.ScaledDummy(8f);

        DrawMoodles();
        ImGuiHelpers.ScaledDummy(8f);

        Ui.Heading("Quick-layer button");
        var show = configuration.ShowQuickLayerButton;
        if (ImGui.Checkbox("Show quick-layer button", ref show))
        {
            configuration.ShowQuickLayerButton = show;
            configuration.Save();
        }
        ImGuiComponents.HelpMarker("A small droplet button, shown while a paired partner is nearby: click it to pick a "
                                 + "partner (your target, if paired) and layer them. Drag it to move it.");
        var locked = configuration.LockQuickLayerButton;
        if (ImGui.Checkbox("Lock position", ref locked))
        {
            configuration.LockQuickLayerButton = locked;
            configuration.Save();
        }
    }

    /// The moodle put on you when a partner's layer lands (its VFX is the one set on it in Moodles).
    private void DrawMoodles()
    {
        Ui.Heading("Moodles");
        var available = moodles.IsAvailable;
        if (!available)
            Ui.MutedWrapped("Moodles isn't loaded. Install or enable it to have a moodle applied when you're layered.");

        using (ImRaii.Disabled(!available))
        {
            var apply = configuration.ApplyMoodle;
            if (ImGui.Checkbox("Apply a moodle when I'm layered", ref apply))
            {
                configuration.ApplyMoodle = apply;
                configuration.Save();
            }
            ImGuiComponents.HelpMarker("When a partner's layer lands, this moodle is put on you, with the VFX, duration "
                                     + "and stacking you set on it in Moodles. It comes off once you're fully clean.");

            using (ImRaii.Disabled(!configuration.ApplyMoodle))
                DrawMoodlePicker();
        }

        if (!available) return;
        if (configuration.MoodleId != Guid.Empty && !moodles.Exists(configuration.MoodleId))
            Ui.Error($"\"{MoodleName()}\" no longer exists in Moodles. Choose another moodle.");
        if (moodleService.LastApplyIgnored)
            Ui.Error("Moodles ignored the last moodle. Turn on \"Allow remote apply\" in Moodles' settings.");
        else
            Ui.MutedWrapped("Moodles' \"Allow remote apply\" setting must be on.");
    }

    private void DrawMoodlePicker()
    {
        var iconSize = new Vector2(ImGui.GetTextLineHeight() * 0.75f, ImGui.GetTextLineHeight());
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        using var combo = ImRaii.Combo("Moodle##moodlePick", configuration.MoodleId == Guid.Empty ? "Choose a moodle" : MoodleName());
        if (!combo) return;

        var list = moodles.Moodles(reload: ImGui.IsWindowAppearing());
        if (list.Count == 0) Ui.Muted("You have no saved moodles.");
        foreach (var m in list)
        {
            if (m.IconId != 0)
            {
                var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(m.IconId)).GetWrapOrEmpty();
                ImGui.Image(icon.Handle, iconSize);
                ImGui.SameLine();
            }
            if (ImGui.Selectable($"{(m.Title.Length > 0 ? m.Title : m.Path)}##{m.Id}", m.Id == configuration.MoodleId))
            {
                configuration.MoodleId = m.Id;
                configuration.MoodleTitle = m.Title;
                configuration.Save();
            }
            if (m.Path.Length > 0 && m.Path != m.Title && ImGui.IsItemHovered())
                ImGui.SetTooltip(m.Path);
        }
    }

    /// The chosen moodle's title, live from Moodles when it can be, else the one saved when it was chosen.
    private string MoodleName()
    {
        foreach (var m in moodles.Moodles())
            if (m.Id == configuration.MoodleId)
                return m.Title.Length > 0 ? m.Title : m.Path;
        return configuration.MoodleTitle.Length > 0 ? configuration.MoodleTitle : "Unknown moodle";
    }

    /// How layers look on your body. Every change recomposites your skin, so it applies when a control is released.
    private void DrawLook()
    {
        Ui.Heading("Look");
        var look = lookDraft ??= (configuration.Look ?? new LayerLook()).Clamped();
        var width = 220 * ImGuiHelpers.GlobalScale;
        var commit = false;

        var (r, g, b) = look.TintRgb();
        var colour = new Vector3(r, g, b) / 255f;
        ImGui.SetNextItemWidth(width);
        if (ImGui.ColorEdit3("Colour##lookTint", ref colour, ImGuiColorEditFlags.NoAlpha))
        {
            var c = Vector3.Clamp(colour, Vector3.Zero, Vector3.One) * 255f + new Vector3(0.5f);
            look.Tint = $"#{(int)c.X:X2}{(int)c.Y:X2}{(int)c.Z:X2}";
        }
        commit |= ImGui.IsItemDeactivatedAfterEdit();
        ImGuiComponents.HelpMarker("Multiplied into your layers' colour, so it shows best on light art; dark or strongly "
                                 + "coloured art barely changes. White leaves them as they are.");

        int Slider(string label, int value, int min, int max, string format)
        {
            ImGui.SetNextItemWidth(width);
            ImGui.SliderInt(label, ref value, min, max, format);
            commit |= ImGui.IsItemDeactivatedAfterEdit();
            return Math.Clamp(value, min, max);
        }
        look.Opacity = Slider("Opacity##lookOpacity", look.Opacity, -100, 100, "%d");
        ImGuiComponents.HelpMarker("Below 0 fades your layers; above 0 makes their thin, wet areas solid without "
                                 + "spreading them onto bare skin.");
        look.Shine = Slider("Shine##lookShine", look.Shine, 0, 100, "%d%%");
        look.SkinTone = Slider("Skin tone shows through##lookSkin", look.SkinTone, 0, 100, "%d%%");
        ImGuiComponents.HelpMarker("At 0% layers keep their own colour on any skin tone; higher lets your skin tone tint "
                                 + "them. With Proteus, its own skin-tone setting also applies.");

        var reset = false;
        using (ImRaii.Disabled(look.IsDefault))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Undo, "Reset look"))
            {
                lookDraft = look = new LayerLook();
                reset = true;
            }
        }

        if (commit || reset)
        {
            var applied = look.Clamped();
            if (applied.Key != (configuration.Look ?? new LayerLook()).Clamped().Key)
            {
                configuration.Look = applied;
                configuration.Save();
                compositor.Schedule();
            }
        }
    }

    // ── Blocked parts ────────────────────────────────────────────────────────

    private void DrawBlocked()
    {
        ImGuiHelpers.ScaledDummy(4f);
        if (layers.Current is not { } me)
        {
            Ui.Muted("Log in to a character to change its blocked parts.");
            return;
        }

        Ui.MutedWrapped("Partners can't layer parts you block. They're told when this changes.");
        ImGuiHelpers.ScaledDummy(4f);

        foreach (var facial in new[] { false, true })
        {
            ImGuiHelpers.ScaledDummy(2f);
            Ui.Muted(facial ? "Face" : "Body");
            using var table = ImRaii.Table($"##blocked{facial}", 2, ImGuiTableFlags.SizingStretchSame);
            if (!table) continue;
            foreach (var part in BodyParts.DisplayOrder)
            {
                if (part.IsFacial() != facial) continue;
                ImGui.TableNextColumn();
                var blocked = part.IsBlockedBy(me.BlockMask);
                if (ImGui.Checkbox($"{part.DisplayName()}##block{part.Id()}", ref blocked))
                {
                    me.BlockMask = blocked ? me.BlockMask | part.Bit() : me.BlockMask & ~part.Bit();
                    configuration.Save();
                    pairing.BroadcastBlocks(part);
                }
            }
        }
    }

    // ── Custom overlays ──────────────────────────────────────────────────────

    private void DrawCustomOverlays()
    {
        ImGuiHelpers.ScaledDummy(4f);
        Ui.MutedWrapped("Use your own art for your layers, transparent everywhere except the art. Everyone sees your "
                      + "art on you; it isn't sent to partners.");
        ImGuiHelpers.ScaledDummy(6f);

        Ui.Heading("Full images");
        Ui.MutedWrapped("One image each, split into the parts for you. Body art uses the Bibo UV layout; face art "
                      + "uses the face-paint layout and is mirrored onto both sides of your face.");
        ImGuiHelpers.ScaledDummy(2f);
        using (var table = ImRaii.Table("##fullImages", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX))
        {
            if (table)
            {
                SetupSourceColumns();
                DrawSourceRow(OverlayTarget.FullBody);
                DrawSourceRow(OverlayTarget.FullFace);
            }
        }

        ImGuiHelpers.ScaledDummy(8f);
        Ui.Heading("Per-part overrides");
        Ui.MutedWrapped("Replace a single part's art, whether shipped or from a full image.");
        foreach (var facial in new[] { false, true })
        {
            ImGuiHelpers.ScaledDummy(4f);
            Ui.Muted(facial ? "Face (face-paint layout)" : "Body (Bibo UV layout)");
            using var table = ImRaii.Table($"##overrides{facial}", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX);
            if (!table) continue;
            SetupSourceColumns();
            foreach (var part in BodyParts.DisplayOrder)
                if (part.IsFacial() == facial)
                    DrawSourceRow(OverlayTarget.Of(part));
        }
    }

    private static void SetupSourceColumns()
    {
        ImGui.TableSetupColumn("Part", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Using", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed);
    }

    /// One art source: a full image or a part's override.
    private void DrawSourceRow(OverlayTarget target)
    {
        var id = target.Key;
        var state = overlaySources.State(target);
        var busy = overlaySources.IsBusy(target);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(target.DisplayName);

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        DrawSourceStatus(target, state, busy);

        ImGui.TableNextColumn();
        using (ImRaii.Disabled(busy))
        {
            if (ImGuiComponents.IconButton($"img{id}", FontAwesomeIcon.FileImage))
                Pick(target, normal: false);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Choose the image (PNG, JPEG, TGA or BMP with transparency)");

            ImGui.SameLine();
            using (ImRaii.Disabled(state != CustomState.Custom))
            {
                if (ImGuiComponents.IconButton($"nrm{id}", FontAwesomeIcon.Mountain))
                    Pick(target, normal: true);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(state == CustomState.Custom
                    ? "Optional: choose a normal map for relief. Without one the art adds no relief."
                    : "Choose an image first; then you can add its normal map.");

            ImGui.SameLine();
            using (ImRaii.Disabled(state == CustomState.None))
            {
                if (ImGuiComponents.IconButton($"rst{id}", FontAwesomeIcon.Undo))
                    overlaySources.Reset(target);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && state != CustomState.None)
                ImGui.SetTooltip("Remove this custom art and its normal map");
        }

        if (overlaySources.Error(target) is { } error)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            Ui.Error(error);
        }
    }

    private void DrawSourceStatus(OverlayTarget target, CustomState state, bool busy)
    {
        if (busy)
        {
            Ui.Muted("Importing...");
            return;
        }
        var fallback = "Shipped art";
        if (target.Part is { } part && overlaySources.State(OverlayTarget.FullFor(part)) == CustomState.Custom)
            fallback = part.IsFacial() ? "Full-face image" : "Full-body image";
        switch (state)
        {
            case CustomState.Custom:
                ImGui.TextColored(Ui.Accent, overlaySources.Record(target)?.OriginalName ?? "Custom");
                if (overlaySources.HasNormal(target))
                {
                    ImGui.SameLine();
                    Ui.Muted("+ normal");
                }
                break;
            case CustomState.Missing:
                Ui.Error(target.Part == null ? "Image missing - choose it again or reset" : $"Override missing - using {fallback.ToLowerInvariant()}");
                break;
            default:
                Ui.Muted(fallback);
                break;
        }
    }

    private void Pick(OverlayTarget target, bool normal)
    {
        var title = $"{target.DisplayName} {(normal ? "normal map" : "image")}";
        var start = lastImageDir ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        fileDialog.OpenFileDialog(title, ImageFilter, (ok, paths) =>
        {
            if (!ok || paths.Count == 0) return;
            lastImageDir = Path.GetDirectoryName(paths[0]);
            overlaySources.Import(target, normal, paths[0]);
        }, 1, start, true);
    }
}
