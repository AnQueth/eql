using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EqLog.Core;
using EqLog.Application.Sessions;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using EqLog.Core.Triggers;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.ViewModels;
using Microsoft.Win32;

namespace EqLog.Overlay;

public sealed class DurationRow
{
    public bool Tracked { get; set; } = true;

    public required string Spell
    {
        get; init;
    }

    public required string Learned
    {
        get; init;
    }

    public required int Samples
    {
        get; init;
    }

    public bool HasSamples
    {
        get
        {
            return Samples > 0;
        }
    }

    public string Override { get; set; } = "";

    public string Wiki { get; init; } = "-";
}

public sealed class SpawnRow
{
    public string Wiki { get; init; } = "-";

    public required string Zone
    {
        get; init;
    }

    public required string Mob
    {
        get; init;
    }

    public required int Kills
    {
        get; init;
    }

    public required string LastKilled
    {
        get; init;
    }

    public required string Learned
    {
        get; init;
    }

    public required int Samples
    {
        get; init;
    }

    public bool HasSamples
    {
        get
        {
            return Samples > 0;
        }
    }

    public string Override { get; set; } = "";

    public bool Tracked
    {
        get; set;
    }
}

public partial class MainWindow : Window
{
    private const int LockHotkeyId = 0x4C31;

    private DateTime _alertTestVisibleUntil;
    private readonly AppSettings _settings;
    private readonly EqLog.Application.Settings.SettingsService _settingsPersistence;
    private TrackingService _tracking;
    private ApplicationPersistenceService _persistence = null!;
    private readonly Dictionary<string, OverlayWindow> _overlays = [];
    private readonly SettingsPanelViewModel _settingsPanel = new();
    private readonly Dictionary<string, OverlaySettingViewModel> _overlaySettings = [];
    private DockWindow _dock = null!;
    private string? _activeDockWorkspaceId;
    private readonly HashSet<ChatChannel> _chatChannels;
    private readonly ObservableCollection<DurationRow> _durationRows = [];
    private readonly ObservableCollection<SpawnRow> _spawnRows = [];
    private string _spawnsSignature = "";
    private readonly HousekeepingService _housekeeping = new();
    private readonly CancellationTokenSource _housekeepingStop = new();
    private Task _housekeepingTask = Task.CompletedTask;
    private bool _housekeepingFailureReported;
    private readonly DispatcherTimer _gameTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _displayScaleSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _displayScaleReady;
    private bool _gameVisible = true;
    private readonly ObservableCollection<AlertTrigger> _triggers;

    private string? _characterName;

    private const int RawBufferSize = 5000;
    private const int RawDisplayMax = 2000;
    private readonly Queue<string> _rawLines = new();
    private readonly object _rawGate = new();
    private long _rawVersion;
    private long _rawShownVersion = -1;

    private GameSession _session;
    private LogSessionService _logSessions = null!;
    private string? _currentFile;
    private string _durationsSignature = "";

    private bool _dataReady;
    private LogFileInfo? _startupLogPreview;

    public MainWindow(StartupState state)
        : this(state, null)
    {
    }

    internal MainWindow(StartupState state, Task<StartupState>? pendingState, Task<StartupState>? availableData = null)
    {
        _dataReady = pendingState is null;
        _settings = state.Settings;
        _settingsPersistence = state.SettingsPersistence ?? SettingsComposition.CreateService();
        _tracking = state.Tracking ?? TrackingComposition.CreateService();
        _session = state.Session;
        InitializeComponent();
        InitializeDisplayScale();
        InitializeThemes(state);
        InitializeStartupData(state);
        _chatChannels = [.. _settings.ChatChannels];
        DurationsGrid.ItemsSource = _durationRows;
        SpawnsGrid.ItemsSource = _spawnRows;
        _triggers = [.. _settings.Triggers];
        _triggers.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(RebuildTriggers, DispatcherPriority.Background);
        TriggersGrid.ItemsSource = _triggers;
        RebuildTriggers();

        FollowNewestBox.IsChecked = _settings.FollowNewest;
        LockBox.IsChecked = _settings.Locked;
        GameVisibleBox.IsChecked = _settings.OnlyWhenGameVisible;
        HoverChromeBox.IsChecked = _settings.HoverOnlyOverlayChrome;
        UsageTelemetryBox.IsChecked = _settings.ShareUsageTelemetry;
        _gameVisible = GameWindowWatcher.IsGameVisible();
        ResetOnLoginBox.IsChecked = _settings.ResetOnLogin;

        _overlayServices = new OverlayShellServices(this, state);
        OverlaysGroup.DataContext = _settingsPanel;
        ChatChannelsGroup.DataContext = _settingsPanel;
        CreateOverlays();
        CreateMessagePump();
        TrackUsageState();
        UpdateLockState();
        CreateChatChannelOptions();

        System.Windows.Application.Current.SessionEnding += (_, _) => SaveAllBlocking();
        _gameTimer.Tick += (_, _) => CheckGameVisible();

        // This window is never shown: its UI lives in the dock window's side panel, and the dock window is the
        // app's main window. A hidden handle is still needed for the Ctrl+Shift+L hotkey.
        Content = null;
        _dock.SetSidePanel(SettingsRoot);
        _dock.Closing += Dock_Closing;
        _dock.Closed += (_, _) =>
        {
            Close();
            System.Windows.Application.Current.Shutdown();
        };
        new WindowInteropHelper(this).EnsureHandle();
        System.Windows.Application.Current.MainWindow = _dock;
        _dock.Show();
        if (_dock.WindowState == WindowState.Normal)
        {
            EnsureOnScreen(_dock);
        }

        if (pendingState is not null)
        {
            SetDataControlsEnabled(false);
            StatusText.Text = "Saved history is being restored in the background. Workspace layout is ready.";
            HistoryProgress.Tag = "Loading saved data...";
            HistoryProgress.IsIndeterminate = true;
            _dock.SetLoadingNotice(HistoryProgress);
            _pump.Start();
            StartupCompletion = CompleteStartupAsync(pendingState, availableData);
        }
        else
        {
            StartupCompletion = Task.CompletedTask;
            StartRuntime();
        }
    }

    internal Task StartupCompletion { get; }

    private void InitializeDisplayScale()
    {
        double saved = _settings.DisplayScale;
        double scale = double.IsFinite(saved)
            ? Math.Clamp(saved, DisplayScaleSlider.Minimum, DisplayScaleSlider.Maximum)
            : 1;
        _settings.DisplayScale = scale;
        DisplayScaleSlider.Value = scale;
        ApplyDisplayScale(scale);
        if (saved != scale)
        {
            DisplayScaleStatus.Text = $"Saved display size was outside the supported range; using {scale:P0}.";
        }
        _displayScaleSaveTimer.Tick += (_, _) =>
        {
            _displayScaleSaveTimer.Stop();
            SaveSettingsInBackground(error => DisplayScaleStatus.Text = $"Could not save display size: {error}");
        };
        _displayScaleReady = true;
    }

    private static void ApplyDisplayScale(double scale)
    {
        System.Windows.Application.Current.Resources["DisplayScaleTransform"] = new ScaleTransform(scale, scale);
    }

