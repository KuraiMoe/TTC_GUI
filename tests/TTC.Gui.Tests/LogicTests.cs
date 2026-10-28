using System.IO;
using TTC.Core;
using TTC.Gui.Models;
using TTC.Gui.Services;
using TTC.Gui.ViewModels;

namespace TTC.Gui.Tests;

public class CardLibraryTests
{
    [Fact]
    public void LoadsFullDatabase()
    {
        var lib = CardLibrary.Load();
        Assert.True(lib.Cards.Count >= 400, $"cards: {lib.Cards.Count}");
    }

    [Fact]
    public void FindsKnownCard()
    {
        var lib = CardLibrary.Load();
        var card = lib.Find(1);
        Assert.NotNull(card);
        Assert.Equal("渡渡鸟", card!.Name);
        Assert.Equal(4, card.Up);
        Assert.Equal(2, card.Right);
        Assert.Equal(3, card.Down);
        Assert.Equal(4, card.Left);
    }

    [Fact]
    public void SearchMatchesPinyinInitials()
    {
        var lib = CardLibrary.Load();
        var hits = lib.Search("ddn");
        Assert.Contains(hits, c => c.Name == "渡渡鸟");

        hits = lib.Search("dbl");
        Assert.Contains(hits, c => c.Name == "冬贝利");

        hits = lib.Search("msj");
        Assert.Contains(hits, c => c.Name == "魔石精");
    }

    [Fact]
    public void SearchMatchesFullPinyin()
    {
        var lib = CardLibrary.Load();
        var hits = lib.Search("duduniao");
        Assert.Contains(hits, c => c.Name == "渡渡鸟");
    }

    [Fact]
    public void SearchFiltersByStar()
    {
        var lib = CardLibrary.Load();
        var stars = lib.Search(null, 5, 100);
        Assert.NotEmpty(stars);
        Assert.All(stars, c => Assert.Equal(5, c.Star));
    }
}

public class SolverServiceTests
{
    private static readonly CardLibrary Lib = CardLibrary.Load();

    private static PositionSetup EmptySetup()
    {
        var setup = new PositionSetup { Current = 1 };
        setup.HandBlue.Add(new HandCard { Card = Lib.Find(1) });
        setup.HandBlue.Add(new HandCard { Card = Lib.Find(2) });
        setup.HandRed.Add(new HandCard { Card = Lib.Find(3) });
        setup.HandRed.Add(new HandCard { Card = Lib.Find(4) });
        return setup;
    }

    [Fact]
    public void SolvesEmptyBoardAndReturnsMove()
    {
        var solver = new SolverService(Lib);
        var setup = EmptySetup();
        var outcome = solver.Solve(setup, new SolveSettings { MaxSeconds = 1.0, MaxDepth = 8 });

        Assert.True(outcome.HasMove);
        Assert.InRange(outcome.BestSlot, 0, 1);
        Assert.InRange(outcome.Row, 0, 2);
        Assert.InRange(outcome.Col, 0, 2);
        Assert.True(outcome.DepthCompleted >= 1);
        Assert.True(outcome.Nodes > 0);
    }

    [Fact]
    public void RejectsUnknownOwnHand()
    {
        var solver = new SolverService(Lib);
        var setup = EmptySetup();
        setup.HandBlue[0].Unknown = true;
        Assert.Throws<InvalidOperationException>(() =>
            solver.Solve(setup, new SolveSettings { MaxSeconds = 0.5, MaxDepth = 4 }));
    }

    [Fact]
    public void SamplesUnknownOpponentHand()
    {
        var solver = new SolverService(Lib);
        var setup = EmptySetup();
        setup.HandRed[0].Unknown = true;
        setup.HandRed[1].Unknown = true;
        var outcome = solver.Solve(setup, new SolveSettings { MaxSeconds = 2.0, MaxDepth = 5, Samples = 4 });

        Assert.True(outcome.HasMove);
        Assert.Equal(4, outcome.Samples);
        Assert.Contains("未知牌", outcome.Message);
    }

