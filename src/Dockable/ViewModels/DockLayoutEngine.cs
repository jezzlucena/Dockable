using Dockable.Models;

namespace Dockable.ViewModels;

/// <summary>
/// Computes the macOS-style fisheye layout for the dock: per-icon scale from cursor distance
/// (raised-cosine falloff), neighbour displacement via cumulative layout, and growth away from the
/// docked screen edge. Also drives live drag-reorder: the dragged tile floats at the cursor while the
/// others open a gap and ease into place. Positions are written onto the item view-models each frame.
///
/// The engine is orientation-aware. Items are laid out along a <em>main</em> axis (horizontal for the
/// Bottom/Top edges, vertical for Left/Right) and magnify along the perpendicular <em>cross</em> axis,
/// growing away from the screen edge. All logical (main, cross/depth) coordinates are mapped to
/// absolute window X/Y/Width/Height per <see cref="DockEdge"/> at the end.
/// </summary>
public sealed class DockLayoutEngine
{
    // Layout metrics in DIPs. Gap/padding are fixed; icon sizes come from settings.
    private const double Gap = 8;
    private const double HPad = 14;   // padding at the two ends of the bar (along the main axis)
    private const double VPad = 14;   // padding between the bar edge and the icons (cross axis)
    private const double EdgeMargin = 16; // gap from the screen edge to the icon row
    // Headroom on the far-from-edge side. Generous so a per-icon hover label (which lives inside the
    // window, above the magnified icon) isn't clipped by the window edge. This is transparent space;
    // it doesn't move the bar or icons on screen (their position is independent of the window height).
    private const double TopHeadroom = 52;

    // Per-frame easing factors, tuned for 60 FPS. They're re-scaled to the real frame delta (see
    // TimeSmooth) so an animation lasts the same wall-clock time at any frame rate — on a slow machine
    // that means fewer, larger steps (frame-skipping) rather than the same small steps stretched over
    // more real time (which made appear/disappear drag on and magnification trail the cursor).
    private const double Smoothing = 0.35;       // scale easing
    private const double AppearSmoothing = 0.18;  // add/remove (grow-in / shrink-out) easing
    private const double DragPosSmoothing = 0.28; // position easing while dragging / settling
    private const double ReferenceFrameMs = 1000.0 / 60.0; // the frame time the factors above assume

    /// <summary>Re-scales a per-60FPS-frame smoothing factor <paramref name="k"/> to the actual elapsed
    /// <paramref name="dtMs"/>, so exponential easing (<c>v += (target-v)*k</c>) converges over a fixed
    /// wall-clock time regardless of frame rate. At 60 FPS it's a no-op; at 30 FPS it takes a
    /// correspondingly larger step. Never overshoots (result stays in [0,1)).</summary>
    private static double TimeSmooth(double k, double dtMs)
        => 1.0 - Math.Pow(1.0 - k, dtMs / ReferenceFrameMs);
    private const double DragScale = 1.12;        // the lifted tile is slightly enlarged
    private const double DragLift = 14;           // and raised (deeper) than the row

    /// <summary>Fraction of each cell the icon fills; the rest is padding within the bar.</summary>
    private const double IconFill = 0.84;

    /// <summary>Peak launch-bounce lift as a fraction of the base icon size.</summary>
    private const double BounceLift = 0.5;

    /// <summary>A separator's layout footprint along the bar, as a fraction of a normal icon cell. Kept
    /// small so the separator's visible spacing stays proportional to the icons (its clickable area is
    /// a fixed <see cref="SeparatorHitArea"/> that overhangs the gaps, independent of this).</summary>
    private const double SeparatorFill = 1.0 / 4.0;

    /// <summary>Gap (DIP) between each end of a separator line and the bar's edges.</summary>
    private const double SeparatorEndInset = 9;

    /// <summary>Fixed clickable/draggable extent of a separator along the bar (DIP), regardless of icon size.</summary>
    private const double SeparatorHitArea = 36;

    private readonly DockViewModel _vm;

