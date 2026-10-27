using System.Collections.ObjectModel;
using System.Windows.Input;
using TTC.Gui.Models;
using TTC.Gui.Services;

namespace TTC.Gui.ViewModels;

public sealed class DraftPickVM : ObservableObject
{
    private CardInfo? _card;

    public DraftPickVM(string title) => Title = title;

    public string Title { get; }

    public CardInfo? Card
    {
        get => _card;
        set
        {
            if (Set(ref _card, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(EdgeText));
                OnPropertyChanged(nameof(StarText));
            }
        }
    }

    public string DisplayName => Card?.Name ?? "点击选卡";
    public string EdgeText => Card is null ? "" : $"上{Card.EdgeText(0)} 右{Card.EdgeText(1)} 下{Card.EdgeText(2)} 左{Card.EdgeText(3)}";
    public string StarText => Card?.StarText ?? "";
}

public sealed class DraftChoiceVM : ObservableObject
{
    public DraftChoiceVM(int stage, int index)
    {
        Stage = stage;
        Index = index;
        Title = $"候选 {index}";
        int count = stage == 3 ? 1 : 2;
        for (int i = 0; i < count; i++)
            Cards.Add(new DraftPickVM($"卡 {i + 1}"));
    }

    public int Stage { get; }
    public int Index { get; }
    public string Title { get; }
    public ObservableCollection<DraftPickVM> Cards { get; } = new();
}

/// <summary>One row of the analysis results.</summary>
public sealed class DraftResultVM
{
    public required DraftDeckScore Score { get; init; }
    public bool IsBest { get; init; }
    public string Header => IsBest
        ? $"★ 推荐  {string.Join(" + ", Score.Selection)}   总分 {Score.FinalScore:0.00}"
        : $"{string.Join(" + ", Score.Selection)}   总分 {Score.FinalScore:0.00}";
    public string Components =>
        $"基础 {C("base")}  规则 {C("rule")}  协同 {C("synergy")}  稳定 {C("stability")}  深算 {C("verification")}  风险 {C("risk")}";
    private string C(string key) => Score.Components.TryGetValue(key, out double v) ? v.ToString("0.0") : "—";
}

public sealed class DraftViewModel : ObservableObject
{
    private readonly CardLibrary _library;
    private bool _ruleSame, _rulePlus, _ruleReverse, _ruleFallenAce;
    private bool _ruleAscension, _ruleDescension, _ruleOrder, _ruleChaos;
    private double _timeBudget = 7.0;
    private bool _isBusy;
    private string _statusText = "配置三段备选后点击「开始分析」。";

