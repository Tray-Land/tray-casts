using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace TrayCasts.Views;

/// <summary>
/// Composition implicit animations for the flyout's content: panels slide and fade in when their
/// Visibility changes, and elements glide to wherever layout moves them. The animations are
/// templates, built once per page and shared by every element. Nothing animates when the user has
/// turned off animation effects in Windows.
/// </summary>
internal sealed class ImplicitMotion
{
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MoveDuration = TimeSpan.FromMilliseconds(320);

    private readonly Compositor _compositor;
    private readonly CompositionEasingFunction _decelerate;
    private readonly CompositionEasingFunction _accelerate;
    private readonly Dictionary<Vector3, ICompositionAnimationBase> _enters = [];
    private readonly Dictionary<Vector3, ICompositionAnimationBase> _exits = [];
    private ImplicitAnimationCollection? _moves;

    public ImplicitMotion(UIElement anyElement)
    {
        _compositor = ElementCompositionPreview.GetElementVisual(anyElement).Compositor;

        // Fluent's "fast out, slow in" for entrances and its mirror for exits.
        _decelerate = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        _accelerate = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0f), new Vector2(1f, 0.5f));
        IsEnabled = new UISettings().AnimationsEnabled;
    }

    public bool IsEnabled { get; }

    /// <summary>The next time <paramref name="element"/> appears, it slides in from
    /// <paramref name="from"/> and fades in; when it goes, it fades out drifting toward
    /// <paramref name="to"/>.</summary>
    public void SetEnterExit(UIElement element, Vector3 from, Vector3 to)
    {
        if (!IsEnabled)
        {
            return;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        ElementCompositionPreview.SetImplicitShowAnimation(element, Enter(from));
        ElementCompositionPreview.SetImplicitHideAnimation(element, Exit(to));
    }

    /// <summary>Shows and hides <paramref name="element"/> instantly, for a change something else
    /// is already animating.</summary>
    public static void ClearEnterExit(UIElement element)
    {
        ElementCompositionPreview.SetImplicitShowAnimation(element, null);
        ElementCompositionPreview.SetImplicitHideAnimation(element, null);
    }

    /// <summary>Layout moves of these elements jump, until <see cref="AnimateMoves"/> again.</summary>
    public static void SuspendMoves(IEnumerable<UIElement> elements)
    {
        foreach (UIElement element in elements)
        {
            ElementCompositionPreview.GetElementVisual(element).ImplicitAnimations = null;
        }
    }

    /// <summary>Layout moves of these elements animate instead of jumping.</summary>
    public void AnimateMoves(params UIElement[] elements)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (_moves is null)
        {
            Vector3KeyFrameAnimation move = _compositor.CreateVector3KeyFrameAnimation();
            move.Target = "Offset";
            move.InsertExpressionKeyFrame(1f, "this.FinalValue", _decelerate);
            move.Duration = MoveDuration;
            _moves = _compositor.CreateImplicitAnimationCollection();
            _moves["Offset"] = move;
        }

        foreach (UIElement element in elements)
        {
            ElementCompositionPreview.GetElementVisual(element).ImplicitAnimations = _moves;
        }
    }

    private ICompositionAnimationBase Enter(Vector3 from)
    {
        if (!_enters.TryGetValue(from, out ICompositionAnimationBase? animation))
        {
            ScalarKeyFrameAnimation fade = _compositor.CreateScalarKeyFrameAnimation();
            fade.Target = "Opacity";
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, _decelerate);
            fade.Duration = EnterDuration;

            Vector3KeyFrameAnimation slide = _compositor.CreateVector3KeyFrameAnimation();
            slide.Target = "Translation";
            slide.InsertKeyFrame(0f, from);
            slide.InsertKeyFrame(1f, Vector3.Zero, _decelerate);
            slide.Duration = EnterDuration;

            CompositionAnimationGroup group = _compositor.CreateAnimationGroup();
            group.Add(fade);
            group.Add(slide);
            _enters[from] = animation = group;
        }

        return animation;
    }

    private ICompositionAnimationBase Exit(Vector3 to)
    {
        if (!_exits.TryGetValue(to, out ICompositionAnimationBase? animation))
        {
            ScalarKeyFrameAnimation fade = _compositor.CreateScalarKeyFrameAnimation();
            fade.Target = "Opacity";
            fade.InsertKeyFrame(1f, 0f, _accelerate);
            fade.Duration = ExitDuration;

            Vector3KeyFrameAnimation slide = _compositor.CreateVector3KeyFrameAnimation();
            slide.Target = "Translation";
            slide.InsertKeyFrame(1f, to, _accelerate);
            slide.Duration = ExitDuration;

            CompositionAnimationGroup group = _compositor.CreateAnimationGroup();
            group.Add(fade);
            group.Add(slide);
            _exits[to] = animation = group;
        }

        return animation;
    }
}