    private double _baseSize = 48;
    private double _maxScale = 2;
    private double _radius = 160;
    private double _restingAlong;      // resting extent of the whole row along the main axis
    private double _restingBlockMain;  // main-axis start of the resting (un-magnified) row
    private double _alongWindow;       // window size along the main axis (eased toward the target)
    private double _alongWindowTarget; // recomputed main-axis size; _alongWindow glides to it per frame
    private double _crossWindow;       // window size along the cross (depth) axis
    private double _gapExtent;         // eased width of the drag/drop placeholder gap (0 when closed)

    private DockItemViewModel? _dragItem;
    private bool _settling;
    private double _lastMouseMain = double.NegativeInfinity; // cursor state of the last real frame,
    private bool _lastHovering;                              // reused by Recompute's nominal step
    private bool _externalDrop;   // an external file is being dragged over the dock (preview a gap)
    private double _externalMain; // its cursor position along the main axis
    private bool _externalPathDrop; // the payload is a folder/document → the gap opens in the right section

    public DockLayoutEngine(DockViewModel vm) => _vm = vm;

    private DockEdge Edge => _vm.Settings.Edge;

    /// <summary>True when items stack vertically (Left/Right edges); the main axis is then screen-Y.</summary>
    public bool IsVertical => Edge is DockEdge.Left or DockEdge.Right;

    /// <summary>Resting cell extent (along the main axis) for an item: narrower for separators.</summary>
    private double BaseCell(DockItemViewModel item) => item.IsSeparator ? _baseSize * SeparatorFill : _baseSize;

    /// <summary>The pin index a drop would land at (number of pinned tiles before the cursor).</summary>
    public int DragInsertIndex { get; private set; }

    public void BeginDrag(DockItemViewModel item)
    {
        _dragItem = item;
        _settling = false;
        item.IsDragging = true;
    }

    public void EndDrag()
    {
        if (_dragItem is not null)
            _dragItem.IsDragging = false;
        _dragItem = null;
        _settling = true; // let the tile glide into its committed slot
    }

    /// <summary>An external file is being dragged over the dock at <paramref name="mouseMain"/>: open
    /// a placeholder gap at the insertion point and keep tracking the cursor.</summary>
    public void UpdateExternalDrop(double mouseMain, bool pathSection)
    {
        _externalDrop = true;
        _externalMain = mouseMain;
        _externalPathDrop = pathSection;
        _settling = false;
    }

    /// <summary>The external drag left or dropped: close the placeholder gap (tiles glide back).</summary>
    public void EndExternalDrop()
    {
        if (!_externalDrop)
            return;
        _externalDrop = false;
        _externalPathDrop = false;
        _settling = true;
    }

    /// <summary>The pin insertion index for an external drop at <paramref name="mouseMain"/> (matches
    /// the placeholder gap's position).</summary>
    public int ComputeDropIndex(double mouseMain)
        => PinnedBeforeCursor(new List<DockItemViewModel>(_vm.Items), mouseMain);

    /// <summary>The right-section (PinnedPaths) insertion index for a folder/document drop at
    /// <paramref name="mouseMain"/> (matches the placeholder gap's position).</summary>
    public int ComputeDropPathIndex(double mouseMain)
        => PathsBeforeCursor(new List<DockItemViewModel>(_vm.Items), mouseMain);

