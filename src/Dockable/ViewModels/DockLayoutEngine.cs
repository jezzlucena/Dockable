namespace Dockable.ViewModels;

/// <summary>
/// Computes the macOS-style fisheye layout for the dock: per-icon scale from cursor distance
/// (raised-cosine falloff), neighbour displacement via cumulative layout, and bottom-anchored
/// growth. Also drives live drag-reorder: the dragged tile floats at the cursor while the others
/// open a gap and ease into place. Positions are written onto the item view-models each frame.
/// </summary>
public sealed class DockLayoutEngine
{
    // Layout metrics in DIPs. Gap/padding are fixed; icon sizes come from settings.
    private const double Gap = 8;
    private const double HPad = 14;
    // VPad matches HPad so the icon-to-bar gap is the same top/bottom as on the sides. BottomMargin
    // must stay >= VPad so the (bottom-anchored) bar isn't clipped at the window edge.
    private const double VPad = 14;
    private const double BottomMargin = 16;
    private const double TopHeadroom = 10;

    private const double Smoothing = 0.35;       // scale easing
    private const double DragPosSmoothing = 0.28; // position easing while dragging / settling
    private const double DragScale = 1.12;        // the lifted tile is slightly enlarged
    private const double DragLift = 14;           // and raised above the row

    /// <summary>Fraction of each cell the icon fills; the rest is padding within the bar.</summary>
    private const double IconFill = 0.84;

    /// <summary>A separator occupies a fifth of a normal icon cell (width).</summary>
    private const double SeparatorFill = 1.0 / 5.0;

    private readonly DockViewModel _vm;

    private double _baseSize = 48;
    private double _maxScale = 2;
    private double _radius = 160;
    private double _restingWidth;
    private double _restingBlockLeft;
    private double _baseline;

    private DockItemViewModel? _dragItem;
    private bool _settling;

    public DockLayoutEngine(DockViewModel vm) => _vm = vm;

    /// <summary>Resting cell width for an item: a third for separators, the base size otherwise.</summary>
    private double BaseCell(DockItemViewModel item) => item.IsSeparator ? _baseSize * SeparatorFill : _baseSize;

    /// <summary>The pin index a drop would land at (number of pinned tiles left of the cursor).</summary>
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

    public void Recompute()
    {
        var s = _vm.Settings;
        _baseSize = s.IconSize > 0 ? s.IconSize : 48;
        _maxScale = s.MagnificationEnabled && s.MaxIconSize > _baseSize ? s.MaxIconSize / _baseSize : 1.0;
        _radius = s.MagnificationRadius > 0 ? s.MagnificationRadius : 160;

        int n = _vm.Items.Count;
        _restingWidth = -Gap;
        foreach (var item in _vm.Items)
            _restingWidth += BaseCell(item) + Gap;
        if (n == 0)
            _restingWidth = 0;

        double grownHeight = _baseSize * _maxScale;
        double windowHeight = grownHeight + BottomMargin + TopHeadroom + 8;
        // Reserve room for a drag gap as well so the window never clips during a reorder.
        double windowWidth = ComputeMaxMagnifiedWidth() + _baseSize + Gap + 2 * HPad + 30;

        _vm.WindowWidth = windowWidth;
        _vm.WindowHeight = windowHeight;
        _baseline = windowHeight - BottomMargin;
        _restingBlockLeft = (windowWidth - _restingWidth) / 2;

        // Top of an icon at full magnification (bottom-anchored), so hover labels can sit above it.
        double maxCell = _baseSize * _maxScale;
        _vm.MagnifiedTop = _baseline - (maxCell + maxCell * IconFill) / 2;

        if (_dragItem is null && !_settling)
            foreach (var item in _vm.Items)
                item.CurrentScale = 1.0;
        Update(double.NegativeInfinity, hovering: false);
    }