    [Fact]
    public void RobustEndgameEnumeratesScenarios()
    {
        var solver = new SolverService(Lib);
        var setup = EmptySetup();

        // 5 occupied cells -> robust endgame path with two unknown opponent cards.
        for (int i = 0; i < 5; i++)
            setup.Board[i] = new BoardCard { Card = Lib.Find(10 + i) ?? Lib.Find(1)!, Owner = i % 2 };
        setup.HandRed[0].Unknown = true;
        setup.HandRed[1].Unknown = true;

        var outcome = solver.Solve(setup, new SolveSettings { MaxSeconds = 3.0, MaxDepth = 10 });
        Assert.True(outcome.HasMove);
        Assert.True(outcome.Scenarios > 0, $"scenarios: {outcome.Scenarios}");
    }

    [Fact]
    public void AppliesTypeModifiersForAscension()
    {
        var solver = new SolverService(Lib);
        var setup = EmptySetup();
        setup.Rules = RuleSet.Ascension;
        var outcome = solver.Solve(setup, new SolveSettings { MaxSeconds = 0.5, MaxDepth = 6 });
        Assert.True(outcome.HasMove);
    }

    [Fact]
    public void RespectsRuleSetLikeReverse()
    {
        var solver = new SolverService(Lib);
        var setup = EmptySetup();
        setup.Rules = RuleSet.Reverse;
        var outcome = solver.Solve(setup, new SolveSettings { MaxSeconds = 0.5, MaxDepth = 8 });
        Assert.True(outcome.HasMove);
    }
}

public class DraftAnalyzerTests
{
    private static readonly CardLibrary Lib = CardLibrary.Load();
    private static readonly DraftAnalyzer Analyzer = new(Lib);

    private static CardInfo Card(int id) => Lib.Find(id) ?? throw new InvalidOperationException($"no card {id}");

    [Fact]
    public void SingleCardBaseMatchesFormula()
    {
        // 渡渡鸟 U4 R2 D3 L4, star 1: avg 3.25, high=0, low=2 (2,3), no adjacency, no high>=3
        // score = 3.25*6 + 0*8 - 2*4 + 1*2.5 = 19.5 - 8 + 2.5 = 14.0
        double score = Analyzer.SingleCardBase(Card(1));
        Assert.Equal(14.0, score, 2);
    }

    [Fact]
    public void DeckLegalEnforcesStarLimits()
    {
        var fiveHigh = Lib.Search(null, 5, 5).Take(5).ToList();
        if (fiveHigh.Count == 5)
        {
            var (legal, warnings) = Analyzer.DeckLegal(fiveHigh);
            Assert.False(legal);
            Assert.NotEmpty(warnings);
        }

        var stars1 = Lib.Search(null, 1, 5).ToList();
        Assert.Equal(5, stars1.Count);
        Assert.True(Analyzer.DeckLegal(stars1).Legal);
    }

    [Fact]
    public void ExpandSelectionBuildsTwentySevenDecks()
    {
        var stages = new List<IReadOnlyList<IReadOnlyList<CardInfo>>>();
        int id = 10;
        foreach (int stage in new[] { 2, 2, 1 })
        {
            var choices = new List<IReadOnlyList<CardInfo>>();
            for (int choice = 0; choice < 3; choice++)
            {
                var cards = new List<CardInfo>();
                for (int k = 0; k < stage; k++) cards.Add(Card(id++));
                choices.Add(cards);
            }
            stages.Add(choices);
        }
        var combos = DraftAnalyzer.ExpandSelection(stages);
        Assert.Equal(27, combos.Count);
        Assert.All(combos, c => Assert.Equal(5, c.Deck.Count));
    }

