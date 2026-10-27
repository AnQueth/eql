using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
    public required string Spell { get; init; }

    public required string Learned { get; init; }

    public required int Samples { get; init; }

    public string Override { get; set; } = "";
}

public sealed class SpawnRow
{
    public required string Zone { get; init; }

    public required string Mob { get; init; }

    public required int Kills { get; init; }

    public required string LastKilled { get; init; }

    public required string Learned { get; init; }

    public required int Samples { get; init; }

    public string Override { get; set; } = "";

    public bool Tracked { get; set; }
}

public partial class MainWindow : Window
{
    private const int LockHotkeyId = 0x4C31;

    /// <summary>If the newest log line is older than this, we are viewing an old log and use log time as "now".</summary>
    private static readonly TimeSpan ReplayThreshold = TimeSpan.FromMinutes(30);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SpellDurationStore _durations = SpellDurationStore.Load(AppSettings.DurationsPath);
    private readonly Dictionary<string, OverlayWindow> _overlays = [];
    private readonly Dictionary<string, CheckBox> _overlayToggles = [];
    private readonly Dictionary<string, CheckBox> _dockToggles = [];
    private DockWindow _dock = null!;
    private readonly HashSet<ChatChannel> _chatChannels;
    private readonly ObservableCollection<DurationRow> _durationRows = [];
    private readonly SpawnStore _spawnStore = SpawnStore.Load(AppSettings.SpawnsPath);
    private readonly MoteStore _moteStore = MoteStore.Load(AppSettings.MotesPath);
    private readonly ObservableCollection<SpawnRow> _spawnRows = [];
    private string _spawnsSignature = "";
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
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
    private bool _loading = true;

    public MainWindow()
    {
        InitializeComponent();
        InitializeThemes();
        InitializeLootQuests();
        InitializeQuestInventory();
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
        _gameVisible = GameWindowWatcher.IsGameVisible();
        OpacitySlider.Value = _settings.BackgroundOpacity;
        FontSlider.Value = _settings.FontSize;
        ResetOnLoginBox.IsChecked = _settings.ResetOnLogin;

        CreateOverlays();
        CreateChatToggles();
        _loading = false;

        Application.Current.SessionEnding += (_, _) => SaveAll();
        _renderTimer.Tick += (_, _) => Render();
        _houseTimer.Tick += (_, _) => Housekeeping();
        _gameTimer.Tick += (_, _) => CheckGameVisible();

        // This window is never shown: its UI lives in the dock window's side panel, and the dock window is the
        // app's main window. A hidden handle is still needed for the Ctrl+Shift+L hotkey.
        Content = null;
        _dock.SetSidePanel(SettingsRoot);
        _dock.Closing += (_, _) => Shutdown();
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
    }