    public DraftViewModel(CardLibrary library)
    {
        _library = library;
        for (int stage = 1; stage <= 3; stage++)
        {
            var list = stage == 1 ? Stages1 : stage == 2 ? Stages2 : Stages3;
            for (int choice = 1; choice <= 3; choice++)
                list.Add(new DraftChoiceVM(stage, choice));
        }

        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !IsBusy);
        RandomFillCommand = new RelayCommand(RandomFill, () => !IsBusy);
        ClearCommand = new RelayCommand(ClearAll, () => !IsBusy);
    }

    public ObservableCollection<DraftChoiceVM> Stages1 { get; } = new();
    public ObservableCollection<DraftChoiceVM> Stages2 { get; } = new();
    public ObservableCollection<DraftChoiceVM> Stages3 { get; } = new();
    public ObservableCollection<DraftResultVM> Results { get; } = new();
    public ObservableCollection<string> ProcessLog { get; } = new();

    /// <summary>The view wires this to the card picker dialog.</summary>
    public Func<DraftPickVM, PickedCard?>? PickCard { get; set; }

    public ICommand AnalyzeCommand { get; }
    public ICommand RandomFillCommand { get; }
    public ICommand ClearCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
            {
                (AnalyzeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RandomFillCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ClearCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public double TimeBudget { get => _timeBudget; set => Set(ref _timeBudget, Math.Clamp(value, 5, 20)); }

    public bool RuleSame { get => _ruleSame; set => Set(ref _ruleSame, value); }
    public bool RulePlus { get => _rulePlus; set => Set(ref _rulePlus, value); }
    public bool RuleReverse { get => _ruleReverse; set => Set(ref _ruleReverse, value); }
    public bool RuleFallenAce { get => _ruleFallenAce; set => Set(ref _ruleFallenAce, value); }
    public bool RuleAscension { get => _ruleAscension; set => Set(ref _ruleAscension, value); }
    public bool RuleDescension { get => _ruleDescension; set => Set(ref _ruleDescension, value); }
    public bool RuleOrder { get => _ruleOrder; set => Set(ref _ruleOrder, value); }
    public bool RuleChaos { get => _ruleChaos; set => Set(ref _ruleChaos, value); }

    public void OpenPicker(DraftPickVM pick)
    {
        if (IsBusy || PickCard is null) return;
        var picked = PickCard(pick);
        if (picked is null) return;
        pick.Card = picked.Clear ? null : picked.Card;
    }

    private List<string> SelectedRules()
    {
        var rules = new List<string>();
        if (RuleSame) rules.Add("同数");
        if (RulePlus) rules.Add("加算");
        if (RuleReverse) rules.Add("逆转");
        if (RuleFallenAce) rules.Add("王牌杀手");
        if (RuleAscension) rules.Add("同类强化");
        if (RuleDescension) rules.Add("同类弱化");
        if (RuleOrder) rules.Add("秩序");
        if (RuleChaos) rules.Add("混乱");
        return rules;
    }

    private async Task AnalyzeAsync()
    {
        Results.Clear();
        ProcessLog.Clear();

        var stages = new List<IReadOnlyList<IReadOnlyList<CardInfo>>>();
        foreach (var stageList in new[] { Stages1, Stages2, Stages3 })
        {
            var choices = new List<IReadOnlyList<CardInfo>>();
            foreach (var choice in stageList)
            {
                var cards = choice.Cards.Select(p => p.Card).ToList();
                if (cards.Any(c => c is null))
                {
                    StatusText = $"备选 {choice.Stage} 的 {choice.Title} 还有未选的卡。";
                    return;
                }
                choices.Add(cards!);
            }
            stages.Add(choices);
        }

        var rules = DraftAnalyzer.NormalizeRules(SelectedRules());
        var analyzer = new DraftAnalyzer(_library);

        IsBusy = true;
        StatusText = "分析中…";
        try
        {
            var result = await Task.Run(() => analyzer.AnalyzeSelection(stages, rules, null, TimeBudget,
                log: line => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ProcessLog.Add(line))));

            bool first = true;
            foreach (var score in result.Results)
            {
                Results.Add(new DraftResultVM { Score = score, IsBest = first && ReferenceEquals(score, result.Best) });
                first = false;
            }
            StatusText = result.Best is null ? "没有合法卡组。"
                : $"完成：推荐 {string.Join(" + ", result.Best.Selection)}（{result.Best.FinalScore:0.00} 分）。";
        }
        catch (Exception ex)
        {
            StatusText = "分析失败。";
            ProcessLog.Add(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RandomFill()
    {
        var all = _library.ValidCards();
        var rng = new Random();
        var used = new HashSet<int>();
        foreach (var pick in Stages1.Concat(Stages2).Concat(Stages3).SelectMany(c => c.Cards))
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                var card = all[rng.Next(all.Count)];
                if (!used.Add(card.Id)) continue;
                pick.Card = card;
                break;
            }
        }
        StatusText = "已随机填充候选卡（自动避开重复）。";
    }

    private void ClearAll()
    {
        foreach (var pick in Stages1.Concat(Stages2).Concat(Stages3).SelectMany(c => c.Cards))
            pick.Card = null;
        Results.Clear();
        ProcessLog.Clear();
        StatusText = "已清空。";
    }
}