    /// <summary>Advances the layout one frame. Returns true while still animating.</summary>
    public bool Update(double mouseX, bool hovering)
    {
        var items = _vm.Items;
        int n = items.Count;
        if (n == 0)
            return false;

        bool dragging = _dragItem is not null && items.Contains(_dragItem);
        bool animating = false;

        // --- Scales (magnification suppressed while dragging for a stable parting) ---
        double cursorContent = mouseX - _restingBlockLeft;
        double rest = 0;
        for (int i = 0; i < n; i++)
        {
            double bc = BaseCell(items[i]);
            double restCenter = rest + bc / 2;
            rest += bc + Gap;
            double target = !dragging && hovering && !items[i].IsSeparator
                ? 1.0 + (_maxScale - 1.0) * Falloff(Math.Abs(cursorContent - restCenter), _radius)
                : 1.0;

            double cur = items[i].CurrentScale;
            cur += (target - cur) * Smoothing;
            if (Math.Abs(target - cur) < 0.001)
                cur = target;
            else
                animating = true;
            items[i].CurrentScale = cur;
        }

        // --- Placed items = everything except the floating dragged tile ---
        var placed = new List<DockItemViewModel>(n);
        foreach (var item in items)
            if (!ReferenceEquals(item, _dragItem))
                placed.Add(item);

        int gapAfterPinned = dragging ? PinnedLeftOfCursor(placed, mouseX) : 0;
        DragInsertIndex = gapAfterPinned;
        int gapSlot = dragging ? 1 + gapAfterPinned : -1; // placed index where the gap opens (after Start)

        double totalWidth = -Gap;
        foreach (var item in placed)
            totalWidth += BaseCell(item) * item.CurrentScale + Gap;
        if (dragging)
            totalWidth += _baseSize + Gap;

        double magLeft = (_vm.WindowWidth - totalWidth) / 2;
        bool animatePos = dragging || _settling;
        bool stillSettling = false;

        double x = magLeft;
        for (int j = 0; j < placed.Count; j++)
        {
            if (j == gapSlot)
                x += _baseSize + Gap; // open the gap

            var item = placed[j];
            double cellW = BaseCell(item) * item.CurrentScale;     // horizontal cell (narrow for separators)
            double cellH = _baseSize * item.CurrentScale;          // vertical cell (always full height)
            double renderW = cellW * IconFill;
            double renderH = cellH * IconFill; // separators match the non-magnified icon height
            double targetX = x + (cellW - renderW) / 2;            // center the icon within its cell

            if (animatePos)
            {
                double delta = targetX - item.X;
                if (Math.Abs(delta) > 0.5) { stillSettling = true; item.X += delta * DragPosSmoothing; }
                else item.X = targetX;
            }
            else
            {
                item.X = targetX;
            }

            item.RenderWidth = renderW;
            item.RenderSize = renderH;
            item.Y = _baseline - (cellH + renderH) / 2;            // center vertically within the cell
            x += cellW + Gap;
        }

        // The dragged tile floats at the cursor, lifted and slightly enlarged.
        if (dragging)
        {
            double cell = _baseSize * DragScale;
            double render = cell * IconFill;
            _dragItem!.RenderSize = render;
            _dragItem.RenderWidth = render;
            _dragItem.X = mouseX - render / 2;
            _dragItem.Y = _baseline - (cell + render) / 2 - DragLift;
            animating = true;
        }

        if (_settling && !dragging)
        {
            if (stillSettling) animating = true;
            else _settling = false;
        }

        _vm.BarLeft = magLeft - HPad;
        _vm.BarWidth = totalWidth + 2 * HPad;
        _vm.BarHeight = _baseSize + 2 * VPad;
        _vm.BarTop = _baseline + VPad - _vm.BarHeight;

        return animating;
    }

    /// <summary>Number of pinned tiles whose resting center is left of the cursor.</summary>
    private int PinnedLeftOfCursor(List<DockItemViewModel> placed, double mouseX)
    {
        // Resting cumulative layout (scales = 1) of the placed items, centered.
        double width = -Gap;
        foreach (var item in placed)
            width += BaseCell(item) + Gap;
        double left = (_vm.WindowWidth - width) / 2;

        int count = 0;
        double x = left;
        foreach (var item in placed)
        {
            double bc = BaseCell(item);
            double center = x + bc / 2;
            if (item.IsTaskbarApp && item.IsPinned && mouseX > center)
                count++;
            x += bc + Gap;
        }
        return count;
    }

    private static double Falloff(double distance, double radius)
    {
        if (distance >= radius)
            return 0;
        return 0.5 * (Math.Cos(Math.PI * distance / radius) + 1);
    }

    private double ComputeMaxMagnifiedWidth()
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

        double max = _restingWidth;
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
