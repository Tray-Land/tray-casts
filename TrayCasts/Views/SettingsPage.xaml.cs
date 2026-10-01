using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayCasts.Playback;
using TrayCasts.Services;
using Windows.ApplicationModel;

namespace TrayCasts.Views;

/// <summary>
/// Settings, shown inside the flyout: there are only a few, all inline, so a separate window
/// would cost more than it gives. Changes apply immediately; the player reads them on each skip.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly Action _goBack;
    private bool _loading = true;

    public SettingsPage(Action goBack)
    {
        _goBack = goBack;
        InitializeComponent();
        VersionText.Text = $"{App.DisplayName} {AppVersion.Current}";

        FillSeconds(SkipBackCombo, PlaybackPolicy.SkipBackChoices, SettingsService.SkipBackSeconds);
        FillSeconds(SkipForwardCombo, PlaybackPolicy.SkipForwardChoices, SettingsService.SkipForwardSeconds);
        _loading = false;
    }

    /// <summary>Re-reads state that can change outside the app (Windows Settings > Startup).</summary>
    public void OnShown()
    {
        _ = LoadStartupStateAsync();
        DispatcherQueue.TryEnqueue(() => BackButton.Focus(FocusState.Programmatic));
    }

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

    private void BackButton_Click(object sender, RoutedEventArgs e) => _goBack();
}
