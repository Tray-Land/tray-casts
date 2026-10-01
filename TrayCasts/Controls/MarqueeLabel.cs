using CommunityToolkit.Labs.WinUI.MarqueeTextRns;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace TrayCasts.Controls;

/// <summary>
/// One line of text that scrolls when it's clipped. It rests on the trimmed text, scrolls the whole
/// text past once (the toolkit's looping marquee, which ends where it started), then rests again
/// before the next pass. Text that fits never moves. Nothing scrolls while <see cref="IsActive"/> is
/// false (the flyout is hidden) or when the user has turned off animation effects in Windows.
/// Font and foreground come from the control's own properties, which both lines inherit.
/// </summary>
public sealed partial class MarqueeLabel : UserControl
{
    private static readonly TimeSpan RestDuration = TimeSpan.FromSeconds(2.5);
    private const double PixelsPerSecond = 32;

    private readonly TextBlock _trimmed = new()
    {
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
    };

    private readonly MarqueeText _marquee = new()
    {
        Behavior = MarqueeBehavior.Looping,
        Direction = MarqueeDirection.Left,
        RepeatBehavior = new RepeatBehavior(1),
        Speed = PixelsPerSecond,
        Visibility = Visibility.Collapsed,
    };

    // Measures the full text off-tree, so the check never disturbs the visible line's layout.
    private readonly TextBlock _ruler = new() { TextWrapping = TextWrapping.NoWrap };
    private readonly DispatcherQueueTimer _rest;
    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;
    private bool _isActive = true;

    public MarqueeLabel()
    {
        IsTabStop = false;

        // The trimmed line carries the text for screen readers; the scrolling copy is decoration.
        AutomationProperties.SetAccessibilityView(_marquee, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        _marquee.MarqueeCompleted += Marquee_Completed;
        _marquee.MarqueeBegan += Marquee_Began;

        _rest = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _rest.Interval = RestDuration;
        _rest.IsRepeating = false;
        _rest.Tick += Rest_Tick;

        Content = new Grid { Children = { _trimmed, _marquee } };
        SizeChanged += OnSizeChanged;
        Unloaded += (_, _) => Rest();
    }

    public string Text
    {
        get => _trimmed.Text;
        set
        {
            if (_trimmed.Text == value)
            {
                return;
            }

            _trimmed.Text = value;
            _marquee.Text = value;
            Restart();
        }
    }

    public TextAlignment TextAlignment
    {
        get => _trimmed.TextAlignment;
        set => _trimmed.TextAlignment = value;
    }

    /// <summary>False stops any scrolling and keeps the text at rest until it's true again.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                Restart();
            }
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.PreviousSize.Width != e.NewSize.Width)
        {
            Restart();
        }
    }

    /// <summary>Back to the trimmed text, then a rest before the first pass if the text is clipped.</summary>
    private void Restart()
    {
        Rest();
        if (CanScroll())
        {
            _rest.Start();
        }
    }

    private void Rest()
    {
        _rest.Stop();
        _marquee.StopMarquee();
        _marquee.Visibility = Visibility.Collapsed;
        _trimmed.Opacity = 1;
    }

    private void Rest_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!CanScroll())
        {
            return;
        }

        // The trimmed line stays in layout, so the control keeps its size while the marquee runs.
        // Collapsing it would resize a centered label, and the resize would cancel the pass.
        _trimmed.Opacity = 0;
        _marquee.Visibility = Visibility.Visible;

        // The first time, the template isn't applied yet; the marquee starts itself once it's sized.
        _marquee.StartMarquee();
    }

    private void Marquee_Completed(object? sender, EventArgs e) => Restart();

    // The marquee also starts itself whenever it's resized; only a pass this control began may run.
    private void Marquee_Began(object? sender, EventArgs e)
    {
        if (_marquee.Visibility == Visibility.Collapsed)
        {
            _marquee.StopMarquee();
        }
    }

    // Clipped text takes all the width it's offered. The trimmed line alone would size to its
    // ellipsized text, a little narrower than the marquee, and a centered label would then change
    // size when a pass starts, which restarts it.
    protected override Size MeasureOverride(Size availableSize)
    {
        Size desired = base.MeasureOverride(availableSize);
        return IsClipped(availableSize.Width) ? new Size(availableSize.Width, desired.Height) : desired;
    }

    private bool CanScroll() =>
        _isActive && _animationsEnabled && IsLoaded && IsClipped(ActualWidth);

    private bool IsClipped(double available)
    {
        if (available <= 0 || double.IsInfinity(available) || string.IsNullOrEmpty(_trimmed.Text))
        {
            return false;
        }

        _ruler.Text = _trimmed.Text;
        _ruler.FontFamily = FontFamily;
        _ruler.FontSize = FontSize;
        _ruler.FontWeight = FontWeight;
        _ruler.FontStyle = FontStyle;
        _ruler.FontStretch = FontStretch;
        _ruler.CharacterSpacing = CharacterSpacing;
        _ruler.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _ruler.DesiredSize.Width > available + 0.5;
    }
}
