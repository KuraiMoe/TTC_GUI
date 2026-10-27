using System.Diagnostics;
using System.Globalization;
using TTC.Core;
using TTC.Gui.Models;

namespace TTC.Gui.Services;

/// <summary>Score of one candidate deck, mirroring the Python service's DeckResult.</summary>
public sealed class DraftDeckScore
{
    public required IReadOnlyList<CardInfo> Deck { get; init; }
    public double FinalScore { get; init; }
    public required IReadOnlyDictionary<string, double> Components { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required IReadOnlyList<string> Selection { get; init; }

    public string ReasonsList => Reasons.Count == 0 ? "—" : string.Join("；", Reasons);

    public string WarningList => Warnings.Count == 0 ? "无" : string.Join("；", Warnings);
}

public sealed class DraftAnalysisResult
{
    public required List<string> Process { get; init; }
    public required List<DraftDeckScore> Results { get; init; }
    public DraftDeckScore? Best { get; init; }
}

/// <summary>
/// C# port of the upstream draft (选拔) analysis: heuristic deck scoring plus greedy-simulation verification
/// against sampled opponent decks. Covers the "selection" mode of the FFXIV three-stage draft and plain
/// fixed-deck comparison. Behaviour and formulas follow Python/app.py (subsystem B).
/// </summary>
public sealed class DraftAnalyzer
{
    public static readonly IReadOnlyDictionary<string, double> DefaultWeights = new Dictionary<string, double>
    {
        ["base"] = 0.18,
        ["rule"] = 0.24,
        ["synergy"] = 0.16,
        ["stability"] = 0.10,
        ["verification"] = 0.32,
        ["risk"] = 0.12,
    };

    private static readonly string[] ExtraRules =
        { "同数", "加算", "逆转", "王牌杀手", "同类强化", "同类弱化", "秩序", "混乱" };

    private readonly CardLibrary _library;
    private readonly Dictionary<int, int> _star = new();
    private readonly List<CardInfo> _allCards;

    public DraftAnalyzer(CardLibrary library)
    {
        _library = library;
        _allCards = library.ValidCards();
        foreach (var card in _allCards)
            _star[card.Id] = Math.Max(card.Star, 1);
    }

    /// <summary>Rules allowed in draft analysis: 选拔 plus at most two extras.</summary>
    public static List<string> NormalizeRules(IEnumerable<string> rules)
    {
        var extras = new List<string>();
        foreach (var rule in rules)
        {
            string trimmed = rule.Trim();
            if (trimmed.Length == 0 || trimmed == "选拔") continue;
            if (Array.IndexOf(ExtraRules, trimmed) >= 0 && !extras.Contains(trimmed))
                extras.Add(trimmed);
            if (extras.Count >= 2) break;
        }
        var result = new List<string> { "选拔" };
        result.AddRange(extras);
        return result;
    }

    /// <summary>selection mode: three stages of choices (stages 1-2 hold two cards per choice, stage 3 one).</summary>
    public DraftAnalysisResult AnalyzeSelection(IReadOnlyList<IReadOnlyList<IReadOnlyList<CardInfo>>> stages,
        IReadOnlyList<string> rules, IReadOnlyDictionary<string, double>? weights = null,
        double timeBudgetSeconds = 7.0, Action<string>? log = null)
    {
        var combos = ExpandSelection(stages);
        return Analyze(combos, rules, weights, timeBudgetSeconds, log);
    }

    /// <summary>decks mode: a few complete five-card decks to compare.</summary>
    public DraftAnalysisResult AnalyzeDecks(IReadOnlyList<IReadOnlyList<CardInfo>> decks,
        IReadOnlyList<string> rules, IReadOnlyDictionary<string, double>? weights = null,
        double timeBudgetSeconds = 7.0, Action<string>? log = null)
    {
        var combos = new List<(IReadOnlyList<CardInfo> Deck, IReadOnlyList<string> Labels)>();
        for (int i = 0; i < decks.Count; i++)
            combos.Add((decks[i], new[] { $"卡组{i + 1}" }));
        return Analyze(combos, rules, weights, timeBudgetSeconds, log);
    }

