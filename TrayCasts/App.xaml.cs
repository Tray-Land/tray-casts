using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayCasts.Services;
using TrayCasts.Views;
using WinUIEx;

namespace TrayCasts;

/// <summary>
/// Tray-only application: no window at launch, just a notification-area icon that toggles the
/// flyout. Windows exist only while they're in use; the idle app is an icon and little else.
/// </summary>
public partial class App : Application
{
    public const string DisplayName = "Tray Casts";

    private const string MutexName = "Local\\TrayCasts_SingleInstance";
    private const string ShowEventName = "Local\\TrayCasts_ShowFlyout";
    private const int MaxTooltipLength = 127; // NOTIFYICONDATA.szTip limit

    // Startup leaves JIT and first-request state behind that the idle app never touches again.
    private static readonly TimeSpan StartupTrimDelay = TimeSpan.FromSeconds(30);

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private TrayIcon? _trayIcon;
    private readonly TrayBadge _badge = new();
    private NewEpisodeMonitor? _newEpisodes;
    private TrayFlyoutWindow? _flyout;
    private DispatcherQueue? _dispatcher;
    private bool _isExiting;

    public App()
    {
        InitializeComponent();

        // A tray app lives on after its windows close; only the Exit menu ends it.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
    }

    public static new App Current => (App)Application.Current;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance owns the tray icon: ask it to open its flyout, then quit.
            try
            {
                // Let the running instance take the foreground when it shows its flyout.
                Windows.Win32.PInvoke.AllowSetForegroundWindow(unchecked((uint)-1)); // ASFW_ANY
                using EventWaitHandle existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch
            {
                // The other instance may be shutting down; nothing to do.
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Exit();
            return;
        }

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => _dispatcher.TryEnqueue(ShowFlyout), null, Timeout.Infinite, executeOnlyOnce: false);

        InitializeTrayIcon();
        PlaybackService.Initialize(_dispatcher);
        PlaybackService.StateChanged += (_, _) => SetTrayStatus(PlaybackService.TrayStatus(MaxTooltipLength - DisplayName.Length - 1));

        _newEpisodes = new NewEpisodeMonitor(_dispatcher, () => _flyout?.IsPopupVisible == true, UpdateTrayIcon);
        if (SettingsService.ShowNewEpisodeBadge)
        {
            _newEpisodes.Start();
        }

        UpdateTrayIcon();