    [Fact]
    public void AnalyzeSelectionRanksDecksWithComponents()
    {
        var stages = new List<IReadOnlyList<IReadOnlyList<CardInfo>>>();
        var star1 = Lib.Search(null, 1, 30).ToList();
        var star2 = Lib.Search(null, 2, 30).ToList();
        int cursor = 0;
        foreach (int stage in new[] { 2, 2, 1 })
        {
            var choices = new List<IReadOnlyList<CardInfo>>();
            for (int choice = 0; choice < 3; choice++)
            {
                var cards = new List<CardInfo>();
                for (int k = 0; k < stage; k++)
                    cards.Add((cursor % 2 == 0 ? star1 : star2)[(cursor * 3 + k) % (cursor % 2 == 0 ? star1.Count : star2.Count)]);
                cursor++;
                choices.Add(cards);
            }
            stages.Add(choices);
        }

        var result = Analyzer.AnalyzeSelection(stages, new[] { "选拔", "加算" }, timeBudgetSeconds: 5.0);
        Assert.NotNull(result.Best);
        Assert.Equal(5, result.Best!.Deck.Count);
        Assert.Equal(27, result.Results.Count);
        Assert.Contains("base", result.Best.Components.Keys);
        Assert.Contains("verification", result.Best.Components.Keys);
        Assert.InRange(result.Best.FinalScore, 0, 100);
        Assert.Contains(result.Best, result.Results);
    }

    [Fact]
    public void RuleScoreRewardsSameRule()
    {
        // 冬贝利 U2 R2 D7 L2: distinct values {2,7} -> dup = 4 - 2 = 2
        var (score, reasons) = Analyzer.RuleScoreForCard(Card(2), new[] { "选拔", "同数" });
        Assert.True(score > 45);
        Assert.Contains("重复边值适合同数", reasons);
    }
}


