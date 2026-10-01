using Microsoft.UI.Dispatching;

namespace TrayCasts.Services;

/// <summary>
/// Checks every feed hourly while no flyout is showing, and adds what's new to
/// <see cref="SettingsService.NewEpisodeCount"/> for the tray badge.
/// </summary>
/// <remarks>
/// The second documented exception to "no network while hidden", because the tray icon itself
/// shows the result. It's opt-in (<see cref="SettingsService.ShowNewEpisodeBadge"/>), and cheap: one
/// conditional request per show an hour, most answered 304, then the library is unloaded again.
/// While the flyout is visible its own poller refreshes, and what it finds the user can already see.
/// </remarks>
internal sealed class NewEpisodeMonitor
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly DispatcherQueueTimer _timer;
    private readonly Func<bool> _isFlyoutVisible;
    private readonly Action _countChanged;
    private bool _isChecking;

    public NewEpisodeMonitor(DispatcherQueue dispatcher, Func<bool> isFlyoutVisible, Action countChanged)
    {
        _isFlyoutVisible = isFlyoutVisible;
        _countChanged = countChanged;
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _ = CheckAsync();
    }

    /// <summary>Schedules the next check an hour after the last refresh (from here or the flyout).</summary>
    public void Start()
    {
        DateTimeOffset due = (SettingsService.LastRefreshedUtc ?? DateTimeOffset.MinValue) + Interval;
        TimeSpan wait = due - DateTimeOffset.UtcNow;
        Schedule(wait < FirstCheckDelay ? FirstCheckDelay : wait);
    }

    public void Stop() => _timer.Stop();

    private void Schedule(TimeSpan wait)
    {
        _timer.Stop();
        _timer.Interval = wait;
        _timer.Start();
    }

    private async Task CheckAsync()
    {
        if (_isChecking)
        {
            return;
        }

        _isChecking = true;
        try
        {
            if (!_isFlyoutVisible() && ConnectivityService.IsInternetAvailable && LibraryService.Library.Subscriptions.Count > 0)
            {
                using CancellationTokenSource timeout = new(Timeout);
                RefreshOutcome outcome = await LibraryService.RefreshAllAsync(timeout.Token);
                if (outcome.NewReleases > 0 && !_isFlyoutVisible())
                {
                    SettingsService.NewEpisodeCount += outcome.NewReleases;
                    _countChanged();
                }
            }
        }
        catch (Exception)
        {
            // Offline or every feed failed: try again next hour; the flyout reports problems.
        }
        finally
        {
            _isChecking = false;
            LibraryService.ReleaseIfUnviewed();
            if (SettingsService.ShowNewEpisodeBadge)
            {
                Schedule(Interval);
            }
        }
    }
}