    /// <summary>Cartesian product of the three draft stages into five-card decks with pick labels.</summary>
    public static List<(IReadOnlyList<CardInfo> Deck, IReadOnlyList<string> Labels)> ExpandSelection(
        IReadOnlyList<IReadOnlyList<IReadOnlyList<CardInfo>>> stages)
    {
        var result = new List<(IReadOnlyList<CardInfo>, IReadOnlyList<string>)>();
        foreach (var c1 in stages.Count > 0 ? stages[0] : Array.Empty<IReadOnlyList<CardInfo>>())
        foreach (var c2 in stages.Count > 1 ? stages[1] : Array.Empty<IReadOnlyList<CardInfo>>())
        foreach (var c3 in stages.Count > 2 ? stages[2] : Array.Empty<IReadOnlyList<CardInfo>>())
        {
            var deck = new List<CardInfo>();
            deck.AddRange(c1);
            deck.AddRange(c2);
            deck.AddRange(c3);
            if (deck.Count != 5) continue;
            result.Add((deck, new[]
            {
                $"备选1-候选{Index(stages, 0, c1) + 1}",
                $"备选2-候选{Index(stages, 1, c2) + 1}",
                $"备选3-候选{Index(stages, 2, c3) + 1}",
            }));
        }
        return result;
    }