        // First run: show where the app lives instead of launching into silence.
        if (!SettingsService.HasLaunchedBefore)
        {
            SettingsService.HasLaunchedBefore = true;
            ShowFlyout();
        }
        else
        {
            MemoryService.ReleaseIdle(StartupTrimDelay);
        }
    }

    /// <summary>Sets the tray tooltip under the app name, e.g. "3 new" or null for just the name.</summary>
    public void SetTrayStatus(string? status)
    {
        if (_trayIcon is null)
        {
            return;
        }

        string tooltip = string.IsNullOrWhiteSpace(status) ? DisplayName : $"{DisplayName}\n{status}";
        _trayIcon.Tooltip = tooltip.Length > MaxTooltipLength ? tooltip[..MaxTooltipLength] : tooltip;
    }

    public void ShowFlyout()
    {
        TrayFlyoutWindow flyout = EnsureFlyout();
        flyout.HidePopup();
        flyout.ShowMainPage();
        flyout.ShowPopup();
        MarkNewEpisodesSeen();
    }

    /// <summary>Shows the flyout on its settings page.</summary>
    public void ShowSettings()
    {
        TrayFlyoutWindow flyout = EnsureFlyout();
        flyout.ShowSettingsPage();
        if (!flyout.IsPopupVisible)
        {
            flyout.ShowPopup();
        }
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new TrayIcon(1, TrayIconPath, DisplayName);
        _trayIcon.Selected += (_, _) =>
        {
            // A tray click always lands on the content, even if settings were left open.
            TrayFlyoutWindow flyout = EnsureFlyout();
            if (!flyout.IsPopupVisible)
            {
                flyout.ShowMainPage();
            }

            flyout.Toggle();
            if (flyout.IsPopupVisible && !flyout.IsShowingSettings)
            {
                MarkNewEpisodesSeen();
            }
        };
        _trayIcon.ContextMenu += TrayIcon_ContextMenu;
        _trayIcon.IsVisible = true;
        WindowPlacementService.SetTrayIcon(_trayIcon);
        SystemThemeService.Changed += (_, _) => _dispatcher?.TryEnqueue(UpdateTrayIcon);
    }

    /// <summary>Turns the background new-episode check and the tray badge on or off (Settings).</summary>
    public void SetNewEpisodeBadge(bool enabled)
    {
        SettingsService.ShowNewEpisodeBadge = enabled;
        SettingsService.NewEpisodeCount = 0;
        if (enabled)
        {
            _newEpisodes?.Start();
        }
        else
        {
            _newEpisodes?.Stop();
        }

        UpdateTrayIcon();
    }

    /// <summary>The flyout's episode list is on screen, so whatever was new has been seen.</summary>
    private void MarkNewEpisodesSeen()
    {
        if (SettingsService.NewEpisodeCount > 0)
        {
            SettingsService.NewEpisodeCount = 0;
            UpdateTrayIcon();
        }
    }

    private void UpdateTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _badge.Update(_trayIcon, TrayIconPath, SettingsService.ShowNewEpisodeBadge ? SettingsService.NewEpisodeCount : 0);
        }
    }

    // A white glyph disappears on a light taskbar, which follows the Windows theme, not the app theme.
    private static string TrayIconPath => Path.Combine(
        AppContext.BaseDirectory, "Assets", SystemThemeService.IsLight ? "AppIcon-dark.ico" : "AppIcon.ico");

    private void TrayIcon_ContextMenu(TrayIcon sender, TrayIconEventArgs args)
    {
        MenuFlyout menu = new();

        // Playback controls first while there's an episode loaded: the menu is the quickest remote.
        if (PlaybackService.Current is { } current)
        {
            bool playing = PlaybackService.IsPlaying;
            MenuFlyoutItem toggle = new()
            {
                Text = playing ? "Pause" : PlaybackService.PositionSeconds > 0 ? "Resume" : "Play",
                Icon = new FontIcon { Glyph = playing ? "" : "" },
            };
            ToolTipService.SetToolTip(toggle, current.Episode.Title);
            toggle.Click += (_, _) => PlaybackService.TogglePlayPause();
            MenuFlyoutItem back = new()
            {
                Text = $"Back {SettingsService.SkipBackSeconds} seconds",
                Icon = new FontIcon { Glyph = "" },
            };
            back.Click += (_, _) => PlaybackService.SkipBack();
            MenuFlyoutItem forward = new()
            {
                Text = $"Forward {SettingsService.SkipForwardSeconds} seconds",
                Icon = new FontIcon { Glyph = "" },
            };
            forward.Click += (_, _) => PlaybackService.SkipForward();

            menu.Items.Add(toggle);
            menu.Items.Add(back);
            menu.Items.Add(forward);
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        MenuFlyoutItem open = new() { Text = $"Open {DisplayName}", Icon = new FontIcon { Glyph = "" } };
        open.Click += (_, _) => ShowFlyout();
        MenuFlyoutItem settings = new() { Text = "Settings", Icon = new FontIcon { Glyph = "" } };
        settings.Click += (_, _) => ShowSettings();
        MenuFlyoutItem exit = new() { Text = "Exit", Icon = new FontIcon { Glyph = "" } };
        exit.Click += (_, _) => ExitApp();

        menu.Items.Add(open);
        menu.Items.Add(settings);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exit);
        args.Flyout = menu;
    }

    private TrayFlyoutWindow EnsureFlyout()
    {
        if (_flyout is null)
        {
            // The flyout closes itself after staying hidden for a while; the next open builds a new one.
            _flyout = new TrayFlyoutWindow();
            _flyout.Closed += (_, _) =>
            {
                _flyout = null;
                ReleaseIdleResourcesIfNoWindows();
            };
        }

        return _flyout;
    }

    private void ReleaseIdleResourcesIfNoWindows()
    {
        if (_flyout is null && !_isExiting)
        {
            MemoryService.ReleaseIdle();
        }
    }

    private void ExitApp()
    {
        _isExiting = true;
        PlaybackService.Shutdown();
        _flyout?.CloseWindow();

        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _newEpisodes?.Stop();
        _badge.Release();

        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Exit();
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "TrayCasts-crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // Never throw from the crash logger.
        }
    }
}