    public void Recompute()
    {
        var s = _vm.Settings;
        _baseSize = s.IconSize > 0 ? s.IconSize : 48;
        _maxScale = s.MagnificationEnabled && s.MaxIconSize > _baseSize ? s.MaxIconSize / _baseSize : 1.0;
        _radius = s.MagnificationRadius > 0 ? s.MagnificationRadius : 160;

        int n = _vm.Items.Count;
        _restingAlong = -Gap;
        foreach (var item in _vm.Items)
            _restingAlong += BaseCell(item) + Gap;
        if (n == 0)
            _restingAlong = 0;

        double grownHeight = _baseSize * _maxScale;
        // Reserve enough depth for whichever is taller: a magnified icon or a bouncing one.
        double iconArea = Math.Max(grownHeight, _baseSize + _baseSize * BounceLift);
        _crossWindow = iconArea + EdgeMargin + TopHeadroom + 8;
        // Reserve room for a drag gap as well so the window never clips during a reorder.
        // The main-axis size is a TARGET: the actual window glides toward it per frame (Update) —
        // stepping it here desynced the Win32 resize from the content for a frame, so the whole
        // dock visibly jittered whenever a tile was added or removed.
        _alongWindowTarget = ComputeMaxMagnifiedAlong() + _baseSize + Gap + 2 * HPad + 30;
        if (_alongWindow <= 0)
            _alongWindow = _alongWindowTarget; // first layout: nothing on screen yet, snap

        if (IsVertical)
        {
            _vm.WindowWidth = _crossWindow;
            _vm.WindowHeight = _alongWindow;
        }
        else
        {
            _vm.WindowWidth = _alongWindow;
            _vm.WindowHeight = _crossWindow;
        }

        _restingBlockMain = (_alongWindow - _restingAlong) / 2;

        // Cross-axis (screen) coordinate of a fully-magnified icon's outer edge, so hover labels can
        // sit beyond it. Exact for Bottom; approximate for the other edges (a known rough edge).
        double maxCell = _baseSize * _maxScale;
        double maxRender = maxCell * IconFill;
        double magNear = EdgeMargin + (maxCell - maxRender) / 2;
        _vm.MagnifiedTop = _crossWindow - magNear - maxRender;

        // One nominal step with the LAST-SEEN cursor state — never a forced "no hover". Recomputes
        // run mid-hover (a departed tile finalizing, the 1 s refresh reconciling): the old reset of
        // every CurrentScale to 1 + a hovering:false step made the magnification blink off and
        // back on under the cursor — a visible width jitter on every add/remove while hovering.
        Update(_lastMouseMain, _lastHovering, ReferenceFrameMs);
    }