    private static int Index(IReadOnlyList<IReadOnlyList<IReadOnlyList<CardInfo>>> stages, int stage,
        IReadOnlyList<CardInfo> choice)
    {
        var list = stages[stage];
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], choice)) return i;
        return 0;
    }

    // ------------------------------------------------------------------ orchestration

    private DraftAnalysisResult Analyze(List<(IReadOnlyList<CardInfo> Deck, IReadOnlyList<string> Labels)> combos,
        IReadOnlyList<string> rules, IReadOnlyDictionary<string, double>? weights,
        double timeBudgetSeconds, Action<string>? log)
    {
        var w = NormalizeWeights(weights);
        double budget = Math.Clamp(timeBudgetSeconds, 5.0, 20.0);
        var process = new List<string>
        {
            $"规则：{string.Join(", ", rules)}",
            $"权重：base {w["base"]}, rule {w["rule"]}, synergy {w["synergy"]}, stability {w["stability"]}, verification {w["verification"]}, risk {w["risk"]}",
            $"深算预算：{budget:0.0} 秒",
            "对手采样：约 2/3 随机合法牌组 + 1/3 高威胁合法牌组",
        };
        foreach (var line in process) log?.Invoke(line);

        var ruleSet = RuleParser.FromPythonList(rules);
        var star = _star;

        // 1. heuristic pass, keep legal decks only
        var preliminary = new List<(DraftDeckScore Score, IReadOnlyList<CardInfo> Deck, IReadOnlyList<string> Labels)>();
        foreach (var (deck, labels) in combos)
        {
            if (!DeckLegal(deck).Legal) continue;
            preliminary.Add((ScoreDeck(deck, rules, w, null, labels), deck, labels));
        }
        process.Add($"完整卡组：{combos.Count} 套，合法 {preliminary.Count} 套");
        log?.Invoke(process[^1]);
        preliminary.Sort((a, b) => b.Score.FinalScore.CompareTo(a.Score.FinalScore));
        if (preliminary.Count == 0)
        {
            process.Add("没有找到合法卡组");
            log?.Invoke("没有找到合法卡组");
            return new DraftAnalysisResult { Process = process, Results = new List<DraftDeckScore>(), Best = null };
        }

        // 2. verification simulation, sharing the time budget in order
        process.Add("顺序深算：共享总预算");
        log?.Invoke(process[^1]);
        var started = Stopwatch.StartNew();
        var scored = new List<DraftDeckScore>();
        int total = preliminary.Count;
        for (int i = 0; i < total; i++)
        {
            var (_, deck, labels) = preliminary[i];
            double remainingBudget = Math.Max(0.2, budget - started.Elapsed.TotalSeconds);
            double deadline = started.Elapsed.TotalSeconds + remainingBudget / (total - i);
            var verification = VerificationScore(deck, rules, ruleSet, deadline);
            var score = ScoreDeck(deck, rules, w, verification, labels);
            scored.Add(score);

            string line = $"深算 {i + 1}/{total}：{verification.Games} 局，威胁 {verification.ThreatRounds} 轮，" +
                          $"均值 {F(verification.Average)}，P10 下限 {F(verification.Floor)}";
            process.Add(line);
            log?.Invoke(line);
        }

        scored.Sort((a, b) => b.FinalScore.CompareTo(a.FinalScore));
        var best = scored[0];
        process.Add($"实际耗时：{started.Elapsed.TotalSeconds:0.00} 秒");
        process.Add($"最高分：{F(best.FinalScore)}");
        if (best.Selection.Count > 0) process.Add($"推荐选择：{string.Join(" + ", best.Selection)}");
        foreach (var reason in best.Reasons) process.Add($"推荐理由：{reason}");
        foreach (var warning in best.Warnings) process.Add($"风险提示：{warning}");
        foreach (var line in process.Skip(4)) log?.Invoke(line);

        return new DraftAnalysisResult { Process = process, Results = scored, Best = best };
    }

    // ------------------------------------------------------------------ scoring

    private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static Dictionary<string, double> NormalizeWeights(IReadOnlyDictionary<string, double>? input)
    {
        var w = new Dictionary<string, double>(DefaultWeights);
        if (input is null) return w;
        foreach (var (key, value) in input)
            if (w.ContainsKey(key)) w[key] = Math.Max(0, value);
        return w;
    }

    /// <summary>Official deck limits: at most two cards of ★4+, at most one ★5.</summary>
    public (bool Legal, List<string> Warnings) DeckLegal(IReadOnlyList<CardInfo> deck)
    {
        int high = deck.Count(c => Star(c) >= 4);
        int five = deck.Count(c => Star(c) == 5);
        var warnings = new List<string>();
        if (high > 2) warnings.Add($"★4以上卡牌有 {high} 张，超过 2 张限制");
        if (five > 1) warnings.Add($"★5 卡牌有 {five} 张，超过 1 张限制");
        return (warnings.Count == 0, warnings);
    }

    private int Star(CardInfo card) => _star.GetValueOrDefault(card.Id, Math.Max(card.Star, 1));

    private static int[] Sides(CardInfo card) => new[] { card.Up, card.Right, card.Down, card.Left };

    public double SingleCardBase(CardInfo card)
    {
        var values = Sides(card);
        double avg = values.Average();
        int highEdges = values.Count(v => v >= 8);
        int lowEdges = values.Count(v => v <= 3);
        bool adjacentHigh =
            (card.Up >= 8 && card.Right >= 8) ||
            (card.Right >= 8 && card.Down >= 8) ||
            (card.Down >= 8 && card.Left >= 8) ||
            (card.Left >= 8 && card.Up >= 8);
        double sc = avg * 6.0 + highEdges * 8.0 - lowEdges * 4.0 + Star(card) * 2.5;
        if (adjacentHigh) sc += 6.0;
        if (highEdges >= 3) sc += 8.0;
        return Clamp(sc);
    }

    public (double Score, List<string> Reasons) RuleScoreForCard(CardInfo card, IReadOnlyList<string> rules)
    {
        var values = Sides(card);
        double avg = values.Average();
        double score = 45.0;
        var reasons = new List<string>();

        if (rules.Contains("同数"))
        {
            int dup = 4 - values.Distinct().Count();
            score += dup * 10.0;
            if (dup > 0) reasons.Add("重复边值适合同数");
        }
        if (rules.Contains("加算"))
        {
            int common = 0;
            for (int i = 0; i < 4; i++)
                for (int j = i + 1; j < 4; j++)
                {
                    int sum = values[i] + values[j];
                    if (sum is >= 8 and <= 13) common++;
                }
            score += Math.Min(common * 3.5, 18.0);
            if (common >= 3) reasons.Add("有多组常见加算和");
        }
        if (rules.Contains("逆转"))
        {
            if (avg <= 5)
            {
                score += 24.0;
                reasons.Add("低均值适合逆转");
            }
            else if (avg >= 8)
            {
                score -= 20.0;
            }
        }
        if (rules.Contains("王牌杀手"))
        {
            int special = values.Count(v => v is 1 or 10);
            score += special * 9.0;
            if (special > 0) reasons.Add("含 1 或 A 可利用王牌杀手");
        }
        if (rules.Contains("同类强化") && card.Type is not null) score += 7.0;
        if (rules.Contains("同类弱化") && card.Type is not null) score += 3.0;
        return (Clamp(score), reasons);
    }

    private double DeckBase(IReadOnlyList<CardInfo> deck) =>
        deck.Count == 0 ? 0 : deck.Average(SingleCardBase);

    private (double Score, List<string> Reasons) DeckRule(IReadOnlyList<CardInfo> deck, IReadOnlyList<string> rules)
    {
        double sum = 0;
        var reasons = new List<string>();
        foreach (var card in deck)
        {
            var (sc, cr) = RuleScoreForCard(card, rules);
            sum += sc;
            reasons.AddRange(cr);
        }
        double coverageBonus = 0;
        for (int side = 0; side < 4; side++)
            if (deck.Max(c => Sides(c)[side]) >= 8) coverageBonus += 3.0;
        var unique = reasons.Distinct().Take(4).ToList();
        return (Clamp(deck.Count == 0 ? 0 : sum / deck.Count + coverageBonus), unique);
    }

    private (double Score, List<string> Reasons) DeckSynergy(IReadOnlyList<CardInfo> deck, IReadOnlyList<string> rules)
    {
        var reasons = new List<string>();
        double score = 50.0;
        var allValues = deck.SelectMany(Sides).ToList();

        int covered = 0;
        if (deck.Count > 0)
        {
            if (deck.Max(c => c.Up) >= 8) covered++;
            if (deck.Max(c => c.Right) >= 8) covered++;
            if (deck.Max(c => c.Down) >= 8) covered++;
            if (deck.Max(c => c.Left) >= 8) covered++;
        }
        score += covered * 6.0;
        if (covered == 4) reasons.Add("四向都有高边覆盖");

        int weakEdges = allValues.Count(v => v <= 3);
        score -= Math.Max(0, weakEdges - 5) * 2.5;

        if (rules.Contains("同数"))
        {
            int dups = allValues.GroupBy(v => v).Where(g => g.Count() >= 3).Sum(g => g.Count() - 1);
            score += Math.Min(dups * 3.0, 18.0);
            if (dups >= 3) reasons.Add("牌组有同数连携素材");
        }
        if (rules.Contains("加算"))
        {
            int popular = 0;
            for (int i = 0; i < deck.Count; i++)
                for (int j = i + 1; j < deck.Count; j++)
                    foreach (int va in Sides(deck[i]))
                        foreach (int vb in Sides(deck[j]))
                            if (va + vb is 8 or 10 or 12) popular++;
            score += Math.Min(popular * 0.45, 18.0);
            if (popular >= 18) reasons.Add("牌组加算组合密度高");
        }

        var typeCounts = new Dictionary<string, int>();
        foreach (var card in deck)
            if (card.Type is not null)
                typeCounts[card.Type] = typeCounts.GetValueOrDefault(card.Type) + 1;

        if (rules.Contains("同类强化") && typeCounts.Count > 0)
        {
            var (bestType, bestCount) = typeCounts.OrderByDescending(kv => kv.Value).First();
            score += bestCount * 6.0;
            if (bestCount >= 2) reasons.Add($"{bestType}类型可形成同类强化");
        }
        if (rules.Contains("同类弱化") && typeCounts.Count > 0)
        {
            int repeats = typeCounts.Values.Where(c => c > 1).Sum(c => c - 1);
            score -= repeats * 8.0;
            if (repeats == 0) reasons.Add("类型分散，适合同类弱化");
        }
        return (Clamp(score), reasons.Take(4).ToList());
    }

    private (double Score, List<string> Reasons) DeckStability(IReadOnlyList<CardInfo> deck, IReadOnlyList<string> rules)
    {
        var perCard = deck.Select(SingleCardBase).OrderByDescending(x => x).ToList();
        double top3Avg = perCard.Count == 0 ? 0 : perCard.Take(3).Average();
        double bot2Avg = perCard.Count == 0 ? 0 : perCard.Skip(Math.Max(0, perCard.Count - 2)).Average();
        double score = top3Avg * 0.65 + bot2Avg * 0.35;
        var reasons = new List<string>();

        if (deck.Count > 0)
        {
            var stars = deck.Select(Star).ToList();
            if (stars.Max() - stars.Min() <= 2)
            {
                score += 5.0;
                reasons.Add("星级曲线平稳");
            }
        }
        if (rules.Contains("秩序") || rules.Contains("混乱"))
        {
            score += bot2Avg * 0.08;
            reasons.Add("可用顺序受限时下限更重要");
        }
        if (rules.Contains("选拔")) score += 4.0;
        return (Clamp(score), reasons);
    }

    private (double Penalty, List<string> Warnings) DeckRisk(IReadOnlyList<CardInfo> deck, IReadOnlyList<string> rules)
    {
        var warnings = new List<string>();
        double penalty = 0.0;
        var (legal, legalWarnings) = DeckLegal(deck);
        if (!legal)
        {
            penalty += 45.0;
            warnings.AddRange(legalWarnings);
        }
        var allValues = deck.SelectMany(Sides).ToList();
        int weakEdges = allValues.Count(v => v <= 3);
        if (weakEdges >= 8 && !rules.Contains("逆转"))
        {
            penalty += (weakEdges - 7) * 3.0;
            warnings.Add("弱边偏多，非逆转规则下容易被反吃");
        }
        if (rules.Contains("逆转"))
        {
            int highEdges = allValues.Count(v => v >= 8);
            if (highEdges >= 8)
            {
                penalty += (highEdges - 7) * 4.0;
                warnings.Add("高边偏多，逆转规则下风险较高");
            }
        }
        return (Clamp(penalty), warnings);
    }

    private DraftDeckScore ScoreDeck(IReadOnlyList<CardInfo> deck, IReadOnlyList<string> rules,
        Dictionary<string, double> w, VerificationResult? verification, IReadOnlyList<string> labels)
    {
        double b = DeckBase(deck);
        var (ruleScore, ruleReasons) = DeckRule(deck, rules);
        var (synergy, synergyReasons) = DeckSynergy(deck, rules);
        var (stability, stabilityReasons) = DeckStability(deck, rules);
        var (risk, warnings) = DeckRisk(deck, rules);
        verification ??= VerificationResult.Default;

        double wSum = Math.Max(w["base"] + w["rule"] + w["synergy"] + w["stability"] + w["verification"], 0.001);
        double final = (b * w["base"] + ruleScore * w["rule"] + synergy * w["synergy"] +
                        stability * w["stability"] + verification.Score * w["verification"]) / wSum;
        final -= risk * w["risk"];

        var reasons = new List<string>();
        reasons.AddRange(ruleReasons);
        reasons.AddRange(synergyReasons);
        reasons.AddRange(stabilityReasons);
        if (reasons.Count == 0) reasons.Add("整体表现均衡");
        if (verification.Games > 0)
            reasons.Add($"深算 {verification.Games} 局，威胁采样 {verification.ThreatRounds} 轮，" +
                        $"均值 {F(verification.Average)}，P10 下限 {F(verification.Floor)}");

        return new DraftDeckScore
        {
            Deck = deck,
            FinalScore = Math.Round(Clamp(final), 2),
            Components = new Dictionary<string, double>
            {
                ["base"] = Math.Round(b, 2),
                ["rule"] = Math.Round(ruleScore, 2),
                ["synergy"] = Math.Round(synergy, 2),
                ["stability"] = Math.Round(stability, 2),
                ["verification"] = Math.Round(verification.Score, 2),
                ["risk"] = Math.Round(risk, 2),
            },
            Reasons = reasons.Take(6).ToList(),
            Warnings = warnings,
            Selection = labels,
        };
    }

    // ------------------------------------------------------------------ verification simulation

    private sealed class VerificationResult
    {
        public static readonly VerificationResult Default = new();
        public double Score { get; init; } = 50.0;
        public int Games { get; init; }
        public int ThreatRounds { get; init; }
        public double Average { get; init; } = 50.0;
        public double Floor { get; init; } = 50.0;
        public double Min { get; init; } = 50.0;
    }

    private VerificationResult VerificationScore(IReadOnlyList<CardInfo> deck, IReadOnlyList<string> rules,
        RuleSet ruleSet, double deadlineSeconds)
    {
        var rng = new Random(unchecked(Environment.TickCount * 17 + deck.Count * 31 + deck[0].Id));
        var avoid = deck.Select(c => c.Id).ToHashSet();
        var scores = new List<double>();
        int rounds = 0, threatRounds = 0;
        var clock = Stopwatch.StartNew();

        while ((clock.Elapsed.TotalSeconds < deadlineSeconds || rounds < 2) && rounds < 2000)
        {
            bool useThreat = rounds % 3 == 2;
            var opp = useThreat
                ? BuildThreatOpponent(avoid, deck, rules, rng)
                : BuildRandomOpponent(avoid, rng);
            if (useThreat) threatRounds++;
            scores.Add(SimulateMatch(deck, opp, rules, ruleSet, myFirst: true, rng));
            scores.Add(SimulateMatch(deck, opp, rules, ruleSet, myFirst: false, rng));
            rounds++;
            if (clock.Elapsed.TotalSeconds >= deadlineSeconds && rounds >= 2) break;
        }

        if (scores.Count == 0)
            return new VerificationResult();

        double avg = scores.Average();
        var sorted = scores.OrderBy(x => x).ToList();
        int floorIdx = Math.Min(sorted.Count - 1, Math.Max(0, (int)(sorted.Count * 0.10)));
        double verified = Clamp(avg * 0.65 + sorted[floorIdx] * 0.35);
        return new VerificationResult
        {
            Score = verified,
            Games = scores.Count,
            ThreatRounds = threatRounds,
            Average = Math.Round(avg, 2),
            Floor = Math.Round(sorted[floorIdx], 2),
            Min = Math.Round(sorted[0], 2),
        };
    }

    /// <summary>Random legal five-card opponent deck (deck limits applied).</summary>
    private List<CardInfo> BuildRandomOpponent(HashSet<int> avoid, Random rng)
    {
        var available = _allCards.Where(c => !avoid.Contains(c.Id)).ToList();
        if (available.Count < 5) available = _allCards.ToList();
        return GenerateLegalDeck(available, rng);
    }

    private List<CardInfo> GenerateLegalDeck(List<CardInfo> cards, Random rng)
    {
        var shuffled = cards.OrderBy(_ => rng.Next()).ToList();
        var deck = new List<CardInfo>();
        foreach (var card in shuffled)
        {
            var candidate = new List<CardInfo>(deck) { card };
            if (candidate.Count <= 5 && DeckLegal(candidate).Legal)
                deck.Add(card);
            if (deck.Count == 5) return deck;
        }
        return shuffled.Take(5).ToList();
    }

    /// <summary>Opponent deck biased toward cards that beat my deck (Python _draft_build_threat_opponent).</summary>
    private List<CardInfo> BuildThreatOpponent(HashSet<int> avoid, IReadOnlyList<CardInfo> myDeck,
        IReadOnlyList<string> rules, Random rng)
    {
        var available = _allCards.Where(c => !avoid.Contains(c.Id)).ToList();
        if (available.Count < 5) available = _allCards.ToList();
        var ranked = available.OrderByDescending(c => OpponentThreat(c, myDeck, rules)).ToList();
        int topCount = Math.Min(36, ranked.Count);
        var topPool = ranked.Take(topCount).ToList();

        var deck = new List<CardInfo>();
        int attempts = 0;
        while (deck.Count < 5 && attempts < 80)
        {
            attempts++;
            int poolSize = Math.Max(8, Math.Min(topPool.Count, 16 + attempts / 8));
            var candidate = topPool[rng.Next(poolSize)];
            if (deck.Any(c => c.Id == candidate.Id)) continue;
            if (DeckLegal(deck.Concat(new[] { candidate }).ToList()).Legal)
                deck.Add(candidate);
        }
        if (deck.Count < 5)
        {
            foreach (var candidate in ranked)
            {
                if (deck.Any(c => c.Id == candidate.Id)) continue;
                if (DeckLegal(deck.Concat(new[] { candidate }).ToList()).Legal)
                    deck.Add(candidate);
                if (deck.Count == 5) break;
            }
        }
        return deck.Take(5).ToList();
    }

    private double OpponentThreat(CardInfo card, IReadOnlyList<CardInfo> myDeck, IReadOnlyList<string> rules)
    {
        var (sc, _) = RuleScoreForCard(card, rules);
        sc += SingleCardBase(card) * 0.35;
        // facing edge pairs: (up,down) (right,left) (down,up) (left,right)
        var pairs = new[] { (0, 2), (1, 3), (2, 0), (3, 1) };
        foreach (var (oppDir, myDir) in pairs)
        {
            foreach (var myCard in myDeck)
            {
                int result = Compare(card, oppDir, myCard, myDir, rules);
                if (result == 1) sc += 4.0;
                else if (result == 0) sc += 0.7;

                int ov = Sides(card)[oppDir];
                int mv = Sides(myCard)[myDir];
                if (rules.Contains("同数") && ov == mv) sc += 2.4;
                if (rules.Contains("加算") && (ov + mv is 8 or 10 or 12)) sc += 1.8;
            }
        }
        return sc;
    }

    /// <summary>Python Card.compare_values: 1 = first wins, -1 = second wins, 0 = tie.</summary>
    private static int Compare(CardInfo a, int sideA, CardInfo b, int sideB, IReadOnlyList<string> rules)
    {
        int mine = Sides(a)[sideA], other = Sides(b)[sideB];
        if (rules.Contains("王牌杀手"))
        {
            bool mineSpecial = mine is 1 or 10, otherSpecial = other is 1 or 10;
            if (mineSpecial && otherSpecial)
            {
                if (mine == other) return 0;
                if (mine == 1 && other == 10) return rules.Contains("逆转") ? -1 : 1;
                if (mine == 10 && other == 1) return rules.Contains("逆转") ? 1 : -1;
            }
        }
        if (rules.Contains("逆转"))
        {
            if (mine < other) return 1;
            return mine > other ? -1 : 0;
        }
        if (mine > other) return 1;
        return mine < other ? -1 : 0;
    }

    /// <summary>Greedy-vs-greedy match; returns 0..100 normalized score from my (blue) perspective.</summary>
    private double SimulateMatch(IReadOnlyList<CardInfo> myDeck, IReadOnlyList<CardInfo> oppDeck,
        IReadOnlyList<string> rules, RuleSet ruleSet, bool myFirst, Random rng)
    {
        var snapshot = new StateSnapshot
        {
            Current = myFirst ? 1 : 0,
            Rules = ruleSet,
        };
        for (int i = 0; i < oppDeck.Count; i++)
            snapshot.Hands[0].Add(new CardSnapshot(oppDeck[i].Up, oppDeck[i].Right, oppDeck[i].Down, oppDeck[i].Left,
                OwnerId.Red, oppDeck[i].Id, oppDeck[i].Type, true));
        for (int i = 0; i < myDeck.Count; i++)
            snapshot.Hands[1].Add(new CardSnapshot(myDeck[i].Up, myDeck[i].Right, myDeck[i].Down, myDeck[i].Left,
                OwnerId.Blue, myDeck[i].Id, myDeck[i].Type, true));

        var state = GameState.FromSnapshot(snapshot);
        if ((ruleSet & (RuleSet.Ascension | RuleSet.Descension)) != 0)
            state.RecalculateTypeModifiers();

        Span<Move> moves = stackalloc Move[GameState.MaxMoves];
        int guard = 0;
        while (!state.IsGameOver && guard < 9)
        {
            guard++;
            int count = state.GenerateMoves(moves);
            if (count == 0) break;
            Move best = moves[0];
            double bestScore = state.CurrentPlayer == 1 ? double.NegativeInfinity : double.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                state.MakeMove(moves[i]);
                double sc = StateScore(state) + (rng.NextDouble() - 0.5) * 0.16;
                state.UndoMove();
                if (state.CurrentPlayer == 1 ? sc > bestScore : sc < bestScore)
                {
                    bestScore = sc;
                    best = moves[i];
                }
            }
            state.MakeMove(best);
        }

        double final = StateScore(state);
        return 100.0 / (1.0 + Math.Exp(-final / 3.0));
    }

    /// <summary>Python _draft_state_score from the blue (ai_idx=1) perspective.</summary>
    private double StateScore(GameState state)
    {
        state.CountCards(out int red, out int blue);
        double score = blue - red;
        for (int cell = 0; cell < 9; cell++)
        {
            int inst = state.BoardInstance(cell);
            if (inst < 0) continue;
            int sign = state.InstanceOwner(inst) == OwnerId.Blue ? 1 : -1;
            int row = cell / 3, col = cell % 3;
            double positional = (row, col) switch
            {
                (0, 0) or (0, 2) or (2, 0) or (2, 2) => 1.4,
                (1, 1) => 1.1,
                _ => 0.9,
            };
            double maxSide = 0;
            for (int side = 0; side < 4; side++)
                maxSide = Math.Max(maxSide, state.InstanceBase(inst, side));
            score += sign * positional * 0.35;
            score += sign * maxSide * 0.035;
            score += sign * _star.GetValueOrDefault(state.InstanceId(inst), 1) * 0.08;
        }
        return score;
    }

    private static double Clamp(double value, double low = 0.0, double high = 100.0) =>
        Math.Max(low, Math.Min(high, value));
}