    private void DisplayScale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_displayScaleReady)
        {
            return;
        }
        _settings.DisplayScale = e.NewValue;
        ApplyDisplayScale(e.NewValue);
        DisplayScaleStatus.Text = "";
        _displayScaleSaveTimer.Stop();
        _displayScaleSaveTimer.Start();
    }

    private void ResetDisplayScale_Click(object sender, RoutedEventArgs e)
    {
        DisplayScaleSlider.Value = 1;
    }

    private void StartRuntime()
    {
        // Started directly rather than from Loaded: Show() can raise Loaded synchronously, before a handler is attached.
        _pump.Start();
        _housekeepingTask = _housekeeping.RunAsync(tick => Dispatcher.InvokeAsync(() =>
        {
            if (!_shuttingDown)
            {
                Housekeeping(tick);
            }
        }).Task, _housekeepingStop.Token);
        _gameTimer.Start();
        Dispatcher.BeginInvoke(async () => await OpenInitialLogAsync(), DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(async () => await InitializeUpdatesAsync(), DispatcherPriority.Loaded);
    }

    private void CreateOverlays()
    {
        var work = SystemParameters.WorkArea;
        InitializeMergeOverlay(_settings, work);
        for (int i = 0; i < OverlayPresenters.All.Count; i++)
        {
            var def = OverlayPresenters.All[i];
            if (!_settings.Overlays.TryGetValue(def.Key, out var placement))
            {
                placement = DefaultPlacement(def, i, work);
                _settings.Overlays[def.Key] = placement;
            }
        }

        var activeWorkspace = _settings.DockWindow.EnsureActiveWorkspace(_settings.Overlays);
        foreach (var (key, placement) in _settings.Overlays)
        {
            if (placement.Docked && !_settings.DockWindow.IsPanelInAnyWorkspace(key))
            {
                activeWorkspace.Panels[key] = DockPanelSettings.From(placement);
            }

            placement.Docked = _settings.DockWindow.IsPanelInAnyWorkspace(key);
        }

        _dock = new DockWindow(_settings.DockWindow);
        _activeDockWorkspaceId = _settings.DockWindow.ActiveWorkspaceId;
        _dock.WorkspaceChanged += Dock_WorkspaceChanged;
        foreach (var def in SettingsOverlayDefinitions())
        {
            var p = _settings.Overlays[def.Key];
            p.FontSize ??= _settings.FontSize;
            p.BackgroundOpacity ??= _settings.BackgroundOpacity;
            p.HoverBackgroundOpacity ??= p.BackgroundOpacity;
            p.Locked ??= _settings.Locked;
            var vm = CreateOverlayViewModel(def);
            vm.FontSize = p.FontSize.Value;
            vm.BackgroundOpacity = p.BackgroundOpacity.Value;
            vm.HoverBackgroundOpacity = p.HoverBackgroundOpacity.Value;
            var panel = new OverlayPanel(vm);
            if (vm is MobInfoOverlayViewModel mobInfo)
            {
                MobTimeoutBox.DataContext = mobInfo;
                MobInfoStatusText.DataContext = mobInfo;
            }

            var w = new OverlayWindow(panel);
            w.Left = p.Left;
            w.Top = p.Top;
            w.Width = p.Width;
            w.Height = p.Height;
            w.SetLocked(p.Locked.Value);
            EnsureOnScreen(w);
            _overlays[def.Key] = w;

            var setting = new OverlaySettingViewModel(def.Key, def.Caption, p, _dock.IsPanelInActiveWorkspace(def.Key),
                new OverlaySettingsHost(this));
            _overlaySettings[def.Key] = setting;
            _settingsPanel.Overlays.Add(setting);
            RefreshWorkspaceSelector(def.Key);
            TrackBackgroundOpacity(vm, p, value =>
            {
                setting.BackgroundOpacity = value;
            });

            ApplyMode(def.Key);
        }
    }

    internal static IReadOnlyList<OverlayDefinition> SettingsOverlayDefinitions()
    {
        return OverlayPresenters.All.OrderBy(def => def.Caption, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private void TrackUsageState()
    {
        if (_appliedTheme is { } theme)
        {
            UsageTelemetry.TrackTheme(theme, _settings.Theme is null, _bundledThemes, changed: false);
        }

        foreach (var def in OverlayPresenters.All)
        {
            var placement = _settings.Overlays[def.Key];
            UsageTelemetry.TrackPanelState(def.Key, placement.Visible, placement.Docked, changed: false);
        }
    }

    private void SetOverlayLocked(string key, bool locked)
    {
        _settings.Overlays[key].Locked = locked;
        _overlays[key].SetLocked(locked);
        UpdateLockState();
    }

    private void SetOverlayFontSize(string key, double value)
    {
        _settings.Overlays[key].FontSize = value;
        _overlays[key].Panel.ViewModel.FontSize = value;
        if (key == "merge")
        {
            RefreshMergeView();
        }
    }

    private void SetOverlayBackgroundOpacity(string key, double value)
    {
        _settings.Overlays[key].BackgroundOpacity = value;
        _overlays[key].Panel.ViewModel.BackgroundOpacity = value;
    }

    private void SetOverlayHoverBackgroundOpacity(string key, double value)
    {
        _settings.Overlays[key].HoverBackgroundOpacity = value;
        _overlays[key].Panel.ViewModel.HoverBackgroundOpacity = value;
    }

    /// <summary>Routes the settings panel's overlay rows to the shell.</summary>
    private sealed class OverlaySettingsHost(MainWindow shell) : IOverlaySettingsHost
    {
        public void SetOverlayVisible(string key, bool visible)
        {
            shell.SetOverlayVisible(key, visible);
        }

        public void SetDocked(string key, bool docked)
        {
            shell.SetDocked(key, docked);
        }

        public void SetOverlayLocked(string key, bool locked)
        {
            shell.SetOverlayLocked(key, locked);
        }

        public void MoveOverlayToWorkspace(string key, string? workspaceId)
        {
            shell.MoveOverlayToWorkspace(key, workspaceId);
        }

        public void SetOverlayFontSize(string key, double value)
        {
            shell.SetOverlayFontSize(key, value);
        }

        public void SetOverlayBackgroundOpacity(string key, double value)
        {
            shell.SetOverlayBackgroundOpacity(key, value);
        }

        public void SetOverlayHoverBackgroundOpacity(string key, double value)
        {
            shell.SetOverlayHoverBackgroundOpacity(key, value);
        }
    }

    /// <summary>Puts an overlay's panel in its floating window or in the dock window, per its settings.</summary>
    private void ApplyMode(string key)
    {
        var p = _settings.Overlays[key];
        var w = _overlays[key];
        bool visible = p.Visible && (key != "mobinfo" || MobInfoActive);
        bool alertPreview = key == "alerts" && DateTime.Now < _alertTestVisibleUntil;
        ApplyOverlayPlacement(_dock, w, p, visible, FloatingAllowed, alertPreview);
        w.SetHoverOnlyChrome(_settings.HoverOnlyOverlayChrome && _gameVisible);
    }

    internal static void ApplyOverlayPlacement(DockWindow dock, OverlayWindow window, OverlayPlacement placement,
        bool visible, bool floatingAllowed, bool alertPreview)
    {
        string key = window.Panel.Key;
        bool dockedAnywhere = dock.IsPanelInAnyWorkspace(key);
        bool dockedHere = dock.IsPanelInActiveWorkspace(key);
        placement.Docked = dockedAnywhere;
        if (dockedAnywhere)
        {
            window.Hide();
            window.DetachPanel();
            if (dockedHere && visible)
            {
                if (dock.IsTemporaryPanel(key))
                {
                    dock.RemovePanel(key);
                }

                dock.AddPanel(window.Panel, placement);
            }
            else
            {
                dock.RemovePanel(key);
            }

            return;
        }

        dock.RemovePanel(key);
        window.AttachPanel();
        if (visible && (floatingAllowed || alertPreview))
        {
            window.Show();
            window.SetLocked(placement.Locked == true);
        }
        else
        {
            window.Hide();
        }
    }

    private bool FloatingAllowed
    {
        get
        {
            return !_settings.OnlyWhenGameVisible || _gameVisible;
        }
    }

    private void CheckGameVisible()
    {
        if (_alertTestVisibleUntil != default && DateTime.Now >= _alertTestVisibleUntil)
        {
            _alertTestVisibleUntil = default;
            ApplyMode("alerts");
        }

        bool visible = GameWindowWatcher.IsGameVisible();
        if (visible != _gameVisible)
        {
            _gameVisible = visible;
            ApplyFloating();
        }

        foreach (var window in _overlays.Values)
        {
            window.SetHoverOnlyChrome(_settings.HoverOnlyOverlayChrome && _gameVisible);
        }
    }

    private void HoverChrome_Click(object sender, RoutedEventArgs e)
    {
        _settings.HoverOnlyOverlayChrome = HoverChromeBox.IsChecked == true;
        foreach (var window in _overlays.Values)
        {
            window.SetHoverOnlyChrome(_settings.HoverOnlyOverlayChrome && _gameVisible);
        }
    }

    /// <summary>Re-applies visibility for every floating overlay without changing its placement.</summary>
    private void ApplyFloating()
    {
        foreach (var (key, p) in _settings.Overlays)
        {
            if (!p.Docked && _overlays.ContainsKey(key))
            {
                ApplyMode(key);
            }
        }
    }

    private void GameVisible_Click(object sender, RoutedEventArgs e)
    {
        _settings.OnlyWhenGameVisible = GameVisibleBox.IsChecked == true;
        ApplyFloating();
    }

    private bool IsShowing(string key)
    {
        var p = _settings.Overlays[key];
        return p.Visible && (key != "mobinfo" || MobInfoActive) &&
            (p.Docked || _dock.HasPanel(key) || _overlays[key].IsVisible);
    }

    private void SetDocked(string key, bool docked)
    {
        bool changed = _dock.IsPanelInActiveWorkspace(key) != docked;
        _dock.SetPanelInActiveWorkspace(key, docked, _settings.Overlays[key], append: docked && changed);
        _settings.Overlays[key].Docked = _dock.IsPanelInAnyWorkspace(key);
        _overlaySettings[key].SyncDocked(_dock.IsPanelInActiveWorkspace(key));
        ApplyMode(key);
        RefreshWorkspaceSelector(key);
        if (changed)
        {
            UsageTelemetry.TrackPanelState(key, _settings.Overlays[key].Visible, docked, changed: true);
        }
        if (docked && _settings.Overlays[key].Visible)
        {
            ShowDock();
        }
    }

    private void FloatPanel(string key)
    {
        bool changed = _dock.IsPanelInAnyWorkspace(key);
        _settings.DockWindow.MovePanelToWorkspace(
            key, targetWorkspaceId: null, DockPanelSettings.From(_settings.Overlays[key]));
        _settings.Overlays[key].Docked = _dock.IsPanelInAnyWorkspace(key);
        _overlaySettings[key].SyncDocked(false);
        ApplyMode(key);
        RefreshWorkspaceSelector(key);
        if (changed)
        {
            UsageTelemetry.TrackPanelState(key, _settings.Overlays[key].Visible, docked: false, changed: true);
        }
    }

    private void RefreshWorkspaceSelector(string key)
    {
        if (!_overlaySettings.TryGetValue(key, out var setting))
        {
            return;
        }

        var memberships = _settings.DockWindow.Workspaces
            .Where(workspace => !workspace.Deleted && workspace.Panels.ContainsKey(key))
            .ToArray();
        var choices = new List<DockWorkspaceChoice>();
        DockWorkspaceChoice? selected = null;
        if (memberships.Length == 0)
        {
            selected = new DockWorkspaceChoice(null, "Not docked");
            choices.Add(selected);
        }
        else if (memberships.Length > 1)
        {
            selected = new DockWorkspaceChoice(null, "Multiple workspaces", IsMultiple: true);
            choices.Add(selected);
        }

        foreach (var workspace in _settings.DockWindow.Workspaces.Where(workspace => !workspace.Deleted))
        {
            var choice = new DockWorkspaceChoice(workspace.Id, workspace.Name);
            choices.Add(choice);
            if (memberships.Length == 1 && memberships[0].Id == workspace.Id)
            {
                selected = choice;
            }
        }

        setting.SetWorkspaceChoices(choices, selected);
    }

    private void MoveOverlayToWorkspace(string key, string? targetWorkspaceId)
    {
        bool wasDocked = _dock.IsPanelInAnyWorkspace(key);
        _settings.DockWindow.MovePanelToWorkspace(
            key, targetWorkspaceId, DockPanelSettings.From(_settings.Overlays[key]));
        _settings.Overlays[key].Docked = _dock.IsPanelInAnyWorkspace(key);
        Dock_WorkspaceChanged(this, EventArgs.Empty);
        bool isDocked = _settings.Overlays[key].Docked;
        if (wasDocked != isDocked)
        {
            UsageTelemetry.TrackPanelState(key, _settings.Overlays[key].Visible, isDocked, changed: true);
        }
    }

    private void ShowDock()
    {
        if (_dock.WindowState == WindowState.Minimized)
        {
            _dock.WindowState = WindowState.Normal;
        }

        _dock.Activate();
    }

    private void DockAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var def in OverlayPresenters.All.Where(d => d.Key != "alerts"))
        {
            SetDocked(def.Key, true);
        }

        ShowDock();
    }

    private void FloatAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var def in OverlayPresenters.All)
        {
            FloatPanel(def.Key);
        }
    }

    private void Dock_WorkspaceChanged(object? sender, EventArgs e)
    {
        if (_activeDockWorkspaceId != _settings.DockWindow.ActiveWorkspaceId)
        {
            _dock.ClearPanelsForWorkspaceChange();
            _activeDockWorkspaceId = _settings.DockWindow.ActiveWorkspaceId;
        }

        foreach (var (key, placement) in _settings.Overlays)
        {
            placement.Docked = _dock.IsPanelInAnyWorkspace(key);
            if (_overlaySettings.TryGetValue(key, out var setting))
            {
                setting.SyncDocked(_dock.IsPanelInActiveWorkspace(key));
            }

            if (_overlays.ContainsKey(key))
            {
                ApplyMode(key);
            }
        }

        foreach (string key in _overlaySettings.Keys)
        {
            RefreshWorkspaceSelector(key);
        }

        _dock.RefreshWorkspaceMenus();
        SaveSettingsInBackground(error =>
        {
            MessageBox.Show(_dock, $"Could not save workspaces: {error}", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Error);
        });
    }

    private static OverlayPlacement DefaultPlacement(OverlayDefinition def, int index, Rect work)
    {
        if (def.Key is "npcs" or "wiki" or "inventory" or "merge" or "itemupgrades" or "moteplan" or "exaltations" or "forage" or "tradeskills" or "leveling")
        {
            return new OverlayPlacement
            {
                Left = work.Left + 40,
                Top = work.Top + 40,
                Width = def.Width,
                Height = def.Height,
                Visible = false,
                Docked = true,
                Locked = false,
            };
        }
        if (def.Key == "mobinfo")
        {
            return new OverlayPlacement
            {
                Left = work.Right - def.Width - 20,
                Top = work.Top + 40,
                Width = def.Width,
                Height = def.Height,
                Visible = true,
                Docked = true,
            };
        }

        if (def.Key == "alerts")
        {
            // Alerts float near the top middle of the screen, where they're hard to miss; docking would hide them.
            return new OverlayPlacement
            {
                Left = work.Left + (work.Width - def.Width) / 2,
                Top = work.Top + work.Height * 0.2,
                Width = def.Width,
                Height = def.Height,
                Visible = true,
            };
        }

        const int columns = 4;
        return new OverlayPlacement
        {
            Left = work.Left + 20 + (index % columns) * 340,
            Top = work.Top + 60 + (index / columns) * 280,
            Width = def.Width,
            Height = def.Height,
            Visible = def.Key is "dps" or "taken" or "timers" or "session" or "chat" or "spawns" or "motes" or "quests",
            Docked = true,
        };
    }

    private static void EnsureOnScreen(Window w)
    {
        double left = SystemParameters.VirtualScreenLeft, top = SystemParameters.VirtualScreenTop;
        double right = left + SystemParameters.VirtualScreenWidth, bottom = top + SystemParameters.VirtualScreenHeight;
        if (w.Left > right - 40 || w.Left + w.Width < left + 40 || w.Top > bottom - 30 || w.Top < top)
        {
            w.Left = SystemParameters.WorkArea.Left + 40;
            w.Top = SystemParameters.WorkArea.Top + 40;
        }
    }

    private void CreateChatChannelOptions()
    {
        foreach (var channel in Enum.GetValues<ChatChannel>())
        {
            _settingsPanel.ChatChannels.Add(new ChatChannelOptionViewModel(channel, _chatChannels.Contains(channel), SetChatChannel));
        }
    }

    private void SetChatChannel(ChatChannel channel, bool enabled)
    {
        if (enabled)
        {
            _chatChannels.Add(channel);
        }
        else
        {
            _chatChannels.Remove(channel);
        }

        _settings.ChatChannels = [.. _chatChannels];
    }

    private void SetOverlayVisible(string key, bool visible)
    {
        bool changed = _settings.Overlays[key].Visible != visible;
        if (visible && changed)
        {
            UsageTelemetry.TrackPanelOpened(key);
        }

        _settings.Overlays[key].Visible = visible;
        if (_overlaySettings.TryGetValue(key, out var setting))
        {
            setting.SyncVisible(visible);
        }

        ApplyMode(key);
        if (changed)
        {
            UsageTelemetry.TrackPanelState(key, visible, _settings.Overlays[key].Docked, changed: true);
        }
        if (visible && (_settings.Overlays[key].Docked || _dock.HasPanel(key)))
        {
            ShowDock();
        }
    }

    private async Task OpenInitialLogAsync()
    {
        string[] args = Environment.GetCommandLineArgs();
        string? savedPath = _settings.LogPath;
        string? savedDirectory = _settings.LogDirectory;
        bool followNewest = _settings.FollowNewest;
        var selection = await _logSessions.FindInitialAsync(args, savedPath, savedDirectory, followNewest);
        string? path = selection.Path;

        if (path is not null)
        {
            await SwitchToAsync(path);
            return;
        }

        if (selection.WaitingDirectory is { } waiting)
        {
            ShowWaitingForLogs(waiting);
            return;
        }

        // First run (or the saved folder is gone) and no logs were found in the usual install folders.
        StatusText.Text = "Couldn't find your EverQuest logs automatically. Choose your EverQuest Logs folder.";
        await ChooseLogFolderAsync();
    }

    private void ShowWaitingForLogs(string directory)
    {
        StatusText.Text = $"Watching {directory} - no eqlog files yet. In game, type /log on; the log is picked up automatically.";
        LogPathBox.Text = directory;
    }

    private async Task ChooseLogFolderAsync()
    {
        string? start = _settings.LogDirectory ?? (_currentFile is not null ? Path.GetDirectoryName(_currentFile) : null);
        var dialog = new OpenFolderDialog
        {
            Title = "Choose your EverQuest Logs folder (or the EverQuest install folder)",
            InitialDirectory = start is not null && await _logSessions.DirectoryExistsAsync(start) ? start : null,
        };
        if (dialog.ShowDialog(_dock) != true)
        {
            if (_currentFile is null)
            {
                StatusText.Text = "No log folder chosen. Use \"Choose folder...\" to pick your EverQuest Logs folder, " +
                                  "or Browse to pick a log file. (Turn on logging in game with /log on.)";
            }
            return;
        }

        string directory = await _logSessions.ResolveDirectoryAsync(dialog.FolderName);
        var newest = await _logSessions.FindNewestAsync(directory);
        _settings.LogDirectory = directory;
        if (newest is null)
        {
            await StopTailingAsync();
            _logSessions.ForgetSource();
            _currentFile = null;
            _characterName = null;
            _inventoryService.SelectSession("", "");
            SynchronizeInventoryView();
            _settings.LogPath = null;
            ShowWaitingForLogs(directory);
            return;
        }

        await SwitchToAsync(newest.Path);
    }

    private async Task SwitchToAsync(string path)
    {
        await StopTailingAsync();
        await SaveNpcCompendiumAsync(throwOnError: false);
        ResetMobInfo();

        var info = await _logSessions.DescribeAsync(path);
        _currentFile = path;
        _characterName = info.CharacterName;
        _session.ConfigureTriggers(_triggers, _characterName);
        _settings.LogPath = path;
        _settings.LogDirectory = Path.GetDirectoryName(path);
        LogPathBox.Text = path;
        lock (_rawGate)
        {
            _rawLines.Clear();
            _rawVersion++;
        }

        _session.Start(_logSessions, info, _settings.ResetOnLogin);
        if (_overlayViewModels.TryGetValue("npcs", out var npcViewModel) &&
            npcViewModel is NpcBrowserOverlayViewModel npcs)
        {
            npcs.ResetForNewLog();
        }
        _inventoryView.ImportStatus = "Waiting for this character's inventory export. Create/update it in game with /outputfile inventory.";
        _questSelectionVersion++;
        SynchronizeInventoryView();
    }

    private async Task StopTailingAsync()
    {
        try
        {
            await _logSessions.StopAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Log reader stopped: {ex.Message}";
        }
    }

    private void Housekeeping(HousekeepingTick tick)
    {
        foreach (var w in _overlays.Values)
        {
            if (w.IsVisible)
            {
                w.ReassertTopmost();
            }
        }

        if (tick.RefreshDue)
        {
            CheckForNewerLog();
        }

        if (_logSessions.Completion is { IsCompleted: false } && !_logSessions.Progress.CaughtUp)
        {
            return;
        }

        _ = CheckInventoryAsync();
        CheckSpawnWiki();

        if (SpawnsTab.IsSelected)
        {
            RefreshSpawns();
        }

        if (tick.RefreshDue)
        {
            RefreshDurations();
        }

        if (tick.SaveDue)
        {
            SaveAllInBackground();
        }
    }

    private bool _checkingNewerLog;

    private async void CheckForNewerLog()
    {
        // While waiting in a chosen folder with no logs yet, the first log to appear is opened whatever FollowNewest says.
        bool waiting = _currentFile is null && _settings.LogDirectory is not null;
        if (_checkingNewerLog || (!waiting && (!_settings.FollowNewest || _currentFile is null)))
        {
            return;
        }

        string? dir = waiting ? _settings.LogDirectory : Path.GetDirectoryName(_currentFile);
        if (dir is null)
        {
            return;
        }

        _checkingNewerLog = true;
        try
        {
            var newest = await _logSessions.FindNewerAsync(dir, _settings.FollowNewest);
            if (waiting)
            {
                if (newest is not null && _currentFile is null)
                {
                    await SwitchToAsync(newest.Path);
                }
            }
            // Another character's log was written after we started reading this one: they logged in.
            else if (newest is not null && _settings.FollowNewest && _currentFile is not null)
            {
                await SwitchToAsync(newest.Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not check for a newer log: {ex.Message}";
        }
        finally
        {
            _checkingNewerLog = false;
        }
    }

    private void RefreshDurations(bool force = false)
    {
        if (!force && DurationsGrid.IsKeyboardFocusWithin)
        {
            return;
        }

        var list = _session.SpellDurations();
        string signature = string.Join('|', list.Select(d => $"{d.Spell}:{d.Samples}:{d.LearnedSeconds:F0}:{d.OverrideSeconds}:{d.Tracked}:{d.WikiSeconds}"));
        if (signature == _durationsSignature)
        {
            return;
        }

        _durationsSignature = signature;
        _durationRows.Clear();
        foreach (var d in list.OrderBy(d => d.Spell, StringComparer.OrdinalIgnoreCase))
        {
            _durationRows.Add(new DurationRow
            {
                Tracked = d.Tracked,
                Spell = d.Spell,
                Learned = d.LearnedSeconds is double s ? OverlayPresenters.Clock(s) : "-",
                Wiki = d.WikiSeconds is double wiki ? OverlayPresenters.Clock(wiki) : "-",
                Samples = d.Samples,
                Override = d.OverrideSeconds?.ToString("0.#", CultureInfo.CurrentCulture) ?? "",
            });
        }
    }

    private void AddDuration_Click(object sender, RoutedEventArgs e)
    {
        string spell = NewDurationSpellBox.Text.Trim();
        if (spell.Length == 0 || !TryReadSpellDuration(NewDurationSecondsBox.Text.Trim(), out double seconds))
        {
            MessageBox.Show(_dock, "Enter a spell name and a positive, finite duration in seconds.", "Add spell");
            return;
        }
        if (!DurationsGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !DurationsGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            return;
        }

        _session.AddSpellDuration(spell, seconds);
        _durationsSignature = "";
        RefreshDurations(force: true);
        NewDurationSpellBox.Clear();
        NewDurationSecondsBox.Clear();
        _ = SaveQuietlyAsync(_session.SaveSpellDurationsAsync(_tracking));
    }

    private void RemoveDuration_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DurationRow row })
        {
            return;
        }
        if (!DurationsGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !DurationsGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            return;
        }
        if (MessageBox.Show(_dock,
            $"Remove {row.Spell}, its learned samples and its override? It will stay removed until you manually add this spell again.",
            "Remove spell", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _session.RemoveSpellDuration(row.Spell);
        _durationsSignature = "";
        RefreshDurations(force: true);
        _ = SaveQuietlyAsync(_session.SaveSpellDurationsAsync(_tracking));
    }

    private void RelearnDuration_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DurationRow row })
        {
            return;
        }
        if (!DurationsGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !DurationsGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            return;
        }
        if (MessageBox.Show(_dock,
            $"Forget the learned duration for {row.Spell} ({row.Learned} from {row.Samples} samples) and measure it again from your next casts? Its override and Track setting are kept.",
            "Relearn spell", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _session.RelearnSpellDuration(row.Spell);
        _durationsSignature = "";
        RefreshDurations(force: true);
        _ = SaveQuietlyAsync(_session.SaveSpellDurationsAsync(_tracking));
    }

    private static bool TryReadSpellDuration(string text, out double seconds)
    {
        return GameSession.TryReadSpellDuration(text, out seconds);
    }

    private void DurationTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: DurationRow row } box)
        {
            _session.SetSpellTracked(row.Spell, box.IsChecked == true);
            _durationsSignature = "";
            _ = SaveQuietlyAsync(_session.SaveSpellDurationsAsync(_tracking));
        }
    }

    private void Durations_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not DurationRow row || e.EditingElement is not TextBox box)
        {
            return;
        }

        string text = box.Text.Trim();
        if (text.Length == 0)
        {
            _session.SetSpellDurationOverride(row.Spell, null);
        }
        else if (TryReadSpellDuration(text, out double seconds))
        {
            _session.SetSpellDurationOverride(row.Spell, seconds);
        }
        else
        {
            e.Cancel = true;
            MessageBox.Show(_dock, "Enter a positive, finite duration in seconds, or leave it empty to use the learned value or wiki estimate.", "Override");
            return;
        }

        _durationsSignature = "";
        _ = SaveQuietlyAsync(_session.SaveSpellDurationsAsync(_tracking));
    }

    private Task _periodicSave = Task.CompletedTask;
    private bool _sessionEnding;

    /// <summary>Copies window positions into the settings; must run on the UI thread before settings are serialized.</summary>
    private void CaptureLayout()
    {
        foreach (var (key, w) in _overlays)
        {
            if (!double.IsFinite(w.Left) || !double.IsFinite(w.Top) || !double.IsFinite(w.Width) || !double.IsFinite(w.Height))
            {
                continue;
            }

            var p = _settings.Overlays[key];
            p.Left = w.Left;
            p.Top = w.Top;
            p.Width = w.Width;
            p.Height = w.Height;
        }

        _dock.SaveBounds();
    }

    /// <summary>
    /// Captures UI-owned state and delegates independent ordered saves to the application coordinator.
    /// </summary>
    private async Task<PersistenceResult> StartSaves()
    {
        if (_dataReady)
        {
            return await _persistence.SaveAsync(_settings, _session);
        }
        try
        {
            await _settingsPersistence.SaveAsync(_settings);
            return new([new(PersistedFeature.Settings, null)]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or InvalidOperationException)
        {
            return new([new(PersistedFeature.Settings, error)]);
        }
    }

    /// <summary>Saves window layout, settings and everything learned. Runs every few seconds, on close and before updating.</summary>
    /// <param name="throwOnError">Fail on the first save error, or when a store is protected because it failed to load.</param>
    private async Task SaveAllAsync(bool throwOnError = false)
    {
        CaptureLayout();
        var result = await StartSaves();
        var generalErrors = new List<string>();
        foreach (var outcome in result.Outcomes)
        {
            if (outcome.Error is not { } error)
            {
                if (outcome.Feature == PersistedFeature.Npcs && _npcSaveEnabled)
                {
                    SetNpcStorageError(null);
                }
                if (outcome.Feature == PersistedFeature.Leveling)
                {
                    _levelingStorageError = null;
                }
                continue;
            }
            UsageTelemetry.TrackError(error, outcome.Feature == PersistedFeature.Wiki ? UsageErrorSource.Wiki : UsageErrorSource.StartupData);
            string message = $"Could not save {outcome.Feature}: {error.Message}. Changes remain retryable; protected files are unchanged.";
            switch (outcome.Feature)
            {
                case PersistedFeature.Inventory:
                    QuestLookupStatus.Text = message;
                    break;
                case PersistedFeature.Wiki:
                    ResetWikiCacheStatus.Text = message;
                    break;
                case PersistedFeature.Npcs:
                    SetNpcStorageError(message);
                    break;
                case PersistedFeature.Mobs:
                    SetMobInfoStatus(message);
                    break;
                case PersistedFeature.Leveling:
                    _levelingStorageError = message;
                    generalErrors.Add(message);
                    break;
                default:
                    generalErrors.Add(message);
                    break;
            }
        }
        if (generalErrors.Count > 0)
        {
            ResetTrackingDataStatus.Text = string.Join(" ", generalErrors);
        }
        if (throwOnError && result.Failures.FirstOrDefault() is { } failure)
        {
            throw new IOException($"Saving {failure.Feature} failed: {failure.Error!.Message}", failure.Error);
        }
    }

    /// <summary>Periodic save; skipped while the previous one is still writing.</summary>
    private void SaveAllInBackground()
    {
        if (_periodicSave.IsCompleted)
        {
            _periodicSave = SaveAllAsync();
        }
    }

    /// <summary>
    /// Windows logoff/shutdown only allows a synchronous handler, so this is the one place that blocks on IO.
    /// The saves run on the thread pool (the UI thread is parked, so nothing mutates state meanwhile) and never
    /// resume on the dispatcher, which keeps the wait deadlock-free.
    /// </summary>
    private void SaveAllBlocking()
    {
        _sessionEnding = true;
        CaptureLayout();
        try
        {
            var saving = Task.Run(StartSaves);
            if (!saving.Wait(TimeSpan.FromSeconds(15)))
            {
                UsageTelemetry.TrackError(new TimeoutException("Session-ending save exceeded 15 seconds."), UsageErrorSource.StartupData);
            }
            else
            {
                foreach (var failure in saving.Result.Failures)
                {
                    UsageTelemetry.TrackError(failure.Error!, UsageErrorSource.StartupData);
                }
            }
        }
        catch (AggregateException ex)
        {
            UsageTelemetry.TrackError(ex.GetBaseException(), UsageErrorSource.StartupData);
        }
    }
    private void SetLocked(bool locked)
    {
        _settings.Locked = locked;
        foreach (var (key, w) in _overlays)
        {
            _settings.Overlays[key].Locked = locked;
            _overlaySettings[key].SyncLocked(locked);
            w.SetLocked(locked);
        }
        UpdateLockState();
    }

    private void UpdateLockState()
    {
        bool allLocked = _settings.Overlays.Values.All(p => p.Locked == true);
        bool anyLocked = _settings.Overlays.Values.Any(p => p.Locked == true);
        _settings.Locked = allLocked;
        LockBox.IsChecked = allLocked ? true : anyLocked ? null : false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        NativeMethods.RegisterHotKey(hwnd, LockHotkeyId, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, 0x4C);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_SETTINGCHANGE && !_shuttingDown && _settings.Theme is null)
        {
            Dispatcher.BeginInvoke(RefreshSystemTheme, DispatcherPriority.Background);
        }

        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == LockHotkeyId)
        {
            SetLocked(!_settings.Locked);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private bool _shutDown;
    private bool _shuttingDown;

    /// <summary>
    /// The dock (main) window is closing. The close is held back while everything is saved asynchronously,
    /// then repeated; at Windows logoff the data was already saved, so it closes immediately.
    /// </summary>
    private async void Dock_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutDown || _sessionEnding)
        {
            return;
        }

        e.Cancel = true;
        if (_shuttingDown)
        {
            return;
        }
        _shuttingDown = true;
        SingleInstance.MarkClosing();
        await ShutdownAsync();
        _shutDown = true;
        _ = Dispatcher.BeginInvoke(() => _dock.Close());
    }

    private async Task ShutdownAsync()
    {
        _displayScaleSaveTimer.Stop();
        _rawFilterWork?.Dispose();
        CancelInventoryFilters();
        _updateCheckTimer.Stop();
        // Start disposal first so it abandons wiki work before the callers' tokens cancel it layer by layer.
        var disposingWiki = _wikiRepository.DisposeAsync();
        foreach (var viewModel in _overlayViewModels.Values.OfType<IDisposable>())
        {
            viewModel.Dispose();
        }
        _wikiCts.Cancel();
        await disposingWiki;
        _pump?.Dispose();
        _housekeepingStop.Cancel();
        try
        {
            await _housekeepingTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            UsageTelemetry.TrackError(ex, UsageErrorSource.StartupData);
            ResetTrackingDataStatus.Text = $"Maintenance stopped: {ex.Message}";
        }
        _gameTimer.Stop();
        NativeMethods.UnregisterHotKey(new WindowInteropHelper(this).Handle, LockHotkeyId);

        try
        {
            await _logSessions.DisposeAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Log reader stopped during shutdown: {ex.Message}";
        }
        await SaveAllAsync();
        _inventoryService.Dispose();
        foreach (var w in _overlays.Values)
        {
            w.Close();
        }
    }
    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        string dir = _currentFile is not null ? Path.GetDirectoryName(_currentFile)! : _settings.LogDirectory ?? LogSessionComposition.DefaultDirectory;
        var dlg = new OpenFileDialog
        {
            Title = "Choose an EverQuest log file",
            Filter = "EverQuest logs (eqlog*.txt)|eqlog*.txt|All files (*.*)|*.*",
            InitialDirectory = await _logSessions.DirectoryExistsAsync(dir) ? dir : null,
        };
        if (dlg.ShowDialog(_dock) == true)
        {
            await SwitchToAsync(dlg.FileName);
        }
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        await ChooseLogFolderAsync();
    }

    private void FollowNewest_Click(object sender, RoutedEventArgs e)
    {
        _settings.FollowNewest = FollowNewestBox.IsChecked == true;
        _logSessions.ResetFollowBaseline();
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        SetLocked(!_settings.Locked);
    }

    private void ResetSession_Click(object sender, RoutedEventArgs e)
    {
        _session.ResetSession();
    }

    private void ResetOnLogin_Click(object sender, RoutedEventArgs e)
    {
        _settings.ResetOnLogin = ResetOnLoginBox.IsChecked == true;
        _session.SetResetOnLogin(_settings.ResetOnLogin);
    }

    private void ResetPositions_Click(object sender, RoutedEventArgs e)
    {
        var work = SystemParameters.WorkArea;
        for (int i = 0; i < OverlayPresenters.All.Count; i++)
        {
            var def = OverlayPresenters.All[i];
            var p = DefaultPlacement(def, i, work);
            var w = _overlays[def.Key];
            w.Left = p.Left;
            w.Top = p.Top;
            w.Width = p.Width;
            w.Height = p.Height;
        }
    }

    private void TriggerColorPick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AlertTrigger trigger } button)
        {
            return;
        }

        CommitTriggerEdits();
        if (!AlertColors.TryParse(trigger.Color, out var initial))
        {
            AlertColors.TryParse(AlertColors.Default, out initial);
        }

        var result = ColorPickerDialog.Pick(button, initial);
        if (result.Color is { } selected)
        {
            trigger.Color = ColorPickerDialog.ToHex(selected);
            RefreshColorBindings(button);
            RebuildTriggers();
        }
        if (result.Message is not null)
        {
            TriggerStatusText.Text = string.IsNullOrEmpty(TriggerStatusText.Text)
                ? result.Message
                : TriggerStatusText.Text + " " + result.Message;
        }
    }

    /// <summary>
    /// Updates the color swatch and text beside the pick button. Refreshing the whole grid throws while a row is being edited.
    /// </summary>
    private static void RefreshColorBindings(FrameworkElement button)
    {
        if (button.Parent is not Panel panel)
        {
            return;
        }

        foreach (UIElement child in panel.Children)
        {
            System.Windows.Data.BindingOperations.GetBindingExpression(child, Border.BackgroundProperty)?.UpdateTarget();
            System.Windows.Data.BindingOperations.GetBindingExpression(child, TextBox.TextProperty)?.UpdateTarget();
        }
    }

    private void RebuildTriggers()
    {
        var errors = _session.ConfigureTriggers(_triggers, _characterName);
        TriggerStatusText.Text = errors.Count == 0 ? "" : "Invalid regex (trigger skipped): " +
            string.Join("; ", errors.Select(TriggerPresentation.CompileError));
        _settings.Triggers = [.. _triggers];
        SaveSettingsInBackground(error => TriggerStatusText.Text += $" Could not save alert triggers: {error}");
    }

    private void TriggerField_SourceUpdated(object? sender, System.Windows.Data.DataTransferEventArgs e)
    {
        Dispatcher.BeginInvoke(RebuildTriggers, DispatcherPriority.Background);
    }

    /// <summary>Saves the trigger field that still has keyboard focus (text boxes save when focus leaves).</summary>
    private void CommitTriggerEdits()
    {
        if (Keyboard.FocusedElement is TextBox box && TriggersGrid.IsAncestorOf(box))
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
    }

    private void Triggers_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Editors swallow the click that would normally select the row, so select the trigger being edited.
        if (e.NewFocus is not DependencyObject focused || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        var row = FindAncestor<DataGridRow>(focused);
        if (row is not null && !row.IsSelected)
        {
            TriggersGrid.SelectedItem = row.Item;
        }
    }

    private void AddTrigger_Click(object sender, RoutedEventArgs e)
    {
        CommitTriggerEdits();
        var trigger = new AlertTrigger();
        _triggers.Add(trigger);
        FocusTriggerField(trigger, "Name");
    }

    private void FocusTriggerField(AlertTrigger trigger, string field)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TriggersGrid.SelectedItem = trigger;
            TriggersGrid.ScrollIntoView(trigger);
            TriggersGrid.UpdateLayout();
            if (TriggersGrid.ItemContainerGenerator.ContainerFromItem(trigger) is DataGridRow row &&
                FindDescendant<TextBox>(row, box => (box.Tag as string) == field) is { } target)
            {
                target.Focus();
                target.SelectAll();
            }
        }, DispatcherPriority.Background);
    }

    private static T? FindAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null and not T)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return element as T;
    }

    private static T? FindDescendant<T>(DependencyObject parent, Func<T, bool> match)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found && match(found))
            {
                return found;
            }

            if (FindDescendant(child, match) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void DeleteTriggers_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = TriggersGrid.SelectedItems.OfType<AlertTrigger>().Any();
        e.Handled = true;
    }

    private void Triggers_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DeleteTriggersButton.IsEnabled = TriggersGrid.SelectedItems.OfType<AlertTrigger>().Any();
        CommandManager.InvalidateRequerySuggested();
    }

    private void DeleteTriggers_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedTriggers();
    }

    private void DeleteTriggers_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        DeleteSelectedTriggers();
    }

    private void DeleteSelectedTriggers()
    {
        CommitTriggerEdits();
        var selected = TriggersGrid.SelectedItems.OfType<AlertTrigger>().ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        string names = string.Join("\n", selected.Take(10).Select(t =>
            $"- {(string.IsNullOrWhiteSpace(t.Name) ? "(unnamed trigger)" : t.Name)}"));
        if (selected.Length > 10)
        {
            names += $"\n... and {selected.Length - 10} more";
        }

        string message = selected.Length == 1
            ? $"Delete this alert trigger?\n\n{names}\n\nThis cannot be undone."
            : $"Delete these {selected.Length} alert triggers?\n\n{names}\n\nThis cannot be undone.";
        if (MessageBox.Show(_dock, message, "Delete alert triggers",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var trigger in selected)
        {
            _triggers.Remove(trigger);
        }

        RebuildTriggers();
    }

    private void TestTrigger_Click(object sender, RoutedEventArgs e)
    {
        CommitTriggerEdits();
        RebuildTriggers();
        var alerts = _session.MatchTriggers([TestLineBox.Text]);
        double previewSeconds = AlertsOverlayViewModel.DisplaySeconds(alerts);
        _alertTestVisibleUntil = DateTime.Now.AddSeconds(previewSeconds + 1);
        SetOverlayVisible("alerts", true);
        if (_settings.Overlays["alerts"].Docked)
        {
            _dock.RevealPanel("alerts");
        }
        else
        {
            EnsureOnScreen(_overlays["alerts"]);
            _overlays["alerts"].ReassertTopmost();
        }
        if (alerts.Count > 0)
        {
            _pump.Publish(new TriggerAlertsMessage(alerts));
        }

        if (alerts.Count == 0)
        {
            TriggerStatusText.Text = "No enabled trigger matched that line." + (TriggerStatusText.Text.Length > 0 ? " " + TriggerStatusText.Text : "");
        }
        else
        {
            TriggerStatusText.Text = $"Matched: {string.Join(", ", alerts.Select(a => a.Trigger.Name))}. Showing in the Alerts overlay."
                + (TriggerStatusText.Text.Length > 0 ? " " + TriggerStatusText.Text : "");
        }
    }

    /// <summary>Runs on the tailer thread.</summary>
    private void RememberRawLines(IReadOnlyList<string> lines)
    {
        lock (_rawGate)
        {
            int skip = Math.Max(0, lines.Count - RawBufferSize);
            for (int i = skip; i < lines.Count; i++)
            {
                _rawLines.Enqueue(lines[i]);
            }

            while (_rawLines.Count > RawBufferSize)
            {
                _rawLines.Dequeue();
            }

            _rawVersion++;
        }
    }

    private void RawFilter_Changed(object sender, RoutedEventArgs e)
    {
        _rawShownVersion = -1;
        if (SettingsRoot.IsLoaded)
        {
            RefreshRawLines(force: true);
        }
    }

    private void RefreshRawLines(bool force = false)
    {
        var scroll = FindScrollViewer(RawLinesList);
        bool atEnd = scroll is null || scroll.VerticalOffset >= scroll.ScrollableHeight - 1;
        bool hold = PauseLinesBox.IsChecked == true || !atEnd || RawLinesList.SelectedItem is not null;
        if (!force && hold && _rawShownVersion >= 0)
        {
            return;
        }

        string[] snapshot;
        lock (_rawGate)
        {
            if (_rawVersion == _rawShownVersion)
            {
                return;
            }

            _rawShownVersion = _rawVersion;
            snapshot = [.. _rawLines];
        }

        string filter = RawFilterBox.Text;
        bool regex = RegexFilterBox.IsChecked == true;
        string? character = _characterName;
        _rawFilterWork ??= new(Dispatcher, error => RawStatusText.Text = $"Log filtering failed: {error.Message}");
        long revision = _session.Revision;
        var key = (_rawShownVersion, filter, regex, character, hold, revision);
        _rawFilterWork.Request(key, token =>
        {
            token.ThrowIfCancellationRequested();
            return RawLogQueries.Filter(snapshot, filter, regex, character, RawDisplayMax);
        }, selection =>
        {
            if (character == _characterName && revision == _session.Revision)
            {
                ApplyRawLines(selection, snapshot.Length, filter, hold);
            }
        }, delayMilliseconds: force ? 180 : 0);
    }

    private AsyncTextFilter<RawLogSelection>? _rawFilterWork;

    private void ApplyRawLines(RawLogSelection selection, int total, string filter, bool hold)
    {
        string status = selection.Status switch
        {
            RawLogFilterStatus.Success => "",
            RawLogFilterStatus.InvalidRegex => $"Invalid regex: {selection.Diagnostic}  ",
            RawLogFilterStatus.TimedOut => "Regex filter timed out; some lines could not be checked.  ",
            _ => throw new ArgumentOutOfRangeException(nameof(selection)),
        };
        var shown = selection.Lines.ToArray();
        object? selected = RawLinesList.SelectedItem;
        RawLinesList.ItemsSource = shown;
        if (selected is not null && Array.IndexOf(shown, selected) >= 0)
        {
            RawLinesList.SelectedItem = selected;
            RawLinesList.ScrollIntoView(selected);
        }
        else if (shown.Length > 0)
        {
            RawLinesList.ScrollIntoView(shown[^1]);
        }

        RawStatusText.Text = status + $"Showing {shown.Length:N0} of the last {total:N0} lines" +
                             (filter.Length > 0 ? " matching the filter" : "") +
                             (hold ? " (paused: scroll to the bottom and clear the selection, or untick Pause, to follow)" : "") +
                             ". Double-click a line to make an alert trigger from it.";
    }

    private void RawLines_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            RawLinesList.SelectedItem = null;
            RawFilter_Changed(sender, e);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
            {
                return sv;
            }

            if (FindScrollViewer(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void RawLines_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        MakeTriggerFromSelection();
    }

    private void MakeTrigger_Click(object sender, RoutedEventArgs e)
    {
        MakeTriggerFromSelection();
    }

    private void CopyLogMessage_Click(object sender, RoutedEventArgs e)
    {
        RawStatusText.Text = CopyLogMessage(RawLinesList.SelectedItem, Clipboard.SetText);
    }

    internal static string CopyLogMessage(object? selectedItem, Action<string> copy)
    {
        if (selectedItem is not string raw)
        {
            return "Select a line first.";
        }

        string message = RawLogQueries.Message(raw);
        if (string.IsNullOrEmpty(message))
        {
            return "The selected line has no message to copy.";
        }

        try
        {
            copy(message);
            return "Message copied. Paste it into an existing trigger and edit it as needed.";
        }
        catch (System.Runtime.InteropServices.ExternalException error)
        {
            return $"Could not copy the message to the clipboard: {error.Message}";
        }
    }

    private void MakeTriggerFromSelection()
    {
        if (RawLinesList.SelectedItem is not string raw)
        {
            RawStatusText.Text = "Select a line first.";
            return;
        }

        string message = RawLogQueries.Message(raw);
        var trigger = new AlertTrigger
        {
            Name = message.Length > 30 ? message[..30] : message,
            Pattern = RawLogQueries.TriggerPattern(message, _characterName),
            Text = "",
        };
        _triggers.Add(trigger);
        TestLineBox.Text = message;
        TriggersTab.IsSelected = true;

        // Start editing the new trigger's alert text.
        FocusTriggerField(trigger, "AlertText");
    }

    private void RefreshSpawns(bool force = false)
    {
        if (!force && System.Windows.Input.Keyboard.FocusedElement is TextBox editing && SpawnsGrid.IsAncestorOf(editing))
        {
            return;
        }

        var query = _session.SpawnQueries(ShowAllMobsBox.IsChecked == true);
        string? zone = query.SpawnZone;
        bool all = ShowAllMobsBox.IsChecked == true;
        var list = query.SpawnCandidates;
        string signature = zone + "|" + all + "|" + string.Join('|', list.Select(c => $"{c.Mob}:{c.Kills}:{c.LastKilled:O}:{c.Info?.Samples}:{c.Info?.LearnedSeconds}:{c.Info?.WikiSeconds}:{c.Info?.OverrideSeconds}:{c.Info?.Tracked}"));
        if (!force && signature == _spawnsSignature)
        {
            return;
        }

        _spawnsSignature = signature;
        SpawnZoneText.Text = zone is null ? "Zone: not known yet" : $"Zone: {zone}";
        _spawnRows.Clear();
        if (zone is null)
        {
            return;
        }

        foreach (var c in list)
        {
            _spawnRows.Add(new SpawnRow
            {
                Zone = zone,
                Mob = c.Mob,
                Kills = c.Kills,
                LastKilled = c.LastKilled is DateTime k ? k.ToString("HH:mm:ss", CultureInfo.CurrentCulture) : "",
                Learned = c.Info?.LearnedSeconds is double s ? OverlayPresenters.Clock(s) : "-",
                Wiki = c.Info?.WikiSeconds is double wiki ? OverlayPresenters.Clock(wiki) : "-",
                Samples = c.Info?.Samples ?? 0,
                Override = c.Info?.OverrideSeconds is double o ? OverlayPresenters.Clock(o) : "",
                Tracked = c.Info?.Tracked == true,
            });
        }
    }

    private void ShowAllMobs_Click(object sender, RoutedEventArgs e)
    {
        RefreshSpawns(force: true);
    }

    private void SpawnTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SpawnRow row } && sender is CheckBox box)
        {
            _session.SetSpawnTracked(row.Zone, row.Mob, box.IsChecked == true);
            CheckSpawnWiki();
            _ = SaveQuietlyAsync(_session.SaveSpawnsAsync(_tracking));
        }
    }

    private void RelearnSpawn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SpawnRow row })
        {
            return;
        }
        if (!SpawnsGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !SpawnsGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            return;
        }
        if (MessageBox.Show(_dock,
            $"Forget the learned respawn time for {row.Mob} ({row.Learned} from {row.Samples} samples) and measure it again from your next kill? Its override and Track setting are kept.",
            "Relearn spawn", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _session.RelearnSpawn(row.Zone, row.Mob);
        CheckSpawnWiki();
        RefreshSpawns(force: true);
        _ = SaveQuietlyAsync(_session.SaveSpawnsAsync(_tracking));
    }

    private void Spawns_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not SpawnRow row || e.EditingElement is not TextBox box)
        {
            return;
        }

        string text = box.Text.Trim();
        if (text.Length == 0)
        {
            _session.SetSpawnOverride(row.Zone, row.Mob, null);
        }
        else if (GameSession.TryReadSpawnDuration(text, out double seconds))
        {
            _session.SetSpawnOverride(row.Zone, row.Mob, seconds);
        }
        else
        {
            e.Cancel = true;
            MessageBox.Show(_dock, "Enter a respawn time as seconds (960) or minutes:seconds (16:00), or leave it empty to use the learned time or wiki fallback.", "Override");
            return;
        }

        _spawnsSignature = "";
        _ = SaveQuietlyAsync(_session.SaveSpawnsAsync(_tracking));
    }

    /// <summary>Settings save for UI handlers that can't await; <paramref name="onError"/> receives the failure message.</summary>
    private async void SaveSettingsInBackground(Action<string>? onError = null)
    {
        try
        {
            await _settingsPersistence.SaveAsync(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            UsageTelemetry.TrackError(ex, UsageErrorSource.StartupData);
            onError?.Invoke(ex.Message);
            if (onError is null)
            {
                ResetTrackingDataStatus.Text = $"Could not save settings: {ex.Message}";
            }
        }
    }

    /// <summary>For small learned-data saves; a failed write stays dirty and the periodic save retries it.</summary>
    private async Task SaveQuietlyAsync(Task save)
    {
        try
        {
            await save;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            UsageTelemetry.TrackError(ex, UsageErrorSource.StartupData);
            ResetTrackingDataStatus.Text = $"Could not save tracking data: {ex.Message}. Changes remain dirty; periodic saves retry.";
        }
    }
}
