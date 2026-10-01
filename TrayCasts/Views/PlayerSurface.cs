using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace TrayCasts.Views;

/// <summary>
/// The card surface behind the player. At rest it is laid out either around the mini player
/// (bottom-aligned, the card's height) or over the whole content area for the full page. To move
/// between the two it is laid out full size and a rounded clip moves its top edge, so the edge
/// can glide or follow a drag without a layout pass per frame.
/// </summary>
internal sealed class PlayerSurface
{
    private const float CornerRadius = 8;
    private const float FarAway = 10_000;
    private static readonly TimeSpan GlideDuration = TimeSpan.FromMilliseconds(320);

    private readonly Border _surface;
    private readonly Visual _visual;
    private readonly Compositor _compositor;
    private readonly CompositionEasingFunction _easing;
    private readonly DispatcherQueue _dispatcher;
    private Visual? _under;
    private bool _restingSmall = true;
    private int _generation;

    public PlayerSurface(Border surface)
    {
        _surface = surface;
        _visual = ElementCompositionPreview.GetElementVisual(surface);
        _compositor = _visual.Compositor;
        _easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>Lays the surface out full size, ready for a clip to move its edge. Measure after
    /// the next layout pass.</summary>
    public void LayOutFull()
    {
        _generation++; // Supersedes a glide still settling.
        _restingSmall = false;
        _surface.VerticalAlignment = VerticalAlignment.Stretch;
        _surface.Height = double.NaN;
    }

    /// <summary>Clips the full-size surface so its top edge sits <paramref name="inset"/> below
    /// its real top. Returns the clip's shape, for a caller that drives it with expressions.</summary>
    /// <param name="under">The view the surface grows over. The surface is translucent, so this
    /// is clipped to above the edge: the card reads as one solid thing changing shape, not as a
    /// window onto the list fading out behind it.</param>
    public CompositionRoundedRectangleGeometry ClipAt(float inset, UIElement under)
    {
        CompositionRoundedRectangleGeometry shape = _compositor.CreateRoundedRectangleGeometry();
        shape.CornerRadius = new Vector2(CornerRadius);
        Vector2 size = new((float)_surface.ActualWidth, (float)_surface.ActualHeight);
        shape.Offset = new Vector2(0, inset);
        shape.Size = size - new Vector2(0, inset);
        _visual.Clip = _compositor.CreateGeometricClip(shape);

        // The underlying view's bottom follows the edge, wherever the edge is driven from. Views
        // in that spot start at the surface's top, and may be mid-collapse (unmeasurable), so the
        // clip is just "everything above the edge": far out on the other three sides.
        ClearUnder();
        _under = ElementCompositionPreview.GetElementVisual(under);
        CompositionRectangleGeometry above = _compositor.CreateRectangleGeometry();
        above.Offset = new Vector2(-FarAway, -FarAway);
        ExpressionAnimation toEdge = _compositor.CreateExpressionAnimation("Vector2(2 * far, Max(0, far + edge.Offset.Y))");
        toEdge.SetReferenceParameter("edge", shape);
        toEdge.SetScalarParameter("far", FarAway);
        above.StartAnimation("Size", toEdge);
        _under.Clip = _compositor.CreateGeometricClip(above);
        return shape;
    }

    /// <summary>Animates the top edge from one inset to another, then rests in the layout that
    /// matches where it ended.</summary>
    public void Glide(float from, float to, bool endsFull, double cardHeight, UIElement under)
    {
        CompositionRoundedRectangleGeometry shape = ClipAt(from, under);
        float height = (float)_surface.ActualHeight;

        Vector2KeyFrameAnimation offset = _compositor.CreateVector2KeyFrameAnimation();
        offset.InsertKeyFrame(1f, new Vector2(0, to), _easing);
        offset.Duration = GlideDuration;
        Vector2KeyFrameAnimation size = _compositor.CreateVector2KeyFrameAnimation();
        size.InsertKeyFrame(1f, new Vector2((float)_surface.ActualWidth, height - to), _easing);
        size.Duration = GlideDuration;

        int generation = ++_generation;
        CompositionScopedBatch batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        shape.StartAnimation("Offset", offset);
        shape.StartAnimation("Size", size);
        batch.End();
        batch.Completed += (_, _) => _dispatcher.TryEnqueue(() =>
        {
            if (generation == _generation)
            {
                Rest(endsFull, cardHeight);
            }
        });
    }

    /// <summary>Drops any clip and lays the surface out for the full page or around the card.</summary>
    public void Rest(bool full, double cardHeight)
    {
        _generation++;
        _visual.Clip = null;
        ClearUnder();
        _restingSmall = !full;
        _surface.VerticalAlignment = full ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;
        _surface.Height = full ? double.NaN : cardHeight;
    }

    private void ClearUnder()
    {
        if (_under is not null)
        {
            _under.Clip = null;
            _under = null;
        }
    }

    /// <summary>Keeps the small surface the card's height as the card grows or shrinks.</summary>
    public void FollowCard(double cardHeight)
    {
        if (_restingSmall)
        {
            _surface.Height = cardHeight;
        }
    }
}
