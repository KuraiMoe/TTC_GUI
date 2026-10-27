using System.Windows;
using System.Windows.Controls;
using TTC.Gui.Services;
using TTC.Gui.ViewModels;

namespace TTC.Gui.Views;

public partial class DeckView : UserControl
{
    public DeckView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Set by the main window; used to open the card picker.</summary>
    public CardLibrary? Library { get; set; }

    private DeckViewModel? Vm => DataContext as DeckViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is DeckViewModel vm)
        {
            vm.PickCard = slot =>
            {
                if (Library is null) return null;
                var dialog = new CardPickerDialog(Library, allowUnknown: false, isBoard: false)
                {
                    Owner = Window.GetWindow(this),
                };
                dialog.ShowDialog();
                return dialog.Result;
            };
        }
    }

    private void OnEditorSlotClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && sender is FrameworkElement { DataContext: SlotViewModel slot })
            Vm.OpenPicker(slot);
    }

    private void OnUseDeck(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && (sender as FrameworkElement)?.DataContext is DeckItemViewModel item)
            Vm.UseDeck(item);
    }

    private void OnEditDeck(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && (sender as FrameworkElement)?.DataContext is DeckItemViewModel item)
            Vm.EditDeck(item);
    }

    private void OnDeleteDeck(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && (sender as FrameworkElement)?.DataContext is DeckItemViewModel item)
            Vm.DeleteDeck(item);
    }
}