    /// <summary>Advances the layout one frame. <paramref name="mouseMain"/> is the cursor's main-axis
    /// coordinate (window X for horizontal docks, window Y for vertical). <paramref name="dtMs"/> is the
    /// real time since the previous processed frame, so the eases stay frame-rate independent. Returns
    /// true while animating.</summary>
    public bool Update(double mouseMain, bool hovering, double dtMs)
    {
        _lastMouseMain = mouseMain; // remembered for Recompute's nominal step (see Recompute)
        _lastHovering = hovering;
        double scaleSmooth = TimeSmooth(Smoothing, dtMs);
        double appearSmooth = TimeSmooth(AppearSmoothing, dtMs);
        double dragSmooth = TimeSmooth(DragPosSmoothing, dtMs);
        var items = _vm.Items;
        int n = items.Count;
        if (n == 0)
            return false;

        bool dragging = _dragItem is not null && items.Contains(_dragItem);
        // An external file drag opens a placeholder gap too (but has no floating in-dock tile).
        bool externalGap = _externalDrop && _dragItem is null;
        bool gapMode = dragging || externalGap;
        double cursorMain = externalGap ? _externalMain : mouseMain;
        bool animating = false;

        // The window's main-axis size glides to its recomputed target so adding/removing a tile
        // never steps it (a step desyncs the Win32 resize from the content for one frame — jitter).
        // _restingBlockMain follows the eased size, which keeps RestingCenterOf aims exact: for a
        // monitor-centered dock the easing offset and the centering offset cancel in screen space.
        if (Math.Abs(_alongWindow - _alongWindowTarget) > 0.5)
        {
            _alongWindow += (_alongWindowTarget - _alongWindow) * appearSmooth;
            animating = true;
        }
        else
        {
            _alongWindow = _alongWindowTarget;
        }
        _restingBlockMain = (_alongWindow - _restingAlong) / 2;
        if (IsVertical)
            _vm.WindowHeight = _alongWindow;
        else
            _vm.WindowWidth = _alongWindow;

        // The drag/drop placeholder gap eases open and closed too (it used to pop the bar wider).
        double gapTarget = gapMode ? _baseSize + Gap : 0;
        if (Math.Abs(_gapExtent - gapTarget) > 0.5)
        {
            _gapExtent += (gapTarget - _gapExtent) * appearSmooth;
            animating = true;
        }
        else
        {
            _gapExtent = gapTarget;
        }

        // --- Scales (magnification suppressed during a drag/drop gap for a stable parting) ---
        double cursorContent = cursorMain - _restingBlockMain;
        double rest = 0;
        for (int i = 0; i < n; i++)
        {
            double bc = BaseCell(items[i]);
            double restCenter = rest + bc / 2;
            rest += bc + Gap;
            double target = !gapMode && hovering && !items[i].IsSeparator
                ? 1.0 + (_maxScale - 1.0) * Falloff(Math.Abs(cursorContent - restCenter), _radius)
                : 1.0;

            double cur = items[i].CurrentScale;
            cur += (target - cur) * scaleSmooth;
            if (Math.Abs(target - cur) < 0.001)
                cur = target;
            else
                animating = true;
            items[i].CurrentScale = cur;

            // Appearance scale: ease toward 1 (added) or 0 (removed) so the row's width transitions
            // smoothly. A growing/shrinking cell pushes neighbours apart / lets them close in.
            double targetAppear = items[i].Departing ? 0.0 : 1.0;
            double a = items[i].AppearScale;
            a += (targetAppear - a) * appearSmooth;
            if (Math.Abs(targetAppear - a) < 0.01)
                a = targetAppear;
            else
                animating = true;
            items[i].AppearScale = a;
        }

        // --- Placed items = everything except the floating dragged tile ---
        var placed = new List<DockItemViewModel>(n);
        foreach (var item in items)
            if (!ReferenceEquals(item, _dragItem))
                placed.Add(item);

        int gapSlot = -1; // placed index where the gap opens
        if (gapMode && (_dragItem?.IsPinnedPath == true || (externalGap && _externalPathDrop)))
        {
            // A pinned file/folder tile, or an external folder/document drag: the gap lives in the
            // right section, among the path tiles.
            DragInsertIndex = PathsBeforeCursor(placed, cursorMain);
            gapSlot = PathGapSlot(placed, DragInsertIndex);
        }
        else if (gapMode)
        {
            DragInsertIndex = PinnedBeforeCursor(placed, cursorMain);
            gapSlot = 1 + DragInsertIndex; // among the pinned apps (after Start)
        }

        double totalAlong = -Gap;
        foreach (var item in placed)
            totalAlong += (BaseCell(item) * item.CurrentScale + Gap) * item.AppearScale; // collapses with appear
        totalAlong += _gapExtent; // the dragged tile / drop placeholder (eased open and closed)

        double magAlong = (_alongWindow - totalAlong) / 2;
        bool animatePos = dragging || _settling || externalGap;
        bool stillSettling = false;

        double along = magAlong;
        for (int j = 0; j < placed.Count; j++)
        {
            if (j == gapSlot)
                along += _gapExtent; // open the gap (eased, so the tiles part smoothly)

            var item = placed[j];
            double appear = item.AppearScale;                       // 0..1 grow-in / shrink-out
            double cellAlong = BaseCell(item) * item.CurrentScale;  // layout footprint along the bar (narrow for separators)
            double cellCross = _baseSize * item.CurrentScale;       // depth cell (always full)
            // Separators get a fixed clickable extent that overhangs the gaps; icons fill their cell.
            double renderAlong = (item.IsSeparator ? SeparatorHitArea : cellAlong * IconFill) * appear;
            // Icons render at IconFill of their depth cell; separators span the bar's depth
            // (cell + 2·VPad) minus SeparatorEndInset at each end, so the divider line reads
            // bar-tall while stopping exactly that inset short of both bar edges (the nearDepth
            // below centers any render depth on the cell, so this stays centered).
            double renderCross = (item.IsSeparator
                ? cellCross + 2 * (VPad - SeparatorEndInset)
                : cellCross * IconFill) * appear;
            double targetMain = along + (cellAlong * appear - renderAlong) / 2; // center the icon within its (collapsing) cell

            double mainPos;
            if (animatePos)
            {
                double cur = IsVertical ? item.Y : item.X;
                double delta = targetMain - cur;
                if (Math.Abs(delta) > 0.5) { stillSettling = true; mainPos = cur + delta * dragSmooth; }
                else mainPos = targetMain;
            }
            else
            {
                mainPos = targetMain;
            }

            double nearDepth = EdgeMargin + (cellCross - renderCross) / 2;

            // Launch/attention bounce: lift only the ICON via its render transform — the container
            // (and with it the running dot at the bar edge) stays put instead of hopping along.
            if (item.UpdateBounce(_baseSize * BounceLift))
                animating = true;
            switch (Edge)
            {
                case DockEdge.Top: item.BounceX = 0; item.BounceY = item.BounceOffset; break;
                case DockEdge.Left: item.BounceX = item.BounceOffset; item.BounceY = 0; break;
                case DockEdge.Right: item.BounceX = -item.BounceOffset; item.BounceY = 0; break;
                default: item.BounceX = 0; item.BounceY = -item.BounceOffset; break; // Bottom: up
            }

            PlaceItem(item, mainPos, renderAlong, renderCross, nearDepth);
            along += (cellAlong + Gap) * appear; // advance shrinks as the cell collapses
        }

        // The dragged tile floats at the cursor, lifted (deeper) and slightly enlarged. The in-canvas
        // tile is invisible during a drag (the ghost popup is shown instead), so exact placement here
        // only needs to keep the gap math consistent.
        if (dragging)
        {
            double cell = _baseSize * DragScale;
            double render = cell * IconFill;
            double nearDepth = EdgeMargin + (cell - render) / 2 + DragLift;
            PlaceItem(_dragItem!, mouseMain - render / 2, render, render, nearDepth);
            animating = true;
        }

        if (_settling && !dragging)
        {
            if (stillSettling) animating = true;
            else _settling = false;
        }

        if (externalGap)
            animating = true; // keep the loop alive while the placeholder gap is open

        PlaceBar(magAlong - HPad, totalAlong + 2 * HPad, _baseSize + 2 * VPad, EdgeMargin - VPad);

        return animating;
    }

