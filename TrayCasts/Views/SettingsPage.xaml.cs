using Microsoft.UI.Xaml;
using System.Xml;
using Microsoft.UI.Xaml.Controls;
using TrayCasts.Playback;
using TrayCasts.Services;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel;

namespace TrayCasts.Views;

/// <summary>
/// Settings, shown inside the flyout: there are only a few, all inline, so a separate window
/// would cost more than it gives. Changes apply immediately; the player reads them on each skip.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly Action _goBack;
    private CancellationTokenSource? _importCts;
    private bool _loading = true;

    public SettingsPage(Action goBack)
    {
        _goBack = goBack;
        InitializeComponent();
        VersionText.Text = $"{App.DisplayName} {AppVersion.Current}";

        FillSeconds(SkipBackCombo, PlaybackPolicy.SkipBackChoices, SettingsService.SkipBackSeconds);
        FillSeconds(SkipForwardCombo, PlaybackPolicy.SkipForwardChoices, SettingsService.SkipForwardSeconds);
        AutoPlayToggle.IsOn = SettingsService.AutoPlayNext;
        BadgeToggle.IsOn = SettingsService.ShowNewEpisodeBadge;
        ArtToggle.IsOn = SettingsService.ShowArtOnTrayIcon;
        _loading = false;
    }

    /// <summary>Re-reads state that can change outside the app (Windows Settings > Startup).</summary>
    public void OnShown()
    {
        _ = LoadStartupStateAsync();
        DispatcherQueue.TryEnqueue(() => BackButton.Focus(FocusState.Programmatic));
    }

    /// <summary>The flyout hid: an import in progress stops, keeping the shows added so far.</summary>
    public void OnHidden() => _importCts?.Cancel();

    private static void FillSeconds(ComboBox combo, IReadOnlyList<int> choices, int selected)
    {
        combo.Items.Clear();
        foreach (int seconds in choices)
        {
            combo.Items.Add(new ComboBoxItem { Content = $"{seconds} seconds", Tag = seconds });
        }

        combo.SelectedIndex = Math.Max(0, choices.ToList().IndexOf(selected));
    }

    private async Task LoadStartupStateAsync() => ShowStartupState(await StartupService.GetStateAsync());

    private void ShowStartupState(StartupTaskState? state)
    {
        _loading = true;
        StartupToggle.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

        // The user (Task Manager, Settings > Apps > Startup) or policy has the final say; the app
        // can't override it, so say where to change it instead of offering a dead toggle.
        StartupToggle.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
        StartupDescription.Text = state switch
        {
            StartupTaskState.DisabledByUser => "Turned off in Settings > Apps > Startup. Turn it on there.",
            StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Managed by your organization.",
            null => "Only available when the app is installed.",
            _ => "Keep your podcasts a click away after you sign in.",
        };
        _loading = false;
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ShowStartupState(await StartupService.SetEnabledAsync(StartupToggle.IsOn));
    }

    private void SkipBackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && SkipBackCombo.SelectedItem is ComboBoxItem { Tag: int seconds })
        {
            SettingsService.SkipBackSeconds = seconds;
        }
    }

    private void SkipForwardCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && SkipForwardCombo.SelectedItem is ComboBoxItem { Tag: int seconds })
        {
            SettingsService.SkipForwardSeconds = seconds;
        }
    }

    private void AutoPlayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            SettingsService.AutoPlayNext = AutoPlayToggle.IsOn;
        }
    }

    private void BadgeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            App.Current.SetNewEpisodeBadge(BadgeToggle.IsOn);
        }
    }

    private void ArtToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            App.Current.SetArtOnTrayIcon(ArtToggle.IsOn);
        }
    }

    // ---- OPML ----------------------------------------------------------------------------

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        FileOpenPicker picker = new(XamlRoot.ContentIslandEnvironment.AppWindowId);
        picker.FileTypeFilter.Add(".opml");
        picker.FileTypeFilter.Add(".xml");
        if (await picker.PickSingleFileAsync() is not { } file)
        {
            return;
        }

        _importCts?.Cancel();
        using CancellationTokenSource cts = new();
        _importCts = cts;
        SetOpmlBusy(true);
        ShowOpmlStatus("Reading the file…", progress: true);
        try
        {
            void ShowChecked(int done) => OpmlStatusText.Text = $"Adding shows… {done} checked";
            ImportOutcome outcome = await LibraryService.ImportOpmlAsync(file.Path, ShowChecked, cts.Token);
            ShowOpmlStatus(ImportSummary(outcome), progress: false);
        }
        catch (OperationCanceledException)
        {
            ShowOpmlStatus("Import stopped. The shows added so far are kept.", progress: false);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            ShowOpmlStatus("That file couldn't be read as OPML.", progress: false);
        }
        finally
        {
            if (ReferenceEquals(_importCts, cts))
            {
                _importCts = null;
            }

            SetOpmlBusy(false);
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        FileSavePicker picker = new(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedFileName = "Tray Casts shows",
            DefaultFileExtension = ".opml",
        };
        picker.FileTypeChoices.Add("OPML", [".opml"]);
        if (await picker.PickSaveFileAsync() is not { } file)
        {
            return;
        }

        SetOpmlBusy(true);
        try
        {
            int count = await LibraryService.ExportOpmlAsync(file.Path);
            ShowOpmlStatus(count == 1 ? "Exported 1 show." : $"Exported {count} shows.", progress: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowOpmlStatus("Couldn't save the file there.", progress: false);
        }
        finally
        {
            SetOpmlBusy(false);
        }
    }

    private static string ImportSummary(ImportOutcome outcome)
    {
        List<string> parts = [outcome.Added == 1 ? "Added 1 show." : $"Added {outcome.Added} shows."];
        if (outcome.AlreadyFollowed > 0)
        {
            parts.Add($"{outcome.AlreadyFollowed} already followed.");
        }

        if (outcome.Failed > 0)
        {
            parts.Add(outcome.Failed == 1 ? "1 couldn't be reached." : $"{outcome.Failed} couldn't be reached.");
        }

        return string.Join(" ", parts);
    }

    private void SetOpmlBusy(bool busy)
    {
        ImportButton.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy;
    }

    private void ShowOpmlStatus(string text, bool progress)
    {
        OpmlStatusPanel.Visibility = Visibility.Visible;
        ImportProgress.IsIndeterminate = progress;
        ImportProgress.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
        OpmlStatusText.Text = text;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _goBack();
}
