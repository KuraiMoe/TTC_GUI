using System.Windows.Media;
using TTC.Gui.Models;

namespace TTC.Gui.ViewModels;

/// <summary>One editable card slot: a board cell or a hand slot.</summary>
public sealed class SlotViewModel : ObservableObject
{
    private CardInfo? _card;
    private bool _unknown;
    private bool _canUse = true;
    private int _owner;
    private bool _isRecommended;
    private bool _isPlayed;
    private bool _isDropHover;

    /// <summary>Board cell 0..8, or -1 for hand slots.</summary>
    public int Cell { get; init; } = -1;

    /// <summary>Hand slot 0..4, or -1 for board cells.</summary>
    public int HandSlot { get; init; } = -1;

    /// <summary>Hand owner (0=red, 1=blue); for board cells this is the current card owner.</summary>
    public int Side { get; init; } = 1;

    public bool IsBoard => Cell >= 0;

    /// <summary>Stable automation id: Board0..8 or Hand0/1_0..4.</summary>
    public string AutomationKey => IsBoard ? $"Board{Cell}" : $"Hand{Side}_{HandSlot}";

    public string Title { get; init; } = "";

    public CardInfo? Card
    {
        get => _card;
        set
        {
            if (Set(ref _card, value))
            {
                if (value is not null) Unknown = false;
                RaiseDisplayChanged();
            }
        }
    }

    public bool Unknown
    {
        get => _unknown;
        set
        {
            if (Set(ref _unknown, value))
            {
                if (value) Card = null;
                RaiseDisplayChanged();
            }
        }
    }

    public bool CanUse
    {
        get => _canUse;
        set => Set(ref _canUse, value);
    }

    /// <summary>Board card owner (0=red, 1=blue); ignored on hand slots.</summary>
    public int Owner
    {
        get => _owner;
        set
        {
            if (Set(ref _owner, value)) RaiseDisplayChanged();
        }
    }

    public bool IsRecommended
    {
        get => _isRecommended;
        set
        {
            if (Set(ref _isRecommended, value)) RaiseDisplayChanged();
        }
    }

    /// <summary>For the recommendation: the card from the hand that would be played here.</summary>
    public bool IsPlayed
    {
        get => _isPlayed;
        set
        {
            if (Set(ref _isPlayed, value)) RaiseDisplayChanged();
        }
    }

    /// <summary>True while a dragged card hovers this slot as a valid drop target.</summary>
    public bool IsDropHover
    {
        get => _isDropHover;
        set
        {
            if (Set(ref _isDropHover, value)) RaiseDisplayChanged();
        }
    }

    public bool IsEmpty => Card is null && !Unknown;

    public string CenterText
    {
        get
        {
            if (Unknown) return "未知";
            if (Card is null) return IsBoard ? "" : "空";
            return Card.Name;
        }
    }

    public string SubText => Card is null ? "" : Card.StarText;

    public Brush BorderBrush
    {
        get
        {
            if (IsDropHover) return GoldBrush;
            if (IsRecommended || IsPlayed) return GoldBrush;
            if (IsBoard && Card is not null) return Owner == 1 ? BlueBrush : RedBrush;
            if (!IsBoard) return Side == 1 ? BlueBrush : RedBrush;
            return EmptyBrush;
        }
    }

    public Brush BackgroundBrush
    {
        get
        {
            if (Unknown) return UnknownBrush;
            if (Card is null) return EmptyBrush;
            return Owner == 1 || (!IsBoard && Side == 1) ? BlueBackBrush : RedBackBrush;
        }
    }

    public static readonly Brush GoldBrush = Create(0xFF, 0xD4, 0x8A);
    public static readonly Brush BlueBrush = Create(0x5A, 0x9B, 0xD5);
    public static readonly Brush RedBrush = Create(0xD9, 0x6A, 0x6A);
    public static readonly Brush EmptyBrush = Create(0x55, 0x55, 0x55);
    public static readonly Brush UnknownBrush = Create(0x6E, 0x6A, 0x5A);
    public static readonly Brush BlueBackBrush = Create(0x27, 0x3A, 0x52);
    public static readonly Brush RedBackBrush = Create(0x52, 0x2B, 0x2B);

    private static SolidColorBrush Create(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void RaiseDisplayChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CenterText));
        OnPropertyChanged(nameof(SubText));
        OnPropertyChanged(nameof(BorderBrush));
        OnPropertyChanged(nameof(BackgroundBrush));
        OnPropertyChanged(nameof(Card));
    }
}