    /// <summary>Maps an item's logical (main, depth) box to absolute window X/Y/W/H for the current edge.</summary>
    private void PlaceItem(DockItemViewModel item, double mainStart, double renderAlong, double renderCross, double nearDepth)
    {
        switch (Edge)
        {
            case DockEdge.Bottom:
                item.X = mainStart; item.RenderWidth = renderAlong;
                item.RenderSize = renderCross;
                item.Y = _crossWindow - nearDepth - renderCross;
                break;
            case DockEdge.Top:
                item.X = mainStart; item.RenderWidth = renderAlong;
                item.RenderSize = renderCross;
                item.Y = nearDepth;
                break;
            case DockEdge.Left:
                item.Y = mainStart; item.RenderSize = renderAlong;
                item.RenderWidth = renderCross;
                item.X = nearDepth;
                break;
            case DockEdge.Right:
                item.Y = mainStart; item.RenderSize = renderAlong;
                item.RenderWidth = renderCross;
                item.X = _crossWindow - nearDepth - renderCross;
                break;
        }
    }

    /// <summary>Maps the bar's logical (main span, depth) to absolute window geometry for the current edge.</summary>
    private void PlaceBar(double barMainStart, double barMainSize, double barCrossSize, double barCrossNear)
    {
        switch (Edge)
        {
            case DockEdge.Bottom:
                _vm.BarLeft = barMainStart; _vm.BarWidth = barMainSize;
                _vm.BarHeight = barCrossSize; _vm.BarTop = _crossWindow - barCrossNear - barCrossSize;
                break;
            case DockEdge.Top:
                _vm.BarLeft = barMainStart; _vm.BarWidth = barMainSize;
                _vm.BarHeight = barCrossSize; _vm.BarTop = barCrossNear;
                break;
            case DockEdge.Left:
                _vm.BarTop = barMainStart; _vm.BarHeight = barMainSize;
                _vm.BarWidth = barCrossSize; _vm.BarLeft = barCrossNear;
                break;
            case DockEdge.Right:
                _vm.BarTop = barMainStart; _vm.BarHeight = barMainSize;
                _vm.BarWidth = barCrossSize; _vm.BarLeft = _crossWindow - barCrossNear - barCrossSize;
                break;
        }
    }

