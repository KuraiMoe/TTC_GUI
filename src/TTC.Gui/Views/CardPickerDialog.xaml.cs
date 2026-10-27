using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TTC.Gui.Models;
using TTC.Gui.Services;
using TTC.Gui.ViewModels;
using Wpf.Ui.Controls;

namespace TTC.Gui.Views;

public partial class CardPickerDialog : FluentWindow
{
    private readonly CardLibrary _library;
    private readonly bool _allowUnknown;
    private readonly bool _isBoard;

    public PickedCard? Result { get; private set; }

    public CardPickerDialog(CardLibrary library, bool allowUnknown, bool isBoard)
    {
        InitializeComponent();
        _library = library;
        _allowUnknown = allowUnknown;
        _isBoard = isBoard;

        PickUnknownButton.Visibility = allowUnknown ? Visibility.Visible : Visibility.Collapsed;
        OwnerLabel.Visibility = isBoard ? Visibility.Visible : Visibility.Collapsed;
        OwnerBlue.Visibility = isBoard ? Visibility.Visible : Visibility.Collapsed;
        OwnerRed.Visibility = isBoard ? Visibility.Visible : Visibility.Collapsed;

        RefreshList();
    }

    private void RefreshList()
    {
        string text = SearchBox.Text;
        int star = 0;
        if (StarAll.IsChecked != true)
        {
            foreach (UIElement child in ((Panel)StarAll.Parent).Children)
            {
                if (child is RadioButton { IsChecked: true, Tag: string tag } && int.TryParse(tag, out int s))
                    star = s;
            }
        }
        CardList.ItemsSource = _library.Search(text, star, 400);
    }

    private void OnSearchChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshList();
    }

    private void OnCardDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void OnPickSelected(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        if (CardList.SelectedItem is not CardInfo card) return;
        int owner = -1;
        if (_isBoard) owner = OwnerBlue.IsChecked == true ? 1 : 0;
        Result = new PickedCard(card, Owner: owner);
        DialogResult = true;
    }

    private void OnPickUnknown(object sender, RoutedEventArgs e)
    {
        Result = new PickedCard(null, Unknown: true);
        DialogResult = true;
    }

    private void OnPickClear(object sender, RoutedEventArgs e)
    {
        Result = new PickedCard(null, Clear: true);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }
}
