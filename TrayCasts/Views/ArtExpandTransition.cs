using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace TrayCasts.Views;

/// <summary>
/// The player card opening into the full now playing page, or the page closing back into it,
/// driven by one number: 0 is the card, 1 is the page. Every moving part is an expression on that
/// number, so a drag can scrub it directly and a release can animate it the rest of the way, and
/// both look the same.
/// </summary>
/// <remarks>
/// Both ends must already be laid out where they rest; this only moves visuals. While it opens
/// the art flies from the card's thumbnail to its full place, the card's header fades, the card's
/// surface grows up to fill the view, the page's details rise in, and the view underneath fades.
/// </remarks>
internal sealed class ArtExpandTransition
{
    private const float DetailsRise = 24;
    private const float LeavingLift = 16;

    private readonly Compositor _compositor;
    private readonly DispatcherQueue _dispatcher;
    private readonly CompositionPropertySet _progress;
    private readonly CompositionEasingFunction _settleEasing;
    private readonly List<Binding> _bindings = [];
    private CompositionRoundedRectangleGeometry? _surfaceEdge;
    private Visual? _thumbnail;
    private Action<bool>? _finished;
    private int _generation;
    private bool _startedOpen;

    public ArtExpandTransition(UIElement anyElement)
    {
        _compositor = ElementCompositionPreview.GetElementVisual(anyElement).Compositor;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _progress = _compositor.CreatePropertySet();
        _progress.InsertScalar("p", 0);
        _settleEasing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
    }

    /// <summary>True from <see cref="Begin"/> until the transition has settled at either end.</summary>
    public bool IsActive { get; private set; }

    /// <summary>True while following a drag, before <see cref="Settle"/>.</summary>
    public bool IsTracking { get; private set; }

    public float Progress { get; private set; }

    /// <summary>Binds the parts to the progress, starting at <paramref name="progress"/>: 0 to
    /// drag the card open, 1 to drag the page closed. <paramref name="finished"/> runs once, when
    /// it settles, with whether it ended open; it should commit the layout for that end, and the
    /// visuals are put back to rest right after it.</summary>
    public void Begin(Parts parts, float progress, Action<bool> finished)
    {
        _finished = finished;
        _generation++;
        IsActive = true;
        IsTracking = true;
        _startedOpen = progress >= 0.5f;
        Progress = progress;
        _progress.InsertScalar("p", progress);

        // The art: scaled about its top-left corner from thumbnail size, and moved from the
        // thumbnail's spot. Both are in the art's own space, i.e. before the Viewbox scales it.
        Bind(parts.Art, "Scale", "Vector3(Lerp(from, 1, ps.p), Lerp(from, 1, ps.p), 1)", e => e.SetScalarParameter("from", parts.ArtScaleFrom), Rest.One, Rest.One);
        Bind(parts.Art, "Translation", "Lerp(from, Vector3(0, 0, 0), ps.p)", e => e.SetVector3Parameter("from", parts.ArtOffsetFrom), Rest.Zero, Rest.Zero);
        ElementCompositionPreview.GetElementVisual(parts.Art).CenterPoint = Vector3.Zero;

        Bind(parts.Details, "Opacity", "ps.p", null, Rest.One, Rest.One);
        Bind(parts.Details, "Translation", $"Vector3(0, {DetailsRise} * (1 - ps.p), 0)", null, Rest.Zero, Rest.Zero);

        // What goes away ends invisible; its own show animation brings it back later.
        Bind(parts.Header, "Opacity", "Clamp(1 - ps.p * 2, 0, 1)", null, Rest.One, Rest.Hidden);
        Bind(parts.Leaving, "Opacity", "1 - ps.p", null, Rest.One, Rest.Hidden);
        Bind(parts.Leaving, "Translation", $"Vector3(0, -{LeavingLift} * ps.p, 0)", null, Rest.Zero, Rest.Hidden);

        // The thumbnail is what the art leaves from; the moving art stands in for it.
        _thumbnail = ElementCompositionPreview.GetElementVisual(parts.Thumbnail);
        _thumbnail.Opacity = 0;

        // The player surface's top edge rises from the card's top to the top of the view, so the
        // card itself becomes the page.
        _surfaceEdge = parts.SurfaceEdge;
        ExpressionAnimation top = _compositor.CreateExpressionAnimation("Vector2(0, inset * (1 - ps.p))");
        top.SetReferenceParameter("ps", _progress);
        top.SetScalarParameter("inset", parts.SurfaceInset);
        _surfaceEdge.StartAnimation("Offset", top);
        ExpressionAnimation size = _compositor.CreateExpressionAnimation("Vector2(w, h - inset * (1 - ps.p))");
        size.SetReferenceParameter("ps", _progress);
        size.SetScalarParameter("inset", parts.SurfaceInset);
        size.SetScalarParameter("w", parts.SurfaceSize.X);
        size.SetScalarParameter("h", parts.SurfaceSize.Y);
        _surfaceEdge.StartAnimation("Size", size);
    }

