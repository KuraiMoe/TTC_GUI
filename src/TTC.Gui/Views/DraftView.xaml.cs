using System.Windows;
using System.Windows.Controls;
using TTC.Gui.Services;
using TTC.Gui.ViewModels;

namespace TTC.Gui.Views;

public partial class DraftView : UserControl
{
    public DraftView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Set by the main window; used to open the card picker.</summary>
    public CardLibrary? Library { get; set; }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is DraftViewModel vm)
        {
            vm.PickCard = pick =>
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

    private void OnPickClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DraftViewModel vm && sender is FrameworkElement { DataContext: DraftPickVM pick })
            vm.OpenPicker(pick);
    }
}