public class DeckStoreTests
{
    [Fact]
    public void RoundTripsSavedDecks()
    {
        string path = Path.Combine(Path.GetTempPath(), "ttc_gui_test_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new DeckStore(path);
            store.Save(new[]
            {
                new SavedDeck { Name = "deck-a", CardIds = new List<int> { 1, 2, 3, 4, 5 } },
                new SavedDeck { Name = "deck-b", CardIds = new List<int> { 10, 20, 30 } },
            });
            var loaded = new DeckStore(path).Load();
            Assert.Equal(2, loaded.Count);
            Assert.Equal("deck-a", loaded[0].Name);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, loaded[0].CardIds);
            Assert.Equal(3, loaded[1].CardIds.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class DragMoveTests
{
    private static readonly CardLibrary Lib = CardLibrary.Load();

    private static SolveViewModel NewVm() => new(Lib) { AutoSolve = false };

    private static (CardInfo Attacker, CardInfo Victim) FindFlipPair()
    {
        // 找一对「落子可翻面」的卡：攻击方 Up 大于受害方 Down。
        var cards = Lib.ValidCards();
        foreach (var victim in cards)
            foreach (var attacker in cards)
                if (attacker.Up > victim.Down && attacker.Id != victim.Id)
                    return (attacker, victim);
        throw new InvalidOperationException("no flip pair in database");
    }

    [Fact]
    public void HandToBoardMovesCardAndSetsOwner()
    {
        var vm = NewVm();
        vm.HandBlue[0].Card = Lib.Find(1);
        vm.DropOnBoard(vm.HandBlue[0], vm.BoardCells[4]);

        Assert.Equal(1, vm.BoardCells[4].Card!.Id);
        Assert.Equal(1, vm.BoardCells[4].Owner);
        Assert.Null(vm.HandBlue[0].Card);
    }

    [Fact]
    public void BoardToBoardIsRejected()
    {
        var vm = NewVm();
        vm.StartGameCommand.Execute(null);
        vm.HandBlue[0].Card = Lib.Find(3);
        vm.DropOnBoard(vm.HandBlue[0], vm.BoardCells[0]); // 蓝方出牌 → 轮到红方
        Assert.Equal(0, vm.Current); // 轮到红方

        // 红方行动轮内也不允许移动棋盘上的牌
        vm.DropOnBoard(vm.BoardCells[0], vm.BoardCells[8]);

        Assert.Equal(3, vm.BoardCells[0].Card!.Id);
        Assert.Null(vm.BoardCells[8].Card);
    }

    [Fact]
    public void HandSlotMappingSurvivesGaps()
    {
        var vm = NewVm();
        vm.HandBlue[0].Card = Lib.Find(1);
        vm.HandBlue[2].Card = Lib.Find(2); // 空出 1 号位形成间隔

        vm.DropOnBoard(vm.HandBlue[0], vm.BoardCells[0]); // 蓝方 → 红方回合
        vm.HandRed[0].Card = Lib.Find(3);
        vm.DropOnBoard(vm.HandRed[0], vm.BoardCells[1]); // 红方 → 蓝方回合

        vm.DropOnBoard(vm.HandBlue[2], vm.BoardCells[5]); // 间隔后的 UI 槽位 2

        Assert.Equal(2, vm.BoardCells[5].Card!.Id); // 落下的必须是 2 号卡
        Assert.Null(vm.HandBlue[2].Card);
    }

    [Fact]
    public void HandToHandSwapsWhenTargetOccupied()
    {
        var vm = NewVm();
        var a = Lib.Find(1)!;
        var b = Lib.Find(2)!;
        vm.HandBlue[0].Card = a;
        vm.HandBlue[1].Card = b;

        vm.DropOnHandSlot(vm.HandBlue[0], vm.HandBlue[1]);

        Assert.Equal(b, vm.HandBlue[0].Card);
        Assert.Equal(a, vm.HandBlue[1].Card);
    }

    [Fact]
    public void OccupiedBoardCellRejectsDrop()
    {
        var vm = NewVm();
        vm.HandBlue[0].Card = Lib.Find(1);
        vm.HandBlue[1].Card = Lib.Find(2);

        vm.DropOnBoard(vm.HandBlue[0], vm.BoardCells[0]); // 蓝方落子，轮到红方
        vm.HandRed[0].Card = Lib.Find(4);
        vm.DropOnBoard(vm.HandRed[0], vm.BoardCells[0]); // 目标已占 → 拒绝

        Assert.Equal(1, vm.BoardCells[0].Card!.Id);
        Assert.NotNull(vm.HandRed[0].Card);
    }

    [Fact]
    public void OnlySideToMoveMayPlay()
    {
        var vm = NewVm();
        vm.StartGameCommand.Execute(null); // 轮到蓝方（我方）
        vm.HandRed[0].Card = Lib.Find(3);

        vm.DropOnBoard(vm.HandRed[0], vm.BoardCells[0]); // 红方在蓝方回合落子 → 拒绝

        Assert.Null(vm.BoardCells[0].Card);
        Assert.NotNull(vm.HandRed[0].Card);
    }

    [Fact]
    public void TurnFlowGatesSolving()
    {
        var vm = NewVm();
        vm.StartGameCommand.Execute(null); // 开始对局：我方先手
        Assert.True(vm.IsMyTurn);
        Assert.True(vm.SolveCommand.CanExecute(null));

        vm.HandBlue[0].Card = Lib.Find(1);
        vm.DropOnBoard(vm.HandBlue[0], vm.BoardCells[0]); // 己方出牌

        Assert.False(vm.IsMyTurn);
        Assert.False(vm.SolveCommand.CanExecute(null)); // 等待对方出牌时不能求解

        vm.HandRed[0].Card = Lib.Find(3);
        vm.DropOnBoard(vm.HandRed[0], vm.BoardCells[1]); // 对方出牌

        Assert.True(vm.IsMyTurn);                       // 自动切回我方
        Assert.True(vm.SolveCommand.CanExecute(null));
    }

    [Fact]
    public void FlipAppliesOnPlay()
    {
        var (attacker, victim) = FindFlipPair();
        var vm = NewVm();
        vm.BoardCells[1].Card = victim;
        vm.BoardCells[1].Owner = 0; // 红方在(0,1)
        vm.HandBlue[0].Card = attacker;

        vm.DropOnBoard(vm.HandBlue[0], vm.BoardCells[4]); // 蓝方在(1,1)，Up 对敌方 Down

        Assert.Equal(1, vm.BoardCells[1].Owner); // 相邻敌牌按规则翻面
    }

    [Fact]
    public void UndoRestoresFlipsAndHand()
    {
        var (attacker, victim) = FindFlipPair();
        var vm = NewVm();
        vm.StartGameCommand.Execute(null);
        vm.BoardCells[1].Card = victim;
        vm.BoardCells[1].Owner = 0;
        vm.HandBlue[0].Card = attacker;

        vm.DropOnBoard(vm.HandBlue[0], vm.BoardCells[4]);
        Assert.Equal(1, vm.BoardCells[1].Owner);

        Assert.True(vm.CanUndoFrom(vm.BoardCells[4]));
        vm.DropOnHandSlot(vm.BoardCells[4], vm.HandBlue[3]); // 拖回手牌 = 悔棋

        Assert.Null(vm.BoardCells[4].Card);
        Assert.Equal(0, vm.BoardCells[1].Owner); // 翻面还原
        Assert.Equal(attacker.Id, vm.HandBlue[0].Card!.Id); // 回到原手牌位
        Assert.True(vm.IsMyTurn); // 回合还原
    }

    [Fact]
    public void EndGameClearsBoardAndRestoresHands()
    {
        var vm = NewVm();
        // 开局前：手牌 2 张明牌 + 1 张未知牌，棋盘预摆 1 张
        vm.HandBlue[0].Card = Lib.Find(1);
        vm.HandBlue[1].Card = Lib.Find(2);
        vm.HandRed[0].Card = Lib.Find(3);
        vm.HandRed[1].Unknown = true;
        vm.BoardCells[0].Card = Lib.Find(5);
        vm.BoardCells[0].Owner = 0;

        vm.StartGameCommand.Execute(null); // 开始对局

        vm.DropOnBoard(vm.HandBlue[0], vm.BoardCells[4]); // 蓝方落子
        vm.HandRed[2].Card = Lib.Find(4);
        vm.DropOnBoard(vm.HandRed[2], vm.BoardCells[5]); // 红方落子
        Assert.NotNull(vm.BoardCells[4].Card);

        vm.StartGameCommand.Execute(null); // 结束对局

        // 棋盘清空（对局与预摆一并清掉）
        Assert.All(vm.BoardCells, c => Assert.Null(c.Card));
        // 双方牌组恢复为开局前，含未知牌
        Assert.Equal(1, vm.HandBlue[0].Card!.Id);
        Assert.Equal(2, vm.HandBlue[1].Card!.Id);
        Assert.Equal(3, vm.HandRed[0].Card!.Id);
        Assert.True(vm.HandRed[1].Unknown);
        Assert.Null(vm.HandRed[2].Card);
    }

    [Fact]
    public void OpponentMayStartFirst()
    {
        var vm = NewVm();
        vm.FirstPlayer = 0; // 对方先手
        vm.StartGameCommand.Execute(null);

        Assert.Equal(0, vm.Current);
        Assert.False(vm.IsMyTurn);
        Assert.False(vm.SolveCommand.CanExecute(null)); // 等对方出牌时不能求解

        vm.HandRed[0].Card = Lib.Find(3);
        vm.DropOnBoard(vm.HandRed[0], vm.BoardCells[0]); // 对方先出牌

        Assert.True(vm.IsMyTurn); // 自动切回我方，可求解
        Assert.True(vm.SolveCommand.CanExecute(null));
    }

    [Fact]
    public void UnknownCardRevealsOnPlay()
    {
        var vm = NewVm();
        vm.PickCard = _ => new PickedCard(Lib.Find(5)); // 弹窗揭示为 5 号卡
        vm.HandRed[1].Unknown = true;

        vm.DropOnBoard(vm.HandRed[1], vm.BoardCells[6]);

        Assert.Equal(5, vm.BoardCells[6].Card!.Id);
        Assert.False(vm.HandRed[1].Unknown);
        Assert.Null(vm.HandRed[1].Card);
    }

    [Fact]
    public void UnknownPlayCancelledWithoutPick()
    {
        var vm = NewVm();
        vm.PickCard = _ => null; // 弹窗取消
        vm.HandRed[1].Unknown = true;

        vm.DropOnBoard(vm.HandRed[1], vm.BoardCells[6]);

        // 先以未知牌落到棋盘，保持待揭示状态
        Assert.Null(vm.BoardCells[6].Card);
        Assert.True(vm.BoardCells[6].Unknown);
        Assert.False(vm.HandRed[1].Unknown);
        Assert.True(vm.HasPendingReveal);

        // 点击棋盘上的「？」可继续揭示
        vm.PickCard = _ => new PickedCard(Lib.Find(5));
        vm.OpenPicker(vm.BoardCells[6]);
        Assert.Equal(5, vm.BoardCells[6].Card!.Id);
        Assert.False(vm.BoardCells[6].Unknown);
        Assert.False(vm.HasPendingReveal);
    }

    [Fact]
    public void PendingUnknownBlocksOtherPlays()
    {
        var vm = NewVm();
        vm.PickCard = _ => null;
        vm.HandRed[1].Unknown = true;
        vm.DropOnBoard(vm.HandRed[1], vm.BoardCells[6]);

        vm.HandBlue[0].Card = Lib.Find(1);
        vm.DropOnBoard(vm.HandBlue[0], vm.BoardCells[0]); // 待揭示期间不能落子

        Assert.Null(vm.BoardCells[0].Card);
        Assert.NotNull(vm.HandBlue[0].Card);
    }

    [Fact]
    public void PendingUnknownCanBeUndone()
    {
        var vm = NewVm();
        vm.PickCard = _ => null;
        vm.HandRed[1].Unknown = true;
        vm.DropOnBoard(vm.HandRed[1], vm.BoardCells[6]);

        Assert.True(vm.CanUndoFrom(vm.BoardCells[6]));
        vm.DropOnHandSlot(vm.BoardCells[6], vm.HandRed[3]); // 拖回手牌 = 撤回

        Assert.Null(vm.BoardCells[6].Card);
        Assert.False(vm.BoardCells[6].Unknown);
        Assert.True(vm.HandRed[1].Unknown); // 退回未知牌
        Assert.False(vm.HasPendingReveal);
    }

    [Fact]
    public void SaveAndLoadDeckFillsHand()
    {
        string path = Path.Combine(Path.GetTempPath(), "ttc_gui_test_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var deckVm = new DeckViewModel(Lib, new DeckStore(path));
            for (int i = 0; i < 5; i++)
                deckVm.EditorSlots[i].Card = Lib.Find(10 + i);
            deckVm.DeckName = "deck-x";

            deckVm.SaveCommand.Execute(null);
            Assert.Single(deckVm.Decks);
            Assert.Equal("deck-x", deckVm.Decks[0].Name);
            Assert.True(File.Exists(path));

            SavedDeck? applied = null;
            deckVm.ApplyToMatch = d => applied = d;
            deckVm.UseDeck(deckVm.Decks[0]);
            Assert.NotNull(applied);
            Assert.Equal("deck-x", applied!.Name);

            var vm = NewVm();
            vm.LoadDeck(applied, 1);
            for (int i = 0; i < 5; i++)
                Assert.Equal(10 + i, vm.HandBlue[i].Card!.Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EditDeckReloadsEditorAndUpdates()
    {
        string path = Path.Combine(Path.GetTempPath(), "ttc_gui_test_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new DeckStore(path);
            store.Save(new[] { new SavedDeck { Name = "old", CardIds = new List<int> { 1, 2, 3, 4, 5 } } });

            var deckVm = new DeckViewModel(Lib, store);
            Assert.Single(deckVm.Decks);
            deckVm.EditDeck(deckVm.Decks[0]);
            Assert.Equal("old", deckVm.DeckName);
            Assert.Equal(1, deckVm.EditorSlots[0].Card!.Id);

            deckVm.EditorSlots[0].Card = Lib.Find(9);
            deckVm.SaveCommand.Execute(null);

            var reloaded = new DeckStore(path).Load();
            Assert.Single(reloaded);
            Assert.Equal(9, reloaded[0].CardIds[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}