    /// <summary>Scrubs to <paramref name="progress"/> while tracking a drag.</summary>
    public void Track(float progress)
    {
        if (!IsTracking)
        {
            return;
        }

        Progress = Math.Clamp(progress, 0f, 1f);
        _progress.InsertScalar("p", Progress);
    }

    /// <summary>Lets go: animates the rest of the way to open or closed, then finishes.</summary>
    public void Settle(bool open)
    {
        if (!IsTracking)
        {
            return;
        }

        IsTracking = false;
        float target = open ? 1f : 0f;
        ScalarKeyFrameAnimation glide = _compositor.CreateScalarKeyFrameAnimation();
        glide.InsertKeyFrame(1f, target, _settleEasing);

        // The farther left to go, the longer it takes, so the speed feels the same either way.
        glide.Duration = TimeSpan.FromMilliseconds(140 + (260 * Math.Abs(target - Progress)));

        int generation = _generation;
        CompositionScopedBatch batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _progress.StartAnimation("p", glide);
        batch.End();
        batch.Completed += (_, _) => _dispatcher.TryEnqueue(() =>
        {
            if (generation == _generation)
            {
                Finish(open);
            }
        });
    }

    /// <summary>Snaps back to where it started at once, e.g. when the flyout hides mid-drag.</summary>
    public void Cancel()
    {
        if (IsActive)
        {
            _progress.StopAnimation("p");
            Finish(_startedOpen);
        }
    }

    private void Finish(bool open)
    {
        _generation++;
        IsActive = false;
        IsTracking = false;
        Progress = open ? 1f : 0f;

        // The layout change and the visual reset land in the same frame, so nothing flickers.
        Action<bool>? finished = _finished;
        _finished = null;
        finished?.Invoke(open);

        foreach (Binding binding in _bindings)
        {
            binding.Visual.StopAnimation(binding.Property);
            Rest rest = open ? binding.WhenOpen : binding.WhenClosed;
            switch (rest)
            {
                case Rest.One when binding.Property == "Opacity":
                    binding.Visual.Opacity = 1;
                    break;
                case Rest.One:
                    binding.Visual.Scale = Vector3.One;
                    break;
                case Rest.Zero:
                    binding.Visual.Properties.InsertVector3("Translation", Vector3.Zero);
                    break;
                case Rest.Hidden when binding.Property == "Opacity":
                    binding.Visual.Opacity = 0;
                    break;
            }
        }

        _bindings.Clear();
        // The surface's clip goes with its rest layout, which the finished callback set.
        _surfaceEdge?.StopAnimation("Offset");
        _surfaceEdge?.StopAnimation("Size");
        _surfaceEdge = null;

        if (_thumbnail is not null)
        {
            _thumbnail.Opacity = 1;
        }
    }

    private void Bind(UIElement element, string property, string expression, Action<ExpressionAnimation>? parameters, Rest whenClosed, Rest whenOpen)
    {
        if (property == "Translation")
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        ExpressionAnimation animation = _compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("ps", _progress);
        parameters?.Invoke(animation);
        visual.StartAnimation(property, animation);
        _bindings.Add(new Binding(visual, property, whenClosed, whenOpen));
    }

    /// <summary>The elements that move, and where the art starts relative to where it ends.</summary>
    /// <param name="Art">The full page's art; it flies from the thumbnail to its own place.</param>
    /// <param name="ArtOffsetFrom">Thumbnail's top-left minus the art's, in the art's own units.</param>
    /// <param name="ArtScaleFrom">Thumbnail width over the art's laid-out width.</param>
    /// <param name="Thumbnail">The card's small art, hidden while the big one stands in for it.</param>
    /// <param name="Details">The page's text under the art.</param>
    /// <param name="Header">The card's header, which the page replaces.</param>
    /// <param name="SurfaceEdge">The clip shape of the full-size player surface (see <see cref="PlayerSurface"/>).</param>
    /// <param name="SurfaceSize">The player surface's full size.</param>
    /// <param name="SurfaceInset">How far below its top the surface's edge sits around the card.</param>
    /// <param name="Leaving">The view the page covers.</param>
    public sealed record Parts(
        UIElement Art,
        Vector3 ArtOffsetFrom,
        float ArtScaleFrom,
        UIElement Thumbnail,
        UIElement Details,
        UIElement Header,
        CompositionRoundedRectangleGeometry SurfaceEdge,
        Vector2 SurfaceSize,
        float SurfaceInset,
        UIElement Leaving);

    /// <summary>What a driven property becomes once settled.</summary>
    private enum Rest
    {
        /// <summary>Invisible, for elements collapsed at that end; their show animation restores them.</summary>
        Hidden,
        One,
        Zero,
    }

    private sealed record Binding(Visual Visual, string Property, Rest WhenClosed, Rest WhenOpen);
}