    private void CreateOverlays()
    {
        var work = SystemParameters.WorkArea;
        _dock = new DockWindow(_settings.DockWindow);
        _dock.OrderChanged += order => _settings.DockWindow.Order = [.. order];
        for (int i = 0; i < OverlayPresenters.All.Count; i++)
        {
            var def = OverlayPresenters.All[i];
            var panel = new OverlayPanel(def.Key, def.Caption, def.AutoScroll) { FontSize = _settings.FontSize };
            panel.SetBackgroundOpacity(_settings.BackgroundOpacity);
            if (def.Key == "quests") ConfigureQuestToolbar(panel);
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
            panel.BreakdownRequested += breakdown => new AbilityBreakdownWindow(breakdown) { Owner = _dock }.Show();
            panel.DockToggleRequested += pnl =>
                Dispatcher.BeginInvoke(() => SetDocked(pnl.Key, !_settings.Overlays[pnl.Key].Docked));
            var w = new OverlayWindow(panel);
            if (!_settings.Overlays.TryGetValue(def.Key, out var p))
            {
                p = DefaultPlacement(def, i, work);
                _settings.Overlays[def.Key] = p;
            }

            w.Left = p.Left;
            w.Top = p.Top;
            w.Width = p.Width;
            w.Height = p.Height;
            w.SetLocked(_settings.Locked);
            EnsureOnScreen(w);
            _overlays[def.Key] = w;

            var toggle = new CheckBox { Content = def.Caption, IsChecked = p.Visible, Tag = def.Key, Margin = new Thickness(0, 2, 4, 2) };
            toggle.Click += (_, _) => SetOverlayVisible(def.Key, toggle.IsChecked == true);
            _overlayToggles[def.Key] = toggle;
            var docked = new CheckBox
            {
                Content = "docked",
                IsChecked = p.Docked,
                FontSize = 11,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 16, 2),
                ToolTip = "Show this panel in the docked-panels window (e.g. on a second monitor) instead of as an overlay",
            };
            docked.Click += (_, _) => SetDocked(def.Key, docked.IsChecked == true);
            _dockToggles[def.Key] = docked;
            OverlayToggles.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { toggle, docked } });

            ApplyMode(def.Key);
        }
    }

    /// <summary>Puts an overlay's panel in its floating window or in the dock window, per its settings.</summary>
    private void ApplyMode(string key)
    {
        var p = _settings.Overlays[key];
        var w = _overlays[key];
        if (p.Docked)
        {
            w.Hide();
            w.DetachPanel();
            if (p.Visible)
            {
                _dock.AddPanel(w.Panel, p, _settings.DockWindow.Order);
            }
            else
            {
                _dock.RemovePanel(key);
            }

            return;
        }

        _dock.RemovePanel(key);
        w.AttachPanel();
        if (p.Visible && FloatingAllowed)
        {
            w.Show();
            w.SetLocked(_settings.Locked);
        }
        else
        {
            w.Hide();
        }
    }

    private bool FloatingAllowed => !_settings.OnlyWhenGameVisible || _gameVisible;

    private void CheckGameVisible()
    {
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
        return p.Visible && (p.Docked || _overlays[key].IsVisible);
    }

    private void SetDocked(string key, bool docked)
    {
        _settings.Overlays[key].Docked = docked;
        _dockToggles[key].IsChecked = docked;
        ApplyMode(key);
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
        if (def.Key == "alerts")
        {
            // Alerts go near the top middle of the screen, where they're hard to miss.
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
            Docked = def.Key == "quests",
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
            var box = new CheckBox { Content = channel.ToString(), IsChecked = _chatChannels.Contains(channel) };
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true) _chatChannels.Add(channel);
                else _chatChannels.Remove(channel);
                _settings.ChatChannels = [.. _chatChannels];
            };
            ChatToggles.Children.Add(box);
        }
    }

    private void SetOverlayVisible(string key, bool visible)
    {
        _settings.Overlays[key].Visible = visible;
        ApplyMode(key);
        if (visible && _settings.Overlays[key].Docked)
        {
            ShowDock();
        }
    }

    private async Task OpenInitialLogAsync()
    {
        string[] args = Environment.GetCommandLineArgs();
        string? path = args.Skip(1).FirstOrDefault(File.Exists);
        if (path is null && _settings.LogPath is { } saved && File.Exists(saved))
        {
            path = saved;
        }

        // When following the newest log, prefer whatever is newest in the folder at startup.
        if (path is null || _settings.FollowNewest)
        {
            string dir = (path is not null ? Path.GetDirectoryName(path) : null) ?? _settings.LogDirectory ?? LogFileLocator.DefaultDirectory;
            if (args.Length <= 1 && LogFileLocator.FindNewest(dir) is { } newest)
            {
                path = newest.Path;
            }
        }

        if (path is null)
        {
            StatusText.Text = $"No eqlog files found in {LogFileLocator.DefaultDirectory}. Use Browse to pick a log file. " +
                              "(Turn on logging in game with /log on.)";
            return;
        }

        await SwitchToAsync(path);
    }

    private async Task SwitchToAsync(string path)
    {
        await StopTailingAsync();

        var info = LogFileLocator.Describe(path);
        _engine = new LogEngine(_durations, _spawnStore, _moteStore)
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
        lock (_progressGate) _progress = default;
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
                    foreach (var alert in _matcher.Match(lines)) _pendingAlerts.Enqueue(alert);
                }
            },
            onProgress: p =>
            {
                live |= p.CaughtUp;
                lock (_progressGate) _progress = p;
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

    private void Render()
    {
        var real = DateTime.Now;
        var last = _engine.LastLogTime;
        bool replay = last is DateTime l && real - l > ReplayThreshold;
        var now = replay ? last!.Value : real;
        var snap = _engine.Snapshot(now);
        RefreshQuestItems(snap);
        PumpQuestLoot();
        RefreshSkyInventory();
        var view = new OverlayView(_chatChannels, UpdateAlerts(real), _settings.LootGrouping);
        if (LogLinesTab.IsSelected)
        {
            RefreshRawLines();
        }

        foreach (var def in OverlayPresenters.All)
        {
            if (!IsShowing(def.Key))
            {
                continue;
            }

            var content = def.Key == "quests" ? QuestOpportunities() : def.Render(snap, _engine, view);
            _overlays[def.Key].Panel.Update(content.Title, content.Subtitle, content.Rows);
        }

        TailProgress p;
        lock (_progressGate) p = _progress;
        HistoryProgress.Value = p.TotalBytes > 0 ? (double)p.BytesRead / p.TotalBytes : 0;
        HistoryProgress.Visibility = p.CaughtUp ? Visibility.Collapsed : Visibility.Visible;

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
        StatusText.Text = $"{phase}: {snap.CharacterName}{zone} - {snap.LinesProcessed:N0} lines, {when}{replayNote}";
    }

    private void Housekeeping()
    {
        _ = CheckInventoryAsync();
        _houseTicks++;
        foreach (var w in _overlays.Values)
        {
            if (w.IsVisible)
            {
                w.ReassertTopmost();
            }
        }

        if (SpawnsTab.IsSelected)
        {
            RefreshSpawns();
        }

        if (_houseTicks % 3 == 0)
        {
            RefreshDurations();
            CheckForNewerLog();
        }

        if (_houseTicks % 5 == 0)
        {
            SaveAll();
        }
    }

    private void CheckForNewerLog()
    {
        if (!_settings.FollowNewest || _currentFile is null)
        {
            return;
        }

        string? dir = Path.GetDirectoryName(_currentFile);
        if (dir is null || LogFileLocator.FindNewest(dir) is not { } newest)
        {
            return;
        }

        // Another character's log was written after we started reading this one: they logged in.
        if (!string.Equals(newest.Path, _currentFile, StringComparison.OrdinalIgnoreCase) && newest.LastWriteTime > _followBaseline)
        {
            _ = SwitchToAsync(newest.Path);
        }
    }

    private void RefreshDurations()
    {
        if (DurationsGrid.IsKeyboardFocusWithin)
        {
            return;
        }

        var list = _engine.SpellDurations();
        string signature = string.Join('|', list.Select(d => $"{d.Spell}:{d.Samples}:{d.LearnedSeconds:F0}:{d.OverrideSeconds}"));
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
                Spell = d.Spell,
                Learned = d.LearnedSeconds is double s ? OverlayPresenters.Clock(s) : "-",
                Samples = d.Samples,
                Override = d.OverrideSeconds?.ToString("0.#", CultureInfo.CurrentCulture) ?? "",
            });
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
        else if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double seconds) && seconds > 0)
        {
            _engine.SetSpellDurationOverride(row.Spell, seconds);
        }
        else
        {
            e.Cancel = true;
            MessageBox.Show(_dock, "Enter a duration in seconds, or leave it empty to use the learned value.", "Override");
            return;
        }

        _durationsSignature = "";
        SaveDurations();
    }

    /// <summary>Saves window layout, settings and everything learned. Runs every few seconds, on close and on Windows logoff.</summary>
    private void SaveAll()
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
        try
        {
            _settings.Save();
        }
        catch (IOException)
        {
        }

        SaveDurations();
        SaveSpawns();
        SaveMotes();
        SaveQuestInventory();
    }

    private void SaveDurations()
    {
        try
        {
            _engine.SaveSpellDurations(AppSettings.DurationsPath);
        }
        catch (IOException)
        {
            // Try again on the next save.
        }
    }

    private void SetLocked(bool locked)
    {
        _settings.Locked = locked;
        LockBox.IsChecked = locked;
        foreach (var w in _overlays.Values)
        {
            w.SetLocked(locked);
        }
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
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == LockHotkeyId)
        {
            SetLocked(!_settings.Locked);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private bool _shutDown;

    /// <summary>Runs when the dock (main) window closes: saves everything and closes the overlays.</summary>
    private void Shutdown()
    {
        if (_shutDown)
        {
            return;
        }

        _shutDown = true;
        _wikiCts.Cancel();
        _renderTimer.Stop();
        _houseTimer.Stop();
        _gameTimer.Stop();
        NativeMethods.UnregisterHotKey(new WindowInteropHelper(this).Handle, LockHotkeyId);

        SaveAll();
        _cts?.Cancel();
        foreach (var w in _overlays.Values)
        {
            w.Close();
        }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        string dir = _currentFile is not null ? Path.GetDirectoryName(_currentFile)! : LogFileLocator.DefaultDirectory;
        var dlg = new OpenFileDialog
        {
            Title = "Choose an EverQuest log file",
            Filter = "EverQuest logs (eqlog*.txt)|eqlog*.txt|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(dir) ? dir : null,
        };
        if (dlg.ShowDialog(_dock) == true)
        {
            await SwitchToAsync(dlg.FileName);
        }
    }

    private void FollowNewest_Click(object sender, RoutedEventArgs e)
    {
        _settings.FollowNewest = FollowNewestBox.IsChecked == true;
        _followBaseline = DateTime.Now;
    }

    private void Lock_Click(object sender, RoutedEventArgs e) => SetLocked(LockBox.IsChecked == true);

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.BackgroundOpacity = e.NewValue;
        foreach (var w in _overlays.Values) w.Panel.SetBackgroundOpacity(e.NewValue);
    }

    private void Font_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.FontSize = e.NewValue;
        foreach (var w in _overlays.Values) w.Panel.FontSize = e.NewValue;
    }

    private void ResetSession_Click(object sender, RoutedEventArgs e) => _engine.ResetSession();

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
            _activeAlerts.Add(new ActiveAlert(a.Text, AlertBrush(a.Trigger.Color), now.AddSeconds(seconds)));
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
        string key = string.IsNullOrWhiteSpace(color) ? "#FF5A5A" : color.Trim();
        if (!_alertBrushes.TryGetValue(key, out var brush))
        {
            try
            {
                brush = new BrushConverter().ConvertFromInvariantString(key) as Brush ?? Brushes.OrangeRed;
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException)
            {
                brush = Brushes.OrangeRed;
            }

            brush.Freeze();
            _alertBrushes[key] = brush;
        }

        return brush;
    }

    private void RebuildTriggers()
    {
        var matcher = new TriggerMatcher(_triggers, _characterName);
        _matcher = matcher;
        TriggerStatusText.Text = matcher.Errors.Count == 0 ? "" : "Invalid regex (trigger skipped): " + string.Join("; ", matcher.Errors);
        _settings.Triggers = [.. _triggers];
        try
        {
            _settings.Save();
        }
        catch (IOException)
        {
        }
    }

    private void Triggers_RowEditEnding(object? sender, DataGridRowEditEndingEventArgs e)
    {
        // The edit is committed after this event; rebuild once it has been applied.
        Dispatcher.BeginInvoke(RebuildTriggers, DispatcherPriority.Background);
    }

    private void TriggerCheck_Click(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(RebuildTriggers, DispatcherPriority.Background);

    private void TestTrigger_Click(object sender, RoutedEventArgs e)
    {
        TriggersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        RebuildTriggers();
        var alerts = _matcher.Match([TestLineBox.Text]);
        foreach (var a in alerts)
        {
            _pendingAlerts.Enqueue(a);
        }

        if (alerts.Count == 0)
        {
            TriggerStatusText.Text = "No enabled trigger matched that line." + (TriggerStatusText.Text.Length > 0 ? " " + TriggerStatusText.Text : "");
        }
        else if (!IsShowing("alerts") && _settings.Overlays["alerts"].Visible && !FloatingAllowed)
        {
            TriggerStatusText.Text = $"Matched: {string.Join(", ", alerts.Select(a => a.Trigger.Name))}. Overlays are hidden because EverQuest isn't visible.";
        }
        else if (!IsShowing("alerts"))
        {
            TriggerStatusText.Text = $"Matched: {string.Join(", ", alerts.Select(a => a.Trigger.Name))}. Turn on the Alerts overlay to see it.";
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
                        try { return regex.IsMatch(message); }
                        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return false; }
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
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }

        return null;
    }

    private void RawLines_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => MakeTriggerFromSelection();

    private void MakeTrigger_Click(object sender, RoutedEventArgs e) => MakeTriggerFromSelection();

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
        Dispatcher.BeginInvoke(() =>
        {
            TriggersGrid.SelectedItem = trigger;
            TriggersGrid.ScrollIntoView(trigger);
            var textColumn = TriggersGrid.Columns.First(c => (c.Header as string) == "Alert text");
            TriggersGrid.CurrentCell = new DataGridCellInfo(trigger, textColumn);
            TriggersGrid.Focus();
            TriggersGrid.BeginEdit();
        }, DispatcherPriority.Background);
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
        string signature = zone + "|" + all + "|" + string.Join('|', list.Select(c => $"{c.Mob}:{c.Kills}:{c.LastKilled:O}:{c.Info?.Samples}:{c.Info?.LearnedSeconds}:{c.Info?.OverrideSeconds}:{c.Info?.Tracked}"));
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
                Samples = c.Info?.Samples ?? 0,
                Override = c.Info?.OverrideSeconds is double o ? OverlayPresenters.Clock(o) : "",
                Tracked = c.Info?.Tracked == true,
            });
        }
    }

    private void ShowAllMobs_Click(object sender, RoutedEventArgs e) => RefreshSpawns(force: true);

    private void SpawnTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SpawnRow row } && sender is CheckBox box)
        {
            _engine.SetSpawnTracked(row.Zone, row.Mob, box.IsChecked == true);
            SaveSpawns();
        }
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
            MessageBox.Show(_dock, "Enter a respawn time as seconds (960) or minutes:seconds (16:00), or leave it empty to use the learned value.", "Override");
            return;
        }

        _spawnsSignature = "";
        SaveSpawns();
    }

    private void SaveSpawns()
    {
        try
        {
            _engine.SaveSpawns(AppSettings.SpawnsPath);
        }
        catch (IOException)
        {
            // Try again on the next save.
        }
    }

    private void SaveMotes()
    {
        try
        {
            _engine.SaveMotes(AppSettings.MotesPath);
        }
        catch (IOException)
        {
            // Try again on the next save.
        }
    }
}
