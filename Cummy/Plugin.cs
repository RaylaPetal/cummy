using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Cummy.Compositor;
using Cummy.Ipc;
using Cummy.Messaging;
using Cummy.Pairing;
using Cummy.State;
using Cummy.Windows;

namespace Cummy;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/cummy";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("Cummy");

    private readonly LayerState layers;
    private readonly ChatSender sender;
    private readonly ChatListener listener;
    private readonly PairingService pairing;
    private readonly PenumbraIpc penumbra;
    private readonly Cummy.Proteus.ProteusIpc proteus;
    private readonly OverlaySources overlaySources;
    private readonly CompositorService compositor;
    private readonly MainWindow mainWindow;
    private readonly SettingsWindow settingsWindow;
    private readonly LayerPickerWindow pickerWindow;
    private readonly InviteWindow inviteWindow;
    private readonly QuickLayerWindow quickLayerWindow;
    private readonly WashOffService washOff;
    private readonly DripService drip;
    private readonly MoodlesIpc moodles;
    private readonly MoodleService moodleService;
    private readonly ContextMenuService contextMenu;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        layers = new LayerState(Configuration);
        drip = new DripService(Configuration, layers);
        sender = new ChatSender();
        listener = new ChatListener();
        pairing = new PairingService(Configuration, layers, sender, listener);
        penumbra = new PenumbraIpc(PluginInterface);
        overlaySources = new OverlaySources(Configuration);
        proteus = new Cummy.Proteus.ProteusIpc(PluginInterface);
        compositor = new CompositorService(penumbra, layers, DataManager, overlaySources, Configuration, proteus);
        moodles = new MoodlesIpc(PluginInterface);
        moodleService = new MoodleService(Configuration, layers, pairing, moodles);

        settingsWindow = new SettingsWindow(Configuration, layers, pairing, overlaySources, compositor, moodles, moodleService, drip);
        pickerWindow = new LayerPickerWindow(pairing);
        mainWindow = new MainWindow(Configuration, layers, pairing, compositor, drip, pickerWindow, ToggleConfigUi);
        inviteWindow = new InviteWindow(pairing);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(settingsWindow);
        WindowSystem.AddWindow(pickerWindow);
        WindowSystem.AddWindow(inviteWindow);
        quickLayerWindow = new QuickLayerWindow(Configuration, pairing, layers);
        WindowSystem.AddWindow(quickLayerWindow);
        washOff = new WashOffService(Configuration, layers);
        contextMenu = new ContextMenuService(pairing, layers, pickerWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Cummy. /cummy settings opens settings; /cummy clean cleans everything; /cummy clean <part> cleans one part (e.g. chest, arm left)."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        CommandManager.RemoveHandler(CommandName);

        contextMenu.Dispose();
        washOff.Dispose();
        drip.Dispose();
        moodleService.Dispose();
        moodles.Dispose();
        WindowSystem.RemoveAllWindows();

        compositor.Dispose();
        proteus.Dispose();
        penumbra.Dispose();
        pairing.Dispose();
        listener.Dispose();
        sender.Dispose();
        layers.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var words = args.Trim();
        if (words.Length == 0)
        {
            ToggleMainUi();
            return;
        }

        var verb = words.Split(' ', 2)[0];
        if (verb.Equals("settings", StringComparison.OrdinalIgnoreCase) || verb.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            ToggleConfigUi();
            return;
        }
        if (!verb.Equals("clean", StringComparison.OrdinalIgnoreCase))
        {
            Notifier.Error($"Unknown command '{verb}'. Try /cummy, /cummy settings or /cummy clean [part].");
            return;
        }

        var target = words.Length > verb.Length ? words[verb.Length..].Trim() : "";
        if (target.Length == 0 || target.Equals("all", StringComparison.OrdinalIgnoreCase)
            || target.Equals("everything", StringComparison.OrdinalIgnoreCase))
        {
            Notifier.Info(layers.CleanAll() ? "All cleaned up." : "You're already clean.");
            return;
        }

        if (!BodyParts.TryParseName(target, out var part))
        {
            var names = string.Join(", ", BodyParts.DisplayOrder.Select(p => p.DisplayName().ToLowerInvariant()));
            Notifier.Error($"Unknown part '{target}'. Parts: {names}.");
            return;
        }
        var partName = part.DisplayName().ToLowerInvariant();
        Notifier.Info(layers.Clean(part) ? $"Cleaned your {partName}." : $"Your {partName} is already clean.");
    }

    public void ToggleConfigUi() => settingsWindow.Toggle();

    public void ToggleMainUi() => mainWindow.Toggle();
}
