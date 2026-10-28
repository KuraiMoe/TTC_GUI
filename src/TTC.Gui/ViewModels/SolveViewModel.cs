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
    private string _statusText = "轮到你出牌：拖动我方手牌到棋盘落子。";
    private string _resultText = "";
    private string _statsText = "";
    private string _messageText = "";
    private int _mySide = 1;
    private int _firstPlayer = 1;
    private bool _isGameActive;
    private double _progress;
    private CancellationTokenSource? _cts;

    // 落子记录：悔棋按记录还原盘面归属与手牌（翻面随走子精确还原）。
    private sealed record PlayRecord(CardInfo Card, int Side, int HandSlot, bool CanUse, int Cell, int[] OwnerBefore);

    private readonly Stack<PlayRecord> _history = new();

    /// <summary>已落到棋盘但尚未揭示的未知牌（等待弹窗选择）。</summary>
    private sealed record PendingPlay(int Cell, int Side, int HandSlot, bool CanUse, int[] OwnerBefore);

    private PendingPlay? _pending;

    /// <summary>棋盘上有未揭示的未知牌时，落子与求解都让位于揭示。</summary>
    public bool HasPendingReveal => _pending is not null;

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

        SolveCommand = new AsyncRelayCommand(SolveAsync, () => !IsBusy && _pending is null && (!IsGameActive || IsMyTurn));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        ClearCommand = new RelayCommand(ClearAll, () => !IsBusy);
        DemoCommand = new RelayCommand(LoadDemo, () => !IsBusy);
        UndoCommand = new RelayCommand(UndoLast, () => !IsBusy && CanUndo);
        StartGameCommand = new RelayCommand(ToggleGame, () => !IsBusy);
    }

    public ObservableCollection<SlotViewModel> BoardCells { get; } = new();
    public ObservableCollection<SlotViewModel> HandBlue { get; } = new();
    public ObservableCollection<SlotViewModel> HandRed { get; } = new();
    public ObservableCollection<DepthRow> DepthRows { get; } = new();

    /// <summary>The view wires this to the card picker dialog.</summary>
    public Func<SlotViewModel, PickedCard?>? PickCard { get; set; }

    /// <summary>Disabled in tests to avoid running the solver on simulated plays.</summary>
    public bool AutoSolve { get; set; } = true;

    public ICommand SolveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand DemoCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand StartGameCommand { get; }

    /// <summary>Which side is the user: 1 = 蓝方, 0 = 红方.</summary>
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
                OnPropertyChanged(nameof(IsMyTurn));
                OnPropertyChanged(nameof(TurnText));
                RaiseCommandStates();
            }
        }
    }

    public bool IsMySideBlue { get => MySide == 1; set { if (value) MySide = 1; OnPropertyChanged(); OnPropertyChanged(nameof(IsMySideRed)); } }
    public bool IsMySideRed { get => MySide == 0; set { if (value) MySide = 0; OnPropertyChanged(); OnPropertyChanged(nameof(IsMySideBlue)); } }

    public string BlueHandTitle => MySide == 1 ? "蓝方手牌（我方）" : "蓝方手牌（对方）";
    public string RedHandTitle => MySide == 0 ? "红方手牌（我方）" : "红方手牌（对方）";

    /// <summary>Turn tracking: 1 = 蓝方, 0 = 红方. Alternates with every played card.</summary>
    public int Current
    {
        get => _current;
        private set
        {
            if (Set(ref _current, value))
            {
                OnPropertyChanged(nameof(IsMyTurn));
                OnPropertyChanged(nameof(TurnText));
                RaiseCommandStates();
            }
        }
    }

    public bool IsMyTurn => Current == MySide;

    /// <summary>准备阶段可自由摆盘；点「开始对局」后进入回合制流程。</summary>
    public bool IsGameActive
    {
        get => _isGameActive;
        private set
        {
            if (Set(ref _isGameActive, value))
            {
                OnPropertyChanged(nameof(TurnText));
                OnPropertyChanged(nameof(StartGameButtonText));
                RaiseCommandStates();
            }
        }
    }

    /// <summary>谁先出牌（0 = 红方, 1 = 蓝方）。</summary>
    public int FirstPlayer
    {
        get => _firstPlayer;
        set
        {
            if (Set(ref _firstPlayer, value))
            {
                OnPropertyChanged(nameof(IsFirstBlue));
                OnPropertyChanged(nameof(IsFirstRed));
            }
        }
    }

    public bool IsFirstBlue { get => FirstPlayer == 1; set { if (value) FirstPlayer = 1; OnPropertyChanged(); OnPropertyChanged(nameof(IsFirstRed)); } }
    public bool IsFirstRed { get => FirstPlayer == 0; set { if (value) FirstPlayer = 0; OnPropertyChanged(); OnPropertyChanged(nameof(IsFirstBlue)); } }

    public string StartGameButtonText => IsGameActive ? "结束对局" : "开始对局";

    /// <summary>求解进度（按时间预算估算），仅求解中有效。</summary>
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public string TurnText
    {
        get
        {
            if (!IsGameActive) return "准备阶段：摆好初始局面后点「开始对局」";
            return IsMyTurn
                ? (Current == 1 ? "当前轮到：蓝方（我方）" : "当前轮到：红方（我方）")
                : (Current == 1 ? "等待蓝方出牌（对方）…" : "等待红方出牌（对方）…");
        }
    }

    public bool CanUndo => (_history.Count > 0 || _pending is not null) && !IsBusy;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value)) RaiseCommandStates();
        }
    }

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string ResultText { get => _resultText; set => Set(ref _resultText, value); }
    public string StatsText { get => _statsText; set => Set(ref _statsText, value); }
    public string MessageText { get => _messageText; set => Set(ref _messageText, value); }

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

    private void RaiseCommandStates()
    {
        (SolveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); // canexecute also depends on _pending
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DemoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UndoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StartGameCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void OpenPicker(SlotViewModel slot)
    {
        if (IsBusy || PickCard is null) return;

        // 棋盘上待揭示的未知牌：选择后切换为实际卡牌并完成这一手。
        if (_pending is not null && slot.IsBoard && slot.Cell == _pending.Cell)
        {
            var pick = PickCard(slot);
            if (pick?.Card is not { } revealed)
            {
                StatusText = "未选择卡牌：已保持未知牌，点击棋盘上的「？」可继续揭示。";
                return;
            }
            var record = _pending;
            _pending = null;
            OnPropertyChanged(nameof(HasPendingReveal));
            var handSlot = record.Side == 1 ? HandBlue[record.HandSlot] : HandRed[record.HandSlot];
            handSlot.CanUse = record.CanUse;
            CompletePlay(handSlot, slot, revealed, record.OwnerBefore);
            return;
        }

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
        _history.Clear();
        OnPropertyChanged(nameof(CanUndo));
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

    private PositionSetup BuildSetup() => BuildSetup(out _);

    /// <summary>
    /// Builds the solver setup. Empty hand slots are skipped (the engine hand is dense), so
    /// <paramref name="slotMap"/> maps UI hand slots to engine slot indexes (−1 = empty slot).
    /// </summary>
    private PositionSetup BuildSetup(out int[][] slotMap)
    {
        slotMap = new[] { new int[5], new int[5] };
        for (int p = 0; p < 2; p++)
            for (int i = 0; i < 5; i++)
                slotMap[p][i] = -1;

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
        AddHand(setup.HandBlue, HandBlue, 1, slotMap);
        AddHand(setup.HandRed, HandRed, 0, slotMap);
        return setup;
    }

    private static void AddHand(List<HandCard> target, ObservableCollection<SlotViewModel> source, int side, int[][] slotMap)
    {
        foreach (var slot in source)
        {
            if (slot.Card is null && !slot.Unknown) continue;
            slotMap[side][slot.HandSlot] = target.Count;
            target.Add(new HandCard { Card = slot.Card, Unknown = slot.Unknown, CanUse = slot.CanUse });
        }
    }
    /// <summary>Plays a hand card onto an empty cell with full rule flips (引擎 MakeMove 计算)。</summary>
    public void DropOnBoard(SlotViewModel from, SlotViewModel cell)
    {
        if (IsBusy || !cell.IsBoard || ReferenceEquals(from, cell)) return;
        if (from.Card is null && !from.Unknown) return;

        if (from.IsBoard)
        {
            StatusText = "棋盘上的牌不能移动；如需撤回上一手请拖回手牌或点「悔棋」。";
            return;
        }
        if (cell.Card is not null || cell.Unknown) return;
        if (_pending is not null)
        {
            StatusText = "棋盘上还有未揭示的未知牌：请先点击它完成揭示。";
            return;
        }
        if (IsGameActive && from.Side != Current)
        {
            StatusText = $"现在轮到{(Current == MySide ? "你" : "对方")}出牌，只能拖{(Current == 1 ? "蓝方" : "红方")}的手牌落子。";
            return;
        }

        int[] ownerBefore = CaptureOwners();

        // 未知牌：先以未知牌落到棋盘，再弹窗揭示它到底是什么。
        if (from.Unknown)
        {
            cell.Unknown = true;
            cell.Owner = from.Side;
            from.Card = null;
            from.Unknown = false;
            _pending = new PendingPlay(cell.Cell, from.Side, from.HandSlot, from.CanUse, ownerBefore);
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(HasPendingReveal));
            RaiseCommandStates();
            StatusText = "已放置未知牌，请在弹窗中选择这张牌是什么（点棋盘上的「？」可重新选择）。";
            OpenPicker(cell);
            return;
        }

        CompletePlay(from, cell, from.Card!, ownerBefore);
    }

    /// <summary>盘面归属快照（-1 = 空格），用于悔棋还原。</summary>
    private int[] CaptureOwners()
    {
        var owners = new int[9];
        for (int i = 0; i < 9; i++)
            owners[i] = BoardCells[i].Card is null && !BoardCells[i].Unknown ? -1 : BoardCells[i].Owner;
        return owners;
    }

    /// <summary>完成一手：按规则翻面并推进回合。</summary>
    private void CompletePlay(SlotViewModel from, SlotViewModel cell, CardInfo card, int[] ownerBefore)
    {
        from.Card = card;
        from.Unknown = false;

        // 用一次性局面按规则计算翻面：行棋方即快照当前方，引擎翻转逻辑与游戏一致。
        var snap = SolverService.BuildSnapshot(BuildSetup(out var slotMap));
        snap.Current = from.Side;
        var g = GameState.FromSnapshot(snap);

        int gameSlot = slotMap[from.Side][from.HandSlot];
        if (gameSlot < 0) return;
        g.MakeMove(new Move(g.HandInstance(from.Side, gameSlot), cell.Cell));

        _history.Push(new PlayRecord(card, from.Side, from.HandSlot, from.CanUse, cell.Cell, ownerBefore));

        cell.Card = card;
        cell.Owner = from.Side;
        from.Card = null;
        from.Unknown = false;
        SyncOwnersFrom(g); // 按规则翻转相邻敌牌
        ClearHighlights();

        if (!IsGameActive)
        {
            StatusText = "已落子（准备阶段，不切换回合）。";
            return;
        }

        Current = 1 - Current;
        StatusText = IsMyTurn
            ? "对方已落子。"
            : "已落子，等待对方出牌——请把对方在游戏里打出的牌拖到棋盘上。";

        if (IsMyTurn && AutoSolve)
        {
            StatusText = "对方已落子，正在自动重新求解…";
            _ = SolveAsync();
        }
    }
    /// <summary>把引擎局面的归属同步回界面（翻面结果）。</summary>
    private void SyncOwnersFrom(GameState g)
    {
        for (int cell = 0; cell < 9; cell++)
        {
            int inst = g.BoardInstance(cell);
            if (inst >= 0) BoardCells[cell].Owner = g.InstanceOwner(inst) == OwnerId.Blue ? 1 : 0;
        }
        OnPropertyChanged(nameof(CanUndo));
        RaiseCommandStates();
    }

    /// <summary>Hand → hand swaps the two cards; a board card dragged back undoes the last play.</summary>
    public void DropOnHandSlot(SlotViewModel from, SlotViewModel target)
    {
        if (IsBusy || target.IsBoard || ReferenceEquals(from, target)) return;

        if (from.IsBoard)
        {
            UndoIfLast(from);
            return;
        }
        if (from.Card is null || from.Unknown) return;

        var moved = from.Card;
        from.Card = target.Card;
        target.Card = moved;
        from.Unknown = false;
        target.Unknown = false;
        _history.Clear();
        OnPropertyChanged(nameof(CanUndo));
        ClearHighlights();
    }

    /// <summary>True when this board card is the most recent play (the only one undo can take back).</summary>
    public bool CanUndoFrom(SlotViewModel boardSlot) =>
        boardSlot.IsBoard &&
        ((_pending is not null && _pending.Cell == boardSlot.Cell) ||
         (_history.Count > 0 && _history.Peek().Cell == boardSlot.Cell));

    /// <summary>The hand slot the last play came from (undo returns the card here).</summary>
    public SlotViewModel? UndoTargetSlot
    {
        get
        {
            if (_pending is not null)
                return _pending.Side == 1 ? HandBlue[_pending.HandSlot] : HandRed[_pending.HandSlot];
            if (_history.Count == 0) return null;
            var record = _history.Peek();
            return record.Side == 1 ? HandBlue[record.HandSlot] : HandRed[record.HandSlot];
        }
    }

    private void UndoIfLast(SlotViewModel boardSlot)
    {
        if (!CanUndoFrom(boardSlot))
        {
            StatusText = "只能撤回最近一手：请拖回最后落下的那张牌，或点「悔棋」。";
            return;
        }
        UndoLast();
    }

    private void UndoLast()
    {
        // 未揭示的未知牌：撤回即退回手牌的未知牌状态。
        if (_pending is not null)
        {
            var pending = _pending;
            _pending = null;
            var cell = BoardCells[pending.Cell];
            cell.Card = null;
            cell.Unknown = false;
            var handSlot = pending.Side == 1 ? HandBlue[pending.HandSlot] : HandRed[pending.HandSlot];
            handSlot.Unknown = true;
            handSlot.CanUse = pending.CanUse;
            StatusText = "已撤回未揭示的未知牌。";
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(HasPendingReveal));
            RaiseCommandStates();
            return;
        }

        if (_history.Count == 0)
        {
            StatusText = "没有可悔的棋。";
            return;
        }

        var record = _history.Pop();
        BoardCells[record.Cell].Card = null;
        for (int c = 0; c < 9; c++)
        {
            if (record.OwnerBefore[c] >= 0 && BoardCells[c].Card is not null)
                BoardCells[c].Owner = record.OwnerBefore[c];
        }
        var slot = record.Side == 1 ? HandBlue[record.HandSlot] : HandRed[record.HandSlot];
        slot.Card = record.Card;
        slot.CanUse = record.CanUse;
        ClearHighlights();

        if (IsGameActive) Current = 1 - Current;
        StatusText = $"已撤回上一手，轮到{(IsMyTurn ? "你" : "对方")}出牌。";
        OnPropertyChanged(nameof(CanUndo));
        RaiseCommandStates();
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
        _history.Clear();
        OnPropertyChanged(nameof(CanUndo));
        ClearHighlights();
        StatusText = $"已将「{deck.Name}」载入{(side == MySide ? "我方" : "对方")}手牌。";
    }

    // ------------------------------------------------------------------ solve

    private async Task SolveAsync()
    {
        ClearHighlights();
        _solver ??= new SolverService(_library);
        var setup = BuildSetup(out var slotMap);
        double budget = MaxSeconds;
        var settings = new SolveSettings
        {
            MaxSeconds = budget,
            MaxDepth = MaxDepth,
            Aggressive = Aggressive,
            Samples = Samples,
        };

        _cts = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        ResultText = "";
        StatsText = "";
        MessageText = "";
        StatusText = "求解中…";
        DepthRows.Clear();

        try
        {
            var outcome = await Task.Run(() => _solver.Solve(setup, settings,
                onDepth: row => System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                    () =>
                    {
                        DepthRows.Add(row);
                        Progress = Math.Min(99, row.ElapsedSeconds / Math.Max(0.1, budget) * 100);
                        StatusText = $"搜索深度 {row.Depth}…  节点 {row.NodesTotal:N0}";
                    }),
                ct: _cts.Token));

            ApplyOutcome(outcome, slotMap);
            Progress = 100;
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

    private void ApplyOutcome(SolveOutcome outcome, int[][] slotMap)
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
        // slotMap[side][uiSlot] = engine slot; invert it to highlight the right UI slot.
        int uiSlot = -1;
        for (int i = 0; i < hand.Count; i++)
        {
            if (slotMap[Current][i] == outcome.BestSlot) { uiSlot = i; break; }
        }
        for (int i = 0; i < hand.Count; i++)
            hand[i].IsPlayed = i == uiSlot;
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
        _history.Clear();
        OnPropertyChanged(nameof(CanUndo));
        ResetTurn();
        StatusText = "已清空。请设置初始局面后点「开始对局」。";
        ClearHighlights();
    }

    private void LoadDemo()
    {
        ClearAll();
        CardInfo? Find(int id) => _library.Find(id);
        var blue1 = Find(1);   // 渡渡鸟
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
        ResetTurn();
        StatusText = "已载入示例局面：蓝方 2 张手牌，红方 2 明 2 暗。轮到你出牌。";
    }

    private void ResetTurn()
    {
        IsGameActive = false;
        Current = MySide;
    }

    /// <summary>开局前的手牌快照（含未知牌与 canUse），结束对局时恢复。</summary>
    private sealed record HandSlotSnapshot(CardInfo? Card, bool Unknown, bool CanUse);

    private HandSlotSnapshot[]? _preGameBlue;
    private HandSlotSnapshot[]? _preGameRed;

    private HandSlotSnapshot[] SnapshotHand(ObservableCollection<SlotViewModel> hand) =>
        hand.Select(s => new HandSlotSnapshot(s.Card, s.Unknown, s.CanUse)).ToArray();

    private static void RestoreHand(ObservableCollection<SlotViewModel> hand, HandSlotSnapshot[] snapshot)
    {
        for (int i = 0; i < hand.Count; i++)
        {
            var saved = i < snapshot.Length ? snapshot[i] : new HandSlotSnapshot(null, false, true);
            hand[i].Card = saved.Card;
            hand[i].Unknown = saved.Unknown;
            hand[i].CanUse = saved.CanUse;
        }
    }

    private void ToggleGame()
    {
        if (IsGameActive)
        {
            IsGameActive = false;
            RestoreForNewMatch();
            StatusText = "已结束对局：棋盘已清空，双方牌组已恢复为开局前状态。";
            return;
        }
        _preGameBlue = SnapshotHand(HandBlue);
        _preGameRed = SnapshotHand(HandRed);
        Current = FirstPlayer;
        IsGameActive = true;
        StatusText = IsMyTurn
            ? "对局开始，轮到你出牌：拖动我方手牌落子（可先点「开始求解」获取推荐）。"
            : "对局开始，对方先手——请把对方打出的牌拖到棋盘上。";
    }

    /// <summary>结束对局：清空棋盘，并把双方牌组（含未知牌）恢复到开局前。</summary>
    private void RestoreForNewMatch()
    {
        foreach (var cell in BoardCells)
        {
            cell.Card = null;
            cell.Unknown = false;
            cell.Owner = 1;
        }
        if (_preGameBlue is not null) RestoreHand(HandBlue, _preGameBlue);
        if (_preGameRed is not null) RestoreHand(HandRed, _preGameRed);

        _history.Clear();
        _pending = null;
        DepthRows.Clear();
        ResultText = "";
        StatsText = "";
        MessageText = "";
        Progress = 0;
        Current = MySide;
        ClearHighlights();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(HasPendingReveal));
        RaiseCommandStates();
    }
}

/// <summary>Result of a card-picker dialog.</summary>
public sealed record PickedCard(CardInfo? Card, bool Unknown = false, bool Clear = false, int Owner = -1);
