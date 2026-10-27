using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TTC.Gui.Models;

namespace TTC.Gui.Controls;

public partial class CardTile : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(CardInfo), typeof(CardTile), new PropertyMetadata(null, Refresh));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(CardTile), new PropertyMetadata(Brushes.Gray, Refresh));

    public static readonly DependencyProperty UnknownProperty = DependencyProperty.Register(
        nameof(Unknown), typeof(bool), typeof(CardTile), new PropertyMetadata(false, Refresh));

    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(
        nameof(Highlight), typeof(bool), typeof(CardTile), new PropertyMetadata(false, Refresh));

    public static readonly DependencyProperty CenterLabelProperty = DependencyProperty.Register(
        nameof(CenterLabel), typeof(string), typeof(CardTile), new PropertyMetadata(null, Refresh));

    public static readonly DependencyProperty BackBrushProperty = DependencyProperty.Register(
        nameof(BackBrush), typeof(Brush), typeof(CardTile), new PropertyMetadata(null, Refresh));

    public CardTile()
    {
        InitializeComponent();
        Loaded += (_, _) => Apply(this);
    }

    public CardInfo? Card
    {
        get => (CardInfo?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public bool Unknown
    {
        get => (bool)GetValue(UnknownProperty);
        set => SetValue(UnknownProperty, value);
    }

    public bool Highlight
    {
        get => (bool)GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    public string? CenterLabel
    {
        get => (string?)GetValue(CenterLabelProperty);
        set => SetValue(CenterLabelProperty, value);
    }

    public Brush? BackBrush
    {
        get => (Brush?)GetValue(BackBrushProperty);
        set => SetValue(BackBrushProperty, value);
    }

    private static void Refresh(DependencyObject d, DependencyPropertyChangedEventArgs e) => Apply((CardTile)d);

    private static void Apply(CardTile tile)
    {
        var card = tile.Card;

        tile.Root.BorderBrush = tile.Highlight ? CardTileBrushes.Gold : tile.Accent;
        tile.Glow.Visibility = tile.Highlight ? Visibility.Visible : Visibility.Collapsed;

        if (tile.Unknown)
        {
            tile.Root.Background = CardTileBrushes.UnknownBack;
            tile.TxtUp.Text = tile.TxtRight.Text = tile.TxtDown.Text = tile.TxtLeft.Text = "?";
            tile.TxtName.Text = tile.CenterLabel ?? "未知牌";
            tile.TxtStar.Text = "";
            return;
        }

        tile.Root.Background = tile.BackBrush ?? CardTileBrushes.EmptyBack;
        if (card is null)
        {
            tile.TxtUp.Text = tile.TxtRight.Text = tile.TxtDown.Text = tile.TxtLeft.Text = "";
            tile.TxtName.Text = tile.CenterLabel ?? "";
            tile.TxtStar.Text = "";
            return;
        }

        tile.TxtUp.Text = card.EdgeText(0);
        tile.TxtRight.Text = card.EdgeText(1);
        tile.TxtDown.Text = card.EdgeText(2);
        tile.TxtLeft.Text = card.EdgeText(3);
        tile.TxtName.Text = tile.CenterLabel ?? card.Name;
        tile.TxtStar.Text = card.StarText;
    }
}

public static class CardTileBrushes
{
    public static readonly Brush Gold = Frozen(0xFF, 0xD4, 0x8A);
    public static readonly Brush UnknownBack = Frozen(0x4A, 0x46, 0x3A);
    public static readonly Brush EmptyBack = Frozen(0x2C, 0x2C, 0x2C);
    public static readonly Brush BlueBack = Frozen(0x27, 0x3A, 0x52);
    public static readonly Brush RedBack = Frozen(0x52, 0x2B, 0x2B);
    public static readonly Brush BlueAccent = Frozen(0x5A, 0x9B, 0xD5);
    public static readonly Brush RedAccent = Frozen(0xD9, 0x6A, 0x6A);
    public static readonly Brush GrayAccent = Frozen(0x66, 0x66, 0x66);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