    /// <summary>
    /// The resting (un-magnified, fully grown-in) center of <paramref name="target"/> in window DIP for
    /// the current edge — the spot it settles at once magnification is off and its grow-in has finished.
    /// Used to aim the minimize warp at a tile's final slot while it's still animating in (its live
    /// position is mid-transition). Returns (0,0) if the item isn't in the row.
    /// </summary>
    public (double X, double Y) RestingCenterOf(DockItemViewModel target)
    {
        // Resting cumulative layout (scales = 1, fully appeared), centered — matches Recompute/Update.
        double along = _restingBlockMain;
        double centerMain = double.NaN;
        foreach (var item in _vm.Items)
        {
            double bc = BaseCell(item);
            if (ReferenceEquals(item, target))
            {
                centerMain = along + bc / 2; // the icon is centered in its cell
                break;
            }
            along += bc + Gap;
        }
        if (double.IsNaN(centerMain))
            return (0, 0);

        // Cross-axis (depth) center of a normal icon tile at rest.
        double renderCross = _baseSize * IconFill;
        double crossCenter = EdgeMargin + (_baseSize - renderCross) / 2 + renderCross / 2;

        return Edge switch
        {
            DockEdge.Top => (centerMain, crossCenter),
            DockEdge.Left => (crossCenter, centerMain),
            DockEdge.Right => (_crossWindow - crossCenter, centerMain),
            _ => (centerMain, _crossWindow - crossCenter), // Bottom
        };
    }

    /// <summary>Number of pinned tiles whose resting center is before the cursor (along the main axis).</summary>
    private int PinnedBeforeCursor(List<DockItemViewModel> placed, double mouseMain)
    {
        // Resting cumulative layout (scales = 1) of the placed items, centered.
        double width = -Gap;
        foreach (var item in placed)
            width += BaseCell(item) + Gap;
        double start = (_alongWindow - width) / 2;

        int count = 0;
        double x = start;
        foreach (var item in placed)
        {
            double bc = BaseCell(item);
            double center = x + bc / 2;
            if (item.IsTaskbarApp && item.IsPinned && mouseMain > center)
                count++;
            x += bc + Gap;
        }
        return count;
    }

    /// <summary>Number of pinned file/folder tiles whose resting center is before the cursor.</summary>
    private int PathsBeforeCursor(List<DockItemViewModel> placed, double mouseMain)
    {
        double width = -Gap;
        foreach (var item in placed)
            width += BaseCell(item) + Gap;
        double start = (_alongWindow - width) / 2;

        int count = 0;
        double x = start;
        foreach (var item in placed)
        {
            double bc = BaseCell(item);
            if (item.IsPinnedPath && mouseMain > x + bc / 2)
                count++;
            x += bc + Gap;
        }
        return count;
    }

    /// <summary>The placed index where a dragged path tile's gap opens: the insertIndex-th slot of
    /// the path group. When the dragged tile was the only one, the group "start" is where paths
    /// compose — after the minimized tiles, just before the Recycle Bin.</summary>
    private static int PathGapSlot(List<DockItemViewModel> placed, int insertIndex)
    {
        int firstPath = placed.FindIndex(p => p.IsPinnedPath);
        if (firstPath >= 0)
            return firstPath + insertIndex;
        int recycle = placed.FindIndex(p => p.IsRecycleBin);
        return recycle >= 0 ? recycle : placed.Count;
    }

    private static double Falloff(double distance, double radius)
    {
        if (distance >= radius)
            return 0;
        return 0.5 * (Math.Cos(Math.PI * distance / radius) + 1);
    }

    private double ComputeMaxMagnifiedAlong()
    {
        int n = _vm.Items.Count;
        if (n == 0)
            return 0;

        // Cumulative resting centers (separators are narrower than icons).
        var centers = new double[n];
        double rest = 0;
        for (int i = 0; i < n; i++)
        {
            double bc = BaseCell(_vm.Items[i]);
            centers[i] = rest + bc / 2;
            rest += bc + Gap;
        }

        double max = _restingAlong;
        for (int c = 0; c < n; c++)
        {
            double cursor = centers[c];
            double total = -Gap;
            for (int i = 0; i < n; i++)
            {
                var item = _vm.Items[i];
                double scale = item.IsSeparator
                    ? 1.0
                    : 1.0 + (_maxScale - 1.0) * Falloff(Math.Abs(cursor - centers[i]), _radius);
                total += BaseCell(item) * scale + Gap;
            }
            max = Math.Max(max, total);
        }
        return max;
    }
}
