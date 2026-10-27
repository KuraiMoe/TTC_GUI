using System.ComponentModel;
using System.Windows;
using TTC.Gui.Services;
using TTC.Gui.ViewModels;
using TTC.Gui.Views;
using Wpf.Ui.Controls;

namespace TTC.Gui;

public partial class MainWindow : FluentWindow
{
    private readonly CardLibrary _library;
    private readonly SolveView _solveView;
    private readonly DraftView _draftView;
    private readonly DeckView _deckView;
    private readonly SolveViewModel _solveVm;
    private readonly AppSettings _settings;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            _library = CardLibrary.Load();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"卡牌数据库加载失败：\n{ex.Message}", "TTC 幻卡助手",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            throw;
        }

        _settings = SettingsStore.Load();
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;

        _solveVm = new SolveViewModel(_library);
        _solveView = new SolveView { Library = _library, DataContext = _solveVm };
        _draftView = new DraftView { Library = _library, DataContext = new DraftViewModel(_library) };
        _deckView = new DeckView { Library = _library, DataContext = new DeckViewModel(_library) };

        // 我的卡组页的「载入到对局」：填入我方手牌并切回对局求解页。
        ((DeckViewModel)_deckView.DataContext!).ApplyToMatch = deck =>
        {
            _solveVm.LoadDeck(deck, _solveVm.MySide);
            NavSolve.IsChecked = true;
        };

        ContentHost.Content = _solveView;
        StatusLine.Text = $"卡牌数据库已加载：{_library.Cards.Count} 张卡牌。";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        else if (!double.IsNaN(RestoreBounds.Width))
        {
            _settings.WindowWidth = RestoreBounds.Width;
            _settings.WindowHeight = RestoreBounds.Height;
        }
        SettingsStore.Save(_settings);
        base.OnClosing(e);
    }

    private void OnNavSolve(object sender, RoutedEventArgs e)
    {
        if (ContentHost is null) return;
        ContentHost.Content = _solveView;
        StatusLine.Text = "对局求解：拖动手牌到棋盘落子，获得推荐着法。";
    }

    private void OnNavDraft(object sender, RoutedEventArgs e)
    {
        ContentHost.Content = _draftView;
        StatusLine.Text = "选拔分析：三段备选展开为完整卡组并深算对比。";
    }

    private void OnNavDecks(object sender, RoutedEventArgs e)
    {
        ContentHost.Content = _deckView;
        StatusLine.Text = "我的卡组：保存常用卡组，对局中一键载入。";
    }
}
