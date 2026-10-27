using System.Collections.ObjectModel;
using System.Windows.Input;
using TTC.Core;
using TTC.Gui.Models;
using TTC.Gui.Services;

namespace TTC.Gui.ViewModels;

public sealed class SolveViewModel : ObservableObject
{
    private readonly CardLibrary _library;
    private SolverService? _solver;

    private bool _ruleSame, _rulePlus, _ruleReverse, _ruleFallenAce;
    private bool _ruleAscension, _ruleDescension, _ruleOrder, _ruleChaos;
    private int _current = 1;
    private double _maxSeconds = 5.0;
    private int _maxDepth = 100;
    private int _samples = 12;
    private bool _aggressive;
    private bool _isBusy;
    private string _statusText = "设置棋盘与手牌后点击「开始求解」。";
    private string _resultText = "";
    private string _statsText = "";
    private string _messageText = "";
    private int _mySide = 1;
    private CancellationTokenSource? _cts;

    public SolveViewModel(CardLibrary library)
    {
        _library = library;

        for (int cell = 0; cell < 9; cell++)
            BoardCells.Add(new SlotViewModel
            {
                Cell = cell,
                Title = $"第 {cell / 3 + 1} 行 第 {cell % 3 + 1} 列",
            });
        for (int i = 0; i < 5; i++)
            HandBlue.Add(new SlotViewModel { HandSlot = i, Side = 1, Title = $"蓝方手牌 {i + 1}" });
        for (int i = 0; i < 5; i++)
            HandRed.Add(new SlotViewModel { HandSlot = i, Side = 0, Title = $"红方手牌 {i + 1}" });

        SolveCommand = new AsyncRelayCommand(SolveAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        ClearCommand = new RelayCommand(ClearAll, () => !IsBusy);
        DemoCommand = new RelayCommand(LoadDemo, () => !IsBusy);
    }

    public ObservableCollection<SlotViewModel> BoardCells { get; } = new();
    public ObservableCollection<SlotViewModel> HandBlue { get; } = new();
    public ObservableCollection<SlotViewModel> HandRed { get; } = new();
    public ObservableCollection<DepthRow> DepthRows { get; } = new();

    /// <summary>The view wires this to the card picker dialog.</summary>
    public Func<SlotViewModel, PickedCard?>? PickCard { get; set; }

    public ICommand SolveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand DemoCommand { get; }

    /// <summary>Which side is the user: 1 = 蓝方, 0 = 红方. Labels and deck loading follow it.</summary>
    public int MySide
    {
        get => _mySide;
        set
        {
            if (Set(ref _mySide, value))
            {
                OnPropertyChanged(nameof(BlueHandTitle));
                OnPropertyChanged(nameof(RedHandTitle));
                OnPropertyChanged(nameof(IsMySideBlue));
                OnPropertyChanged(nameof(IsMySideRed));
            }
        }
    }

    public bool IsMySideBlue { get => MySide == 1; set { if (value) MySide = 1; OnPropertyChanged(); OnPropertyChanged(nameof(IsMySideRed)); } }
    public bool IsMySideRed { get => MySide == 0; set { if (value) MySide = 0; OnPropertyChanged(); OnPropertyChanged(nameof(IsMySideBlue)); } }

    public string BlueHandTitle => MySide == 1 ? "蓝方手牌（我方）" : "蓝方手牌（对方）";
    public string RedHandTitle => MySide == 0 ? "红方手牌（我方）" : "红方手牌（对方）";

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
            {
                (SolveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ClearCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (DemoCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string ResultText { get => _resultText; set => Set(ref _resultText, value); }
    public string StatsText { get => _statsText; set => Set(ref _statsText, value); }
    public string MessageText { get => _messageText; set => Set(ref _messageText, value); }

    public int Current { get => _current; set => Set(ref _current, value); }
    public bool IsBlueTurn { get => _current == 1; set { Current = value ? 1 : 0; OnPropertyChanged(); OnPropertyChanged(nameof(IsRedTurn)); } }
    public bool IsRedTurn { get => _current == 0; set { Current = value ? 0 : 1; OnPropertyChanged(); OnPropertyChanged(nameof(IsBlueTurn)); } }

    public double MaxSeconds { get => _maxSeconds; set => Set(ref _maxSeconds, Math.Clamp(value, 0.5, 60)); }
    public int MaxDepth { get => _maxDepth; set => Set(ref _maxDepth, Math.Clamp(value, 1, 100)); }
    public int Samples { get => _samples; set => Set(ref _samples, Math.Clamp(value, 1, 32)); }
    public bool Aggressive { get => _aggressive; set => Set(ref _aggressive, value); }

    public bool RuleSame { get => _ruleSame; set => Set(ref _ruleSame, value); }
    public bool RulePlus { get => _rulePlus; set => Set(ref _rulePlus, value); }
    public bool RuleReverse { get => _ruleReverse; set => Set(ref _ruleReverse, value); }
    public bool RuleFallenAce { get => _ruleFallenAce; set => Set(ref _ruleFallenAce, value); }
    public bool RuleAscension { get => _ruleAscension; set => Set(ref _ruleAscension, value); }
    public bool RuleDescension { get => _ruleDescension; set => Set(ref _ruleDescension, value); }
    public bool RuleOrder { get => _ruleOrder; set => Set(ref _ruleOrder, value); }
    public bool RuleChaos { get => _ruleChaos; set => Set(ref _ruleChaos, value); }

    public void OpenPicker(SlotViewModel slot)
    {
        if (IsBusy || PickCard is null) return;
        var picked = PickCard(slot);
        if (picked is null) return;

        if (picked.Clear)
        {
            slot.Card = null;
            slot.Unknown = false;
        }
        else if (picked.Unknown)
        {
            slot.Unknown = true;
            slot.Card = null;
        }
        else
        {
            slot.Card = picked.Card;
            slot.Unknown = false;
            if (slot.IsBoard && picked.Owner is 0 or 1) slot.Owner = picked.Owner;
        }
    }

    // ------------------------------------------------------------------ drag & drop

    /// <summary>Hand card → empty board cell, or board card → empty board cell.</summary>
    public void DropOnBoard(SlotViewModel from, SlotViewModel cell)
    {
        if (IsBusy || !cell.IsBoard || ReferenceEquals(from, cell)) return;
        if (from.Card is null || from.Unknown) return;
        if (cell.Card is not null) return; // occupied cells are not drop targets

        cell.Card = from.Card;
        cell.Owner = from.IsBoard ? from.Owner : from.Side;
        from.Card = null;
        from.Unknown = false;
        ClearHighlights();
    }

    /// <summary>Drop on a hand slot: hand→hand moves or swaps; board→hand returns the card to the hand.</summary>
    public void DropOnHandSlot(SlotViewModel from, SlotViewModel target)
    {
        if (IsBusy || target.IsBoard || ReferenceEquals(from, target)) return;
        if (from.Card is null || from.Unknown) return;

        if (from.IsBoard)
        {
            if (target.Card is not null || target.Unknown) return;
            target.Card = from.Card;
            from.Card = null;
            ClearHighlights();
            return;
        }

        // hand → hand: swap when the target slot is taken, otherwise move.
        var moved = from.Card;
        from.Card = target.Card;
        target.Card = moved;
        from.Unknown = false;
        target.Unknown = false;
        ClearHighlights();
    }

    // ------------------------------------------------------------------ saved decks

    /// <summary>Fills one side's hand with a saved deck (used by the 我的卡组 page).</summary>
    public void LoadDeck(SavedDeck deck, int side)
    {
        if (IsBusy) return;
        var hand = side == 1 ? HandBlue : HandRed;
        int slot = 0;
        foreach (int id in deck.CardIds)
        {
            if (slot >= hand.Count) break;
            var card = _library.Find(id);
            if (card is null) continue;
            hand[slot].Card = card;
            hand[slot].Unknown = false;
            slot++;
        }
        for (; slot < hand.Count; slot++)
        {
            hand[slot].Card = null;
            hand[slot].Unknown = false;
        }
        ClearHighlights();
        StatusText = $"已将「{deck.Name}」载入{(side == MySide ? "我方" : "对方")}手牌。";
    }

    private RuleSet BuildRuleSet()
    {
        var rules = RuleSet.None;
        if (RuleSame) rules |= RuleSet.Same;
        if (RulePlus) rules |= RuleSet.Plus;
        if (RuleReverse) rules |= RuleSet.Reverse;
        if (RuleFallenAce) rules |= RuleSet.FallenAce;
        if (RuleAscension) rules |= RuleSet.Ascension;
        if (RuleDescension) rules |= RuleSet.Descension;
        if (RuleOrder) rules |= RuleSet.Order;
        if (RuleChaos) rules |= RuleSet.Chaos;
        return rules;
    }

    private PositionSetup BuildSetup()
    {
        var setup = new PositionSetup
        {
            Current = Current,
            Rules = BuildRuleSet(),
        };
        foreach (var slot in BoardCells)
        {
            if (slot.Card is null) continue;
            setup.Board[slot.Cell] = new BoardCard { Card = slot.Card, Owner = slot.Owner };
        }
        foreach (var slot in HandBlue)
        {
            if (slot.Card is null && !slot.Unknown) continue;
            setup.HandBlue.Add(new HandCard { Card = slot.Card, Unknown = slot.Unknown, CanUse = slot.CanUse });
        }
        foreach (var slot in HandRed)
        {
            if (slot.Card is null && !slot.Unknown) continue;
            setup.HandRed.Add(new HandCard { Card = slot.Card, Unknown = slot.Unknown, CanUse = slot.CanUse });
        }
        return setup;
    }

    private async Task SolveAsync()
    {
        ClearHighlights();
        _solver ??= new SolverService(_library);
        var setup = BuildSetup();
        var settings = new SolveSettings
        {
            MaxSeconds = MaxSeconds,
            MaxDepth = MaxDepth,
            Aggressive = Aggressive,
            Samples = Samples,
        };

        _cts = new CancellationTokenSource();
        IsBusy = true;
        StatusText = "求解中…";
        DepthRows.Clear();

        try
        {
            var outcome = await Task.Run(() => _solver.Solve(setup, settings,
                onDepth: row => System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                    () =>
                    {
                        DepthRows.Add(row);
                        StatusText = $"搜索深度 {row.Depth}…  节点 {row.NodesTotal:N0}";
                    }),
                ct: _cts.Token));

            ApplyOutcome(setup, outcome);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消。";
        }
        catch (Exception ex)
        {
            StatusText = "求解失败。";
            MessageText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void ApplyOutcome(PositionSetup setup, SolveOutcome outcome)
    {
        // Replace the live rows with the engine's authoritative per-level stats.
        DepthRows.Clear();
        foreach (var row in outcome.Depths)
            DepthRows.Add(row);

        if (!outcome.HasMove)
        {
            ResultText = outcome.MoveText;
            StatsText = "";
            MessageText = outcome.Message;
            StatusText = "完成。";
            return;
        }

        var hand = Current == 1 ? HandBlue : HandRed;
        for (int i = 0; i < hand.Count; i++)
            hand[i].IsPlayed = i == outcome.BestSlot;
        int cell = outcome.Row * 3 + outcome.Col;
        if (cell >= 0 && cell < BoardCells.Count)
            BoardCells[cell].IsRecommended = true;

        ResultText = $"推荐：{outcome.CardName} → 第 {outcome.Row + 1} 行 第 {outcome.Col + 1} 列";
        StatsText =
            $"估值 {outcome.Score:0.00}   深度 {outcome.DepthCompleted}   节点 {outcome.Nodes:N0}   " +
            $"置换表命中 {outcome.TtHits:N0}   剪枝 {outcome.Cutoffs:N0}   耗时 {outcome.TotalMs:0} ms";
        if (outcome.Samples > 1) StatsText += $"   样本 {outcome.Samples}";
        if (outcome.Scenarios > 0) StatsText += $"   场景 {outcome.Scenarios}";
        MessageText = outcome.Message;
        StatusText = "完成。";
    }

    private void ClearHighlights()
    {
        foreach (var slot in BoardCells) slot.IsRecommended = false;
        foreach (var slot in HandBlue) slot.IsPlayed = false;
        foreach (var slot in HandRed) slot.IsPlayed = false;
    }

    private void ClearAll()
    {
        foreach (var slot in BoardCells)
        {
            slot.Card = null;
            slot.Owner = 1;
        }
        foreach (var slot in HandBlue.Concat(HandRed))
        {
            slot.Card = null;
            slot.Unknown = false;
            slot.CanUse = true;
        }
        DepthRows.Clear();
        ResultText = "";
        StatsText = "";
        MessageText = "";
        StatusText = "已清空。";
        ClearHighlights();
    }

    private void LoadDemo()
    {
        ClearAll();
        CardInfo? Find(int id) => _library.Find(id);
        var blue1 = Find(1);   // 渡渡鸟 4/2/3/4 (T/R/B/L per db T4 B3 L4 R2)
        var blue2 = Find(2);   // 冬贝利
        var red1 = Find(3);
        var red2 = Find(4);
        if (blue1 is null || blue2 is null) return;

        HandBlue[0].Card = blue1;
        HandBlue[1].Card = blue2;
        if (red1 is not null) HandRed[0].Card = red1;
        if (red2 is not null) HandRed[1].Card = red2;
        HandRed[2].Unknown = true;
        HandRed[3].Unknown = true;
        Current = 1;
        StatusText = "已载入示例局面：蓝方 2 张手牌，红方 2 明 2 暗。";
    }
}

/// <summary>Result of a card-picker dialog.</summary>
public sealed record PickedCard(CardInfo? Card, bool Unknown = false, bool Clear = false, int Owner = -1);
