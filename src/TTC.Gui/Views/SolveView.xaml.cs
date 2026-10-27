using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TTC.Gui.Services;
using TTC.Gui.ViewModels;

namespace TTC.Gui.Views;

public partial class SolveView : UserControl
{
    private SlotViewModel? _dragSource;
    private SlotViewModel? _hoverSlot;
    private Point _dragStart;
    private bool _dragging;
    private UIElement? _captured;

    public SolveView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Set by the main window; used to open the card picker.</summary>
    public CardLibrary? Library { get; set; }

    private SolveViewModel? Vm => DataContext as SolveViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is SolveViewModel vm)
        {
            vm.PickCard = slot =>
            {
                if (Library is null) return null;
                var dialog = new CardPickerDialog(Library, allowUnknown: !slot.IsBoard, isBoard: slot.IsBoard)
                {
                    Owner = Window.GetWindow(this),
                };
                dialog.ShowDialog();
                return dialog.Result;
            };
            vm.DepthRows.CollectionChanged += OnDepthRowsChanged;
        }
    }

    private void OnDepthRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Vm is null || Vm.DepthRows.Count == 0) return;
        var last = Vm.DepthRows[^1];
        Dispatcher.BeginInvoke(() => DepthGrid.ScrollIntoView(last));
    }

    // ------------------------------------------------------------------ click (card picker)

    private void OnBoardSlotClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && sender is FrameworkElement { DataContext: SlotViewModel slot })
            Vm.OpenPicker(slot);
    }

    private void OnHandSlotClick(object sender, RoutedEventArgs e) => OnBoardSlotClick(sender, e);

    // ------------------------------------------------------------------ drag & drop (floating card)

    private void OnSlotMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SlotViewModel slot }) return;
        _dragSource = slot;
        _dragStart = e.GetPosition(RootGrid);
    }

    private void OnSlotMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource is null) return;

        if (!_dragging)
        {
            var pos = e.GetPosition(RootGrid);
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            if (_dragSource.Card is null || _dragSource.Unknown)
            {
                _dragSource = null;
                return;
            }
            StartDrag(sender as UIElement);
        }

        MoveDragVisual(e);
        UpdateHover(e);
    }

    private void OnSlotMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            EndDrag(drop: true);
            e.Handled = true; // suppress the picker click after a drag
        }
        _dragSource = null;
    }

    private void StartDrag(UIElement? element)
    {
        _dragging = true;
        _captured = element;
        element?.CaptureMouse();

        DragTile.Card = _dragSource!.Card;
        DragPopup.IsOpen = true;
        DragScale.ScaleX = DragScale.ScaleY = 0.85;
        var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        DragScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        DragScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim.Clone());
    }

    private void MoveDragVisual(MouseEventArgs e)
    {
        var pos = e.GetPosition(RootGrid);
        DragPopup.HorizontalOffset = pos.X + 16;
        DragPopup.VerticalOffset = pos.Y + 12;
    }

    private void UpdateHover(MouseEventArgs e)
    {
        var target = FindSlotUnder(e) ?? FindZoneSlotUnder(e);
        if (ReferenceEquals(target, _hoverSlot)) return;

        if (_hoverSlot is not null) _hoverSlot.IsDropHover = false;
        _hoverSlot = target is not null && IsDropValid(_dragSource!, target) ? target : null;
        if (_hoverSlot is not null) _hoverSlot.IsDropHover = true;
    }

    private SlotViewModel? FindSlotUnder(MouseEventArgs e)
    {
        var hit = InputHitTest(e.GetPosition(this)) as DependencyObject;
        while (hit is not null)
        {
            if (hit is FrameworkElement { DataContext: SlotViewModel slot } && !ReferenceEquals(slot, _dragSource))
                return slot;
            hit = VisualTreeHelper.GetParent(hit);
        }
        return null;
    }

    /// <summary>Zone fallback: dropping a board card anywhere on a hand panel returns it to the first free slot.</summary>
    private SlotViewModel? FindZoneSlotUnder(MouseEventArgs e)
    {
        if (_dragSource is not { IsBoard: true }) return null;
        foreach (var (zone, side) in new[] { (HandBlueZone, 1), (HandRedZone, 0) })
        {
            if (zone.InputHitTest(e.GetPosition(zone)) is null) continue;
            var hand = side == 1 ? Vm!.HandBlue : Vm!.HandRed;
            return System.Linq.Enumerable.FirstOrDefault(hand, s => s.Card is null && !s.Unknown);
        }
        return null;
    }

    private static bool IsDropValid(SlotViewModel from, SlotViewModel target)
    {
        if (from.Card is null || from.Unknown || ReferenceEquals(from, target)) return false;
        if (target.IsBoard) return target.Card is null;          // board: empty cell only
        if (from.IsBoard) return target.Card is null && !target.Unknown; // return to an empty hand slot
        return true;                                              // hand → hand: move or swap
    }

    private void EndDrag(bool drop)
    {
        if (_hoverSlot is not null && drop && _dragSource is not null && Vm is not null)
        {
            if (_hoverSlot.IsBoard) Vm.DropOnBoard(_dragSource, _hoverSlot);
            else Vm.DropOnHandSlot(_dragSource, _hoverSlot);
        }

        if (_hoverSlot is not null) _hoverSlot.IsDropHover = false;
        _hoverSlot = null;
        _dragging = false;
        _captured?.ReleaseMouseCapture();
        _captured = null;
        DragPopup.IsOpen = false;
        DragTile.Card = null;
    }
}
