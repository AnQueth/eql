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
using EqLog.Core.IO;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using EqLog.Core.Triggers;
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

    /// <summary>If the newest log line is older than this, we are viewing an old log and use log time as "now".</summary>
    private static readonly TimeSpan ReplayThreshold = TimeSpan.FromMinutes(30);

    private readonly AppSettings _settings;
    private readonly SpellDurationStore _durations;
    private readonly Dictionary<string, OverlayWindow> _overlays = [];
    private readonly Dictionary<string, CheckBox> _overlayToggles = [];
    private readonly Dictionary<string, CheckBox> _dockToggles = [];
    private readonly Dictionary<string, CheckBox> _lockToggles = [];
    private DockWindow _dock = null!;
    private readonly HashSet<ChatChannel> _chatChannels;
    private readonly ObservableCollection<DurationRow> _durationRows = [];
    private readonly SpawnStore _spawnStore;
    private readonly MoteStore _moteStore;
    private readonly ObservableCollection<SpawnRow> _spawnRows = [];
    private string _spawnsSignature = "";
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private static readonly TimeSpan WikiRenderInterval = TimeSpan.FromSeconds(1);
    private DateTime _lastWikiRender;
    private readonly DispatcherTimer _houseTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _gameTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _gameVisible = true;
    private readonly object _progressGate = new();
    private readonly ObservableCollection<AlertTrigger> _triggers;
    private readonly ConcurrentQueue<TriggerAlert> _pendingAlerts = new();
    private readonly List<ActiveAlert> _activeAlerts = [];
    private readonly Dictionary<string, Brush> _alertBrushes = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxAlerts = 6;

    private volatile TriggerMatcher _matcher = TriggerMatcher.Empty;
    private string? _characterName;

    private const int RawBufferSize = 5000;
    private const int RawDisplayMax = 2000;
    private readonly Queue<string> _rawLines = new();
    private readonly object _rawGate = new();
    private long _rawVersion;
    private long _rawShownVersion = -1;

    private LogEngine _engine = new();
    private CancellationTokenSource? _cts;
    private Task? _tailTask;
    private string? _currentFile;
    private DateTime _followBaseline;
    private TailProgress _progress;
    private string _durationsSignature = "";
    private int _houseTicks;
    private bool _rendering;

    public MainWindow(StartupState state)
    {
        _settings = state.Settings;
        _durations = state.Durations;
        _engine = new LogEngine(_durations);
        _spawnStore = state.Spawns;
        _moteStore = state.Motes;
        InitializeComponent();
        InitializeThemes(state);
        InitializeLootQuests(state);
        InitializeQuestInventory(state);
        InitializeMobInfo(state);
        InitializeExaltations(state);
        InitializeNpcCompendium(state);
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
        UsageTelemetryBox.IsChecked = _settings.ShareUsageTelemetry;
        _gameVisible = GameWindowWatcher.IsGameVisible();
        ResetOnLoginBox.IsChecked = _settings.ResetOnLogin;

        CreateOverlays();
        TrackUsageState();
        UpdateLockState();
        CreateChatToggles();

        Application.Current.SessionEnding += (_, _) => SaveAllBlocking();
        _renderTimer.Tick += async (_, _) => await RenderAsync();
        _houseTimer.Tick += (_, _) => Housekeeping();
        _gameTimer.Tick += (_, _) => CheckGameVisible();

        // This window is never shown: its UI lives in the dock window's side panel, and the dock window is the
        // app's main window. A hidden handle is still needed for the Ctrl+Shift+L hotkey.
        Content = null;
        _dock.SetSidePanel(SettingsRoot);
        _dock.Closing += Dock_Closing;
        _dock.Closed += (_, _) =>
        {
            Close();
            Application.Current.Shutdown();
        };
        new WindowInteropHelper(this).EnsureHandle();
        Application.Current.MainWindow = _dock;
        _dock.Show();
        if (_dock.WindowState == WindowState.Normal)
        {
            EnsureOnScreen(_dock);
        }

        // Started directly rather than from Loaded: Show() can raise Loaded synchronously, before a handler is attached.
        _renderTimer.Start();
        _houseTimer.Start();
        _gameTimer.Start();
        Dispatcher.BeginInvoke(async () => await OpenInitialLogAsync(), DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(async () => await InitializeUpdatesAsync(), DispatcherPriority.Loaded);
    }

    private void CreateOverlays()
    {
        var work = SystemParameters.WorkArea;
        _dock = new DockWindow(_settings.DockWindow);
        for (int i = 0; i < OverlayPresenters.All.Count; i++)
        {
            var def = OverlayPresenters.All[i];
            if (!_settings.Overlays.TryGetValue(def.Key, out var p))
            {
                p = DefaultPlacement(def, i, work);
                _settings.Overlays[def.Key] = p;
            }
            p.FontSize ??= _settings.FontSize;
            p.BackgroundOpacity ??= _settings.BackgroundOpacity;
            p.Locked ??= _settings.Locked;
            var panel = new OverlayPanel(def.Key, def.Caption, def.AutoScroll) { FontSize = p.FontSize.Value };
            panel.SetBackgroundOpacity(p.BackgroundOpacity.Value);
            if (def.Key == "quests")
            {
                ConfigureQuestToolbar(panel);
            }

            if (def.Key == "sky")
            {
                ConfigureSkyToolbar(panel);
            }
            if (def.Key == "mobinfo")
            {
                ConfigureMobToolbar(panel);
            }
            if (def.Key == "npcs")
            {
                ConfigureNpcToolbar(panel);
            }
            if (def.Key == "inventory")
            {
                ConfigureInventoryOverlay(panel);
            }
            if (def.Key == "moteplan")
            {
                ConfigureMotePlanOverlay(panel);
            }
            if (def.Key == "exaltations")
            {
                ConfigureExaltations(panel);
            }

            if (def.Key == "loot")
            {
                var grouping = new ComboBox
                {
                    ItemsSource = new[] { "Recent drops", "Grouped: most recent", "Grouped: item name (A-Z)" },
                    SelectedIndex = Math.Clamp(_settings.LootGrouping, 0, 2),
                    Margin = new Thickness(0, 5, 0, 5),
                    ToolTip = "Grouped views count all loot observed this session, separately per looter. Upgrade variants are combined.",
                };
                grouping.SelectionChanged += (_, _) => _settings.LootGrouping = grouping.SelectedIndex;
                panel.SetToolbar(grouping);
            }
            panel.LootLookupRequested += async item => await LookupLootItemAsync(item);
            panel.ItemWikiRequested += item => OpenWiki(QuestLookup.PageUrl(QuestLookup.ItemTitle(item)));
            panel.LinkRequested += url => OpenWiki(url);
            panel.BreakdownRequested += breakdown =>
            {
                UsageTelemetry.TrackFeature(UsageFeature.AbilityBreakdown);
                new AbilityBreakdownWindow(breakdown) { Owner = _dock }.Show();
            };
            panel.DockToggleRequested += pnl =>
                Dispatcher.BeginInvoke(() => SetDocked(pnl.Key, !_settings.Overlays[pnl.Key].Docked));
            var w = new OverlayWindow(panel);
            w.Left = p.Left;
            w.Top = p.Top;
            w.Width = p.Width;
            w.Height = p.Height;
            w.SetLocked(p.Locked.Value);
            EnsureOnScreen(w);
            _overlays[def.Key] = w;

            var toggle = new CheckBox { Content = def.Caption, IsChecked = p.Visible, Tag = def.Key, Margin = new Thickness(0, 6, 8, 6) };
            toggle.Click += (_, _) => SetOverlayVisible(def.Key, toggle.IsChecked == true);
            _overlayToggles[def.Key] = toggle;
            var docked = new CheckBox
            {
                Content = "docked",
                IsChecked = p.Docked,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 6),
                ToolTip = "Show this panel in the docked-panels window (e.g. on a second monitor) instead of as an overlay",
            };
            docked.SetResourceReference(ForegroundProperty, "NightMuted");
            docked.Click += (_, _) => SetDocked(def.Key, docked.IsChecked == true);
            _dockToggles[def.Key] = docked;
            var toggleRow = new Grid { Width = 250, Margin = new Thickness(0, 0, 12, 0) };
            toggleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
            toggleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(docked, 1);
            toggleRow.Children.Add(toggle);
            toggleRow.Children.Add(docked);
            OverlayToggles.Children.Add(toggleRow);
            OverlayAppearance.Children.Add(CreateOverlayAppearance(w, p));

            ApplyMode(def.Key);
        }
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

    private Expander CreateOverlayAppearance(OverlayWindow window, OverlayPlacement placement)
    {
        var controls = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };
        var locked = new CheckBox
        {
            Content = "Locked (click-through)",
            IsChecked = placement.Locked,
            Margin = new Thickness(0, 4, 0, 8),
            ToolTip = "Locks this floating overlay only. Docked panels remain interactive. Unlock here to move or resize it.",
        };
        locked.Click += (_, _) =>
        {
            placement.Locked = locked.IsChecked == true;
            window.SetLocked(placement.Locked.Value);
            UpdateLockState();
        };
        _lockToggles[window.Panel.Key] = locked;
        controls.Children.Add(locked);

        AddSlider("Text size", 9, 22, placement.FontSize!.Value, 1, value =>
        {
            placement.FontSize = value;
            window.Panel.FontSize = value;
        }, value => $"{value:0} pt");
        AddSlider("Background", 0, 1, placement.BackgroundOpacity!.Value, 0.05, value =>
        {
            placement.BackgroundOpacity = value;
            window.Panel.SetBackgroundOpacity(value);
        }, value => $"{value:P0}");
        return new Expander { Header = window.Panel.Caption, Content = controls };

        void AddSlider(string caption, double minimum, double maximum, double value, double tick,
            Action<double> changed, Func<double, string> format)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            row.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center });
            var slider = new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = value,
                TickFrequency = tick,
                SmallChange = tick,
                LargeChange = tick * 2,
                IsSnapToTickEnabled = true,
                ToolTip = $"{caption} for {window.Panel.Caption}",
            };
            var label = new TextBlock
            {
                Text = format(slider.Value),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            slider.ValueChanged += (_, e) =>
            {
                changed(e.NewValue);
                label.Text = format(e.NewValue);
            };
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(label, 2);
            row.Children.Add(slider);
            row.Children.Add(label);
            controls.Children.Add(row);
        }
    }

    /// <summary>Puts an overlay's panel in its floating window or in the dock window, per its settings.</summary>
    private void ApplyMode(string key)
    {
        var p = _settings.Overlays[key];
        var w = _overlays[key];
        bool visible = p.Visible && (key != "mobinfo" || MobInfoActive);
        if (p.Docked)
        {
            w.Hide();
            w.DetachPanel();
            if (visible)
            {
                _dock.AddPanel(w.Panel, p);
            }
            else
            {
                _dock.RemovePanel(key);
            }

            return;
        }

        _dock.RemovePanel(key);
        w.AttachPanel();
        if (visible && (FloatingAllowed || (key == "alerts" && DateTime.Now < _alertTestVisibleUntil)))
        {
            w.Show();
            w.SetLocked(p.Locked == true);
        }
        else
        {
            w.Hide();
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
    }

    /// <summary>Re-applies show/hide for every floating overlay.</summary>
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
        return p.Visible && (key != "mobinfo" || MobInfoActive) && (p.Docked || _overlays[key].IsVisible);
    }

    private void SetDocked(string key, bool docked)
    {
        bool changed = _settings.Overlays[key].Docked != docked;
        _settings.Overlays[key].Docked = docked;
        _dockToggles[key].IsChecked = docked;
        ApplyMode(key);
        if (changed)
        {
            UsageTelemetry.TrackPanelState(key, _settings.Overlays[key].Visible, docked, changed: true);
        }
        if (docked && _settings.Overlays[key].Visible)
        {
            ShowDock();
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
            SetDocked(def.Key, false);
        }
    }

    private static OverlayPlacement DefaultPlacement(OverlayDefinition def, int index, Rect work)
    {
        if (def.Key is "npcs" or "wiki" or "inventory" or "moteplan" or "exaltations")
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

    private void CreateChatToggles()
    {
        foreach (var channel in Enum.GetValues<ChatChannel>())
        {
            var box = new CheckBox
            {
                Content = channel.ToString(),
                IsChecked = _chatChannels.Contains(channel),
                Width = 140,
                Margin = new Thickness(0, 6, 10, 6),
            };
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true)
                {
                    _chatChannels.Add(channel);
                }
                else
                {
                    _chatChannels.Remove(channel);
                }

                _settings.ChatChannels = [.. _chatChannels];
            };
            ChatToggles.Children.Add(box);
        }
    }

    private void SetOverlayVisible(string key, bool visible)
    {
        bool changed = _settings.Overlays[key].Visible != visible;
        if (visible && changed)
        {
            UsageTelemetry.TrackPanelOpened(key);
        }

        _settings.Overlays[key].Visible = visible;
        ApplyMode(key);
        if (changed)
        {
            UsageTelemetry.TrackPanelState(key, visible, _settings.Overlays[key].Docked, changed: true);
        }
        if (visible && _settings.Overlays[key].Docked)
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
        // Probing the disk has no async API, so it runs on the thread pool.
        string? path = await Task.Run(() => FindInitialLog(args, savedPath, savedDirectory, followNewest));

        if (path is not null)
        {
            await SwitchToAsync(path);
            return;
        }

        if (savedDirectory is not null && await Task.Run(() => Directory.Exists(savedDirectory)))
        {
            ShowWaitingForLogs(savedDirectory);
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
            InitialDirectory = start is not null && await Task.Run(() => Directory.Exists(start)) ? start : null,
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

        string directory = await Task.Run(() => LogFileLocator.ResolveChosenDirectory(dialog.FolderName));
        var newest = await Task.Run(() => LogFileLocator.FindNewest(directory));
        _settings.LogDirectory = directory;
        if (newest is null)
        {
            await StopTailingAsync();
            _currentFile = null;
            _settings.LogPath = null;
            ShowWaitingForLogs(directory);
            return;
        }

        await SwitchToAsync(newest.Path);
    }

    private static string? FindInitialLog(string[] args, string? savedPath, string? savedDirectory, bool followNewest)
    {
        string? path = args.Skip(1).FirstOrDefault(File.Exists);
        if (path is null && savedPath is not null && File.Exists(savedPath))
        {
            path = savedPath;
        }

        // When following the newest log, prefer whatever is newest in the folder at startup.
        if (path is null || followNewest)
        {
            string? dir = (path is not null ? Path.GetDirectoryName(path) : null) ?? savedDirectory ?? LogFileLocator.DetectDirectory();
            if (args.Length <= 1 && dir is not null && LogFileLocator.FindNewest(dir) is { } newest)
            {
                path = newest.Path;
            }
        }
        return path;
    }

    private async Task SwitchToAsync(string path)
    {
        await StopTailingAsync();
        await SaveNpcCompendiumAsync(throwOnError: false);
        ResetMobInfo();

        var info = await Task.Run(() => LogFileLocator.Describe(path));
        _engine = new LogEngine(_durations, _spawnStore, _moteStore, _mobResistances, Path.GetFullPath(path), _npcCompendium)
        {
            CharacterName = info.CharacterName,
            ResetOnLogin = _settings.ResetOnLogin,
        };
        _currentFile = path;
        _characterName = info.CharacterName;
        _matcher = new TriggerMatcher(_triggers, _characterName);
        _followBaseline = DateTime.Now;
        _settings.LogPath = path;
        _settings.LogDirectory = Path.GetDirectoryName(path);
        LogPathBox.Text = path;
        lock (_progressGate)
        {
            _progress = default;
        }

        lock (_rawGate)
        {
            _rawLines.Clear();
            _rawVersion++;
        }

        var engine = _engine;
        var cts = new CancellationTokenSource();
        _cts = cts;
        var tailer = new LogTailer(path);

        // Both callbacks run on the tailer thread. Lines delivered after the first catch-up are new, so only those fire alerts.
        bool live = false;
        engine.SpellObserved += _wikiRepository.ObserveSpell;
        engine.MobConsidered += (con, zone) =>
        {
            if (live)
            {
                _mobEvents.Enqueue((con, zone));
            }
        };
        engine.ConsiderAssessment += assessment =>
        {
            if (live)
            {
                _mobEvents.Enqueue((assessment, ""));
            }
        };
        string questCharacter = _characterName ?? "";
        engine.LootObserved += (loot, lineNumber) =>
        {
            if (loot.Looter == Names.You && loot.SoldFor is null && !loot.Stored)
            {
                bool newLoot = live;
                _questLoot.Enqueue(() => _questInventory.NoteLoot(questCharacter, path, lineNumber, loot.Item, loot.Quantity, newLoot, loot.Time.ToUniversalTime()));
            }
        };
        _tailTask = Task.Run(() => tailer.RunAsync(
            fromStart: true,
            onLines: lines =>
            {
                engine.ProcessLines(lines);
                RememberRawLines(lines);
                if (live)
                {
                    foreach (var alert in _matcher.Match(lines))
                    {
                        _pendingAlerts.Enqueue(alert);
                    }
                }
            },
            onProgress: p =>
            {
                live |= p.CaughtUp;
                lock (_progressGate)
                {
                    _progress = p;
                }
            },
            cts.Token));
    }

    private async Task StopTailingAsync()
    {
        if (_cts is null || _tailTask is null)
        {
            return;
        }

        await _cts.CancelAsync();
        try
        {
            await _tailTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Log reader stopped: {ex.Message}";
        }

        _cts.Dispose();
        _cts = null;
        _tailTask = null;
    }

    private async Task RenderAsync()
    {
        if (_rendering || _shutDown)
        {
            return;
        }

        _rendering = true;
        try
        {
            await RenderFrameAsync();
        }
        finally
        {
            _rendering = false;
        }
    }

    private async Task RenderFrameAsync()
    {
        PumpMobInfo();
        TailProgress p;
        lock (_progressGate)
        {
            p = _progress;
        }
        HistoryProgress.Value = p.TotalBytes > 0 ? (double)p.BytesRead / p.TotalBytes : 0;
        HistoryProgress.Visibility = p.CaughtUp ? Visibility.Hidden : Visibility.Visible;
        if (!p.CaughtUp && _currentFile is not null)
        {
            StatusText.Text = $"Loading history {HistoryProgress.Value:P0} - {p.LinesRead:N0} lines";
        }

        var engine = _engine;
        var channels = new HashSet<ChatChannel>(_chatChannels);
        bool includeChat = IsShowing("chat");
        var frame = await Task.Run(() =>
        {
            // Waiting for the parser and building detached tracker snapshots must not park the dispatcher.
            var wallTime = DateTime.Now;
            var lastLogTime = engine.LastLogTime;
            var snapshotTime = lastLogTime is DateTime time && wallTime - time > ReplayThreshold
                ? time
                : wallTime;
            var snapshot = engine.Snapshot(snapshotTime);
            IReadOnlyList<ChatEvent> chat = includeChat ? engine.RecentChat(60, channels) : [];
            return (Snapshot: snapshot, Chat: chat);
        });
        if (_shutDown || !ReferenceEquals(engine, _engine))
        {
            return;
        }

        var snap = frame.Snapshot;
        var real = DateTime.Now;
        var last = snap.LastLogTime;
        bool replay = last is DateTime l && real - l > ReplayThreshold;
        RefreshNpcChoices();
        string title = $"EverQuest Log Overlay - v{AppVersion}";
        if (!string.IsNullOrWhiteSpace(snap.CharacterName))
        {
            title += $" - {snap.CharacterName}";
            title += snap.CurrentLoadout is { } current
                ? $" [{current.Level} {current.Classes}]" : " [classes unknown - use /who]";
        }
        if (_dock.Title != title)
        {
            _dock.Title = title;
        }
        PumpQuestLoot();
        // Wiki activity only changes on a seconds scale; refreshing it every frame made the whole UI sluggish.
        bool renderWiki = IsShowing("wiki") && real - _lastWikiRender >= WikiRenderInterval;
        if (renderWiki)
        {
            _lastWikiRender = real;
        }
        var view = new OverlayView(_chatChannels, UpdateAlerts(real), _settings.LootGrouping, _questInventory,
            renderWiki ? _wikiRepository.GetActivity() : null,
            renderWiki ? GetQuestWikiActivity() : null, frame.Chat);
        if (LogLinesTab.IsSelected)
        {
            RefreshRawLines();
        }

        var renderBudget = System.Diagnostics.Stopwatch.StartNew();
        foreach (var def in OverlayPresenters.All)
        {
            if (!IsShowing(def.Key))
            {
                continue;
            }
            if (def.Key == "wiki" && !renderWiki)
            {
                continue;
            }

            var content = def.Key switch
            {
                "quests" => QuestOpportunities(),
                "sky" => SkyOverlayContent(),
                "mobinfo" => MobInfoContent(),
                "npcs" => NpcCompendiumContent(),
                "inventory" => new("Inventory & loot quests", _characterName, []),
                "moteplan" => new("Mote upgrade planner", _characterName, []),
                "exaltations" => ExaltationsContent(),
                _ => def.Render(snap, _engine, view),
            };
            var rows = def.Key is "loot" or "wiki"
                ? content.Rows.Select(row => row.LootItem is { } item
                    ? row with { HasQuestReferences = HasQuestReferences(item) }
                    : row).ToArray()
                : content.Rows;
            _overlays[def.Key].Panel.Update(content.Title, content.Subtitle, rows, content.TitleLink);
            if (!p.CaughtUp && renderBudget.ElapsedMilliseconds >= 8)
            {
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                if (_shutDown || !ReferenceEquals(engine, _engine))
                {
                    return;
                }
                renderBudget.Restart();
            }
        }

        if (_tailTask is { IsFaulted: true } t)
        {
            StatusText.Text = $"Log reader error: {t.Exception?.GetBaseException().Message}";
            return;
        }

        if (_currentFile is null)
        {
            return;
        }

        string phase = p.CaughtUp ? "Live" : $"Loading history {HistoryProgress.Value:P0}";
        string zone = snap.Session.Zone is { } z ? $" in {z}" : "";
        string when = last is DateTime lt ? $"last line {lt:g}" : "no lines yet";
        string replayNote = replay ? "  (log is not being written; showing it as of the last line)" : "";
        string loadout = snap.CurrentLoadout is { } profile
            ? $" [{profile.Level} {profile.Classes}]" : " [classes unknown - use /who]";
        StatusText.Text = $"{phase}: {snap.CharacterName}{loadout}{zone} - {snap.LinesProcessed:N0} lines, {when}{replayNote}";
    }

    private void Housekeeping()
    {
        _houseTicks++;
        foreach (var w in _overlays.Values)
        {
            if (w.IsVisible)
            {
                w.ReassertTopmost();
            }
        }

        if (_houseTicks % 3 == 0)
        {
            CheckForNewerLog();
        }

        lock (_progressGate)
        {
            if (_tailTask is { IsCompleted: false } && !_progress.CaughtUp)
            {
                return;
            }
        }

        _ = CheckInventoryAsync();
        CheckSpawnWiki();
        CheckMotePlan();

        if (SpawnsTab.IsSelected)
        {
            RefreshSpawns();
        }

        if (_houseTicks % 3 == 0)
        {
            RefreshDurations();
        }

        if (_houseTicks % 5 == 0)
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
            var newest = await Task.Run(() => LogFileLocator.FindNewest(dir));
            if (waiting)
            {
                if (newest is not null && _currentFile is null)
                {
                    await SwitchToAsync(newest.Path);
                }
            }
            // Another character's log was written after we started reading this one: they logged in.
            else if (newest is not null && _settings.FollowNewest && _currentFile is not null &&
                !string.Equals(newest.Path, _currentFile, StringComparison.OrdinalIgnoreCase) &&
                newest.LastWriteTime > _followBaseline)
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

        var list = _engine.SpellDurations();
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

        _engine.SetSpellDurationOverride(spell, seconds);
        _engine.SetSpellTracked(spell, true);
        _durationsSignature = "";
        RefreshDurations(force: true);
        NewDurationSpellBox.Clear();
        NewDurationSecondsBox.Clear();
        _ = SaveQuietlyAsync(_engine.SaveSpellDurationsAsync(AppSettings.DurationsPath));
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

        _engine.RemoveSpellDuration(row.Spell);
        _durationsSignature = "";
        RefreshDurations(force: true);
        _ = SaveQuietlyAsync(_engine.SaveSpellDurationsAsync(AppSettings.DurationsPath));
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

        _engine.RelearnSpellDuration(row.Spell);
        _durationsSignature = "";
        RefreshDurations(force: true);
        _ = SaveQuietlyAsync(_engine.SaveSpellDurationsAsync(AppSettings.DurationsPath));
    }

    private static bool TryReadSpellDuration(string text, out double seconds)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds) &&
            double.IsFinite(seconds) && seconds > 0 && seconds <= TimeSpan.MaxValue.TotalSeconds - 60;
    }

    private void DurationTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: DurationRow row } box)
        {
            _engine.SetSpellTracked(row.Spell, box.IsChecked == true);
            _durationsSignature = "";
            _ = SaveQuietlyAsync(_engine.SaveSpellDurationsAsync(AppSettings.DurationsPath));
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
            _engine.SetSpellDurationOverride(row.Spell, null);
        }
        else if (TryReadSpellDuration(text, out double seconds))
        {
            _engine.SetSpellDurationOverride(row.Spell, seconds);
        }
        else
        {
            e.Cancel = true;
            MessageBox.Show(_dock, "Enter a positive, finite duration in seconds, or leave it empty to use the learned value or wiki estimate.", "Override");
            return;
        }

        _durationsSignature = "";
        _ = SaveQuietlyAsync(_engine.SaveSpellDurationsAsync(AppSettings.DurationsPath));
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
    /// Starts every save. Thread-safe tracker stores prepare and write on workers; UI-owned state is captured
    /// on the caller's thread. Stores order writes so older saves cannot win. <c>Report</c> runs on the UI thread
    /// with the error message, or null on success.
    /// </summary>
    private List<(Task Task, Action<string?>? Report)> StartSaves()
    {
        var engine = _engine;
        var saves = new List<(Task Task, Action<string?>? Report)>
        {
            (_settings.SaveAsync(), null),
            (Task.Run(() => engine.SaveSpellDurationsAsync(AppSettings.DurationsPath)), null),
            (Task.Run(() => engine.SaveSpawnsAsync(AppSettings.SpawnsPath)), null),
            (Task.Run(() => engine.SaveMotesAsync(AppSettings.MotesPath)), null),
            (StartQuestInventorySave(), error =>
            {
                if (error is not null)
                {
                    _questDirty = true;
                    QuestLookupStatus.Text = $"Could not save quest inventory: {error}";
                }
            }),
        };
        if (_mobSaveEnabled)
        {
            saves.Add((Task.Run(() => _mobResistances.SaveAsync(MobResistancePath)), error =>
            {
                if (error is not null)
                {
                    MobInfoStatusText.Text = $"Could not save mob resistances: {error}";
                }
            }));
        }
        if (_npcSaveEnabled)
        {
            saves.Add((Task.Run(() => _npcCompendium.SaveAsync(NpcCompendiumFolder)), error =>
            {
                _npcStorageError = error is null ? null : $"NPC compendium save failed: {error}";
                if (_npcStorageError is not null)
                {
                    MobInfoStatusText.Text = _npcStorageError;
                }
            }));
        }
        return saves;
    }

    /// <summary>Saves window layout, settings and everything learned. Runs every few seconds, on close and before updating.</summary>
    /// <param name="throwOnError">Fail on the first save error, or when a store is protected because it failed to load.</param>
    private async Task SaveAllAsync(bool throwOnError = false)
    {
        CaptureLayout();
        foreach (var (task, report) in StartSaves())
        {
            try
            {
                await task;
                report?.Invoke(null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report?.Invoke(ex.Message);
                if (throwOnError)
                {
                    throw;
                }
            }
        }
        if (throwOnError && !_mobSaveEnabled)
        {
            throw new IOException("Mob data was not loaded successfully. The original file is protected from overwrites.");
        }
        if (throwOnError && !_npcSaveEnabled)
        {
            throw new IOException(_npcStorageError);
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
            Task.Run(() => Task.WhenAll(StartSaves().Select(s => s.Task))).Wait(TimeSpan.FromSeconds(15));
        }
        catch (AggregateException)
        {
            // The session is ending; there is nowhere left to report a failed save.
        }
    }
    private void SetLocked(bool locked)
    {
        _settings.Locked = locked;
        foreach (var (key, w) in _overlays)
        {
            _settings.Overlays[key].Locked = locked;
            _lockToggles[key].IsChecked = locked;
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
        await ShutdownAsync();
        _shutDown = true;
        _dock.Close();
    }

    private async Task ShutdownAsync()
    {
        // Start disposal first so it abandons wiki work before the callers' tokens cancel it layer by layer.
        var disposingWiki = _wikiRepository.DisposeAsync();
        _wikiCts.Cancel();
        _npcWikiCts?.Cancel();
        await disposingWiki;
        _renderTimer.Stop();
        _houseTimer.Stop();
        _gameTimer.Stop();
        NativeMethods.UnregisterHotKey(new WindowInteropHelper(this).Handle, LockHotkeyId);

        _cts?.Cancel();
        if (_tailTask is { } tail)
        {
            try
            {
                // Finish the current batch before the final save.
                await tail;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Log reader stopped during shutdown: {ex.Message}";
            }
        }
        await SaveAllAsync();
        foreach (var w in _overlays.Values)
        {
            w.Close();
        }
    }
    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        string dir = _currentFile is not null ? Path.GetDirectoryName(_currentFile)! : _settings.LogDirectory ?? LogFileLocator.DefaultDirectory;
        var dlg = new OpenFileDialog
        {
            Title = "Choose an EverQuest log file",
            Filter = "EverQuest logs (eqlog*.txt)|eqlog*.txt|All files (*.*)|*.*",
            InitialDirectory = await Task.Run(() => Directory.Exists(dir)) ? dir : null,
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
        _followBaseline = DateTime.Now;
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        SetLocked(!_settings.Locked);
    }

    private void ResetSession_Click(object sender, RoutedEventArgs e)
    {
        _engine.ResetSession();
    }

    private void ResetOnLogin_Click(object sender, RoutedEventArgs e)
    {
        _settings.ResetOnLogin = ResetOnLoginBox.IsChecked == true;
        _engine.ResetOnLogin = _settings.ResetOnLogin;
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

    private IReadOnlyList<ActiveAlert> UpdateAlerts(DateTime now)
    {
        bool sound = false;
        while (_pendingAlerts.TryDequeue(out var a))
        {
            double seconds = a.Trigger.Seconds > 0 ? a.Trigger.Seconds : 8;
            _activeAlerts.Add(new ActiveAlert(a.Text, AlertBrush(a.Trigger.Color), now.AddSeconds(seconds), a.Trigger.Effect));
            sound |= a.Trigger.Sound;
        }

        _activeAlerts.RemoveAll(a => a.Expires <= now);
        if (_activeAlerts.Count > MaxAlerts)
        {
            _activeAlerts.RemoveRange(0, _activeAlerts.Count - MaxAlerts);
        }

        if (sound)
        {
            SystemSounds.Exclamation.Play();
        }

        return [.. _activeAlerts];
    }

    private Brush AlertBrush(string? color)
    {
        string key = string.IsNullOrWhiteSpace(color) ? AlertColors.Default : color.Trim();
        if (!_alertBrushes.TryGetValue(key, out var brush))
        {
            brush = AlertColors.ToBrush(key);
            _alertBrushes[key] = brush;
        }

        return brush;
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
            TriggersGrid.Items.Refresh();
            RebuildTriggers();
        }
        if (result.Message is not null)
        {
            TriggerStatusText.Text = string.IsNullOrEmpty(TriggerStatusText.Text)
                ? result.Message
                : TriggerStatusText.Text + " " + result.Message;
        }
    }

    private void RebuildTriggers()
    {
        var matcher = new TriggerMatcher(_triggers, _characterName);
        _matcher = matcher;
        TriggerStatusText.Text = matcher.Errors.Count == 0 ? "" : "Invalid regex (trigger skipped): " + string.Join("; ", matcher.Errors);
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
        var alerts = _matcher.Match([TestLineBox.Text]);
        double previewSeconds = alerts.Count == 0 ? 8 :
            alerts.Max(a => a.Trigger.Seconds > 0 ? a.Trigger.Seconds : 8);
        _alertTestVisibleUntil = DateTime.Now.AddSeconds(previewSeconds + 1);
        _overlayToggles["alerts"].IsChecked = true;
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
        foreach (var a in alerts)
        {
            _pendingAlerts.Enqueue(a);
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

        Func<string, bool> keep = _ => true;
        string filter = RawFilterBox.Text;
        string status = "";
        if (filter.Length > 0)
        {
            if (RegexFilterBox.IsChecked == true)
            {
                if (TriggerMatcher.TryCompile(filter, _characterName, out var regex, out string? error))
                {
                    keep = raw =>
                    {
                        string message = LogLine.TryParse(raw, out var l) ? l.Message : raw;
                        try
                        {
                            return regex.IsMatch(message);
                        }
                        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                        {
                            return false;
                        }
                    };
                }
                else
                {
                    status = $"Invalid regex: {error}  ";
                }
            }
            else
            {
                keep = raw => raw.Contains(filter, StringComparison.OrdinalIgnoreCase);
            }
        }

        var shown = snapshot.Where(keep).TakeLast(RawDisplayMax).ToArray();
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

        RawStatusText.Text = status + $"Showing {shown.Length:N0} of the last {snapshot.Length:N0} lines" +
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

    private void MakeTriggerFromSelection()
    {
        if (RawLinesList.SelectedItem is not string raw)
        {
            RawStatusText.Text = "Select a line first.";
            return;
        }

        string message = LogLine.TryParse(raw, out var line) ? line.Message : raw.Trim();
        var trigger = new AlertTrigger
        {
            Name = message.Length > 30 ? message[..30] : message,
            Pattern = TriggerMatcher.PatternFromMessage(message, _characterName),
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

        string? zone = _engine.SpawnZone;
        bool all = ShowAllMobsBox.IsChecked == true;
        var list = _engine.SpawnCandidates()
            .Where(c => all || c.LikelyNamed || c.Info?.Tracked == true)
            .OrderByDescending(c => c.Info?.Tracked == true)
            .ThenBy(c => c.Mob, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            _engine.SetSpawnTracked(row.Zone, row.Mob, box.IsChecked == true);
            CheckSpawnWiki();
            _ = SaveQuietlyAsync(_engine.SaveSpawnsAsync(AppSettings.SpawnsPath));
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

        _engine.RelearnSpawn(row.Zone, row.Mob);
        CheckSpawnWiki();
        RefreshSpawns(force: true);
        _ = SaveQuietlyAsync(_engine.SaveSpawnsAsync(AppSettings.SpawnsPath));
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
            _engine.SetSpawnOverride(row.Zone, row.Mob, null);
        }
        else if (SpawnStore.TryParseDuration(text, out double seconds))
        {
            _engine.SetSpawnOverride(row.Zone, row.Mob, seconds);
        }
        else
        {
            e.Cancel = true;
            MessageBox.Show(_dock, "Enter a respawn time as seconds (960) or minutes:seconds (16:00), or leave it empty to use the learned time or wiki fallback.", "Override");
            return;
        }

        _spawnsSignature = "";
        _ = SaveQuietlyAsync(_engine.SaveSpawnsAsync(AppSettings.SpawnsPath));
    }

    /// <summary>Settings save for UI handlers that can't await; <paramref name="onError"/> receives the failure message.</summary>
    private async void SaveSettingsInBackground(Action<string>? onError = null)
    {
        try
        {
            await _settings.SaveAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onError?.Invoke(ex.Message);
        }
    }

    /// <summary>For small learned-data saves; a failed write stays dirty and the periodic save retries it.</summary>
    private static async Task SaveQuietlyAsync(Task save)
    {
        try
        {
            await save;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
