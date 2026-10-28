using System.Diagnostics;
using System.IO;
using TTC.Core;
using TTC.Engine;
using TTC.Gui.Models;
using TTC.Search;

namespace TTC.Gui.Services;

/// <summary>
/// Drives the C# minimax engine from an editable position: builds the snapshot (including type modifiers),
/// runs the standard iterative-deepening search, and adds unknown-hand handling — either the information-set
/// robust endgame of the upstream project (few empty cells, at most two unknown cards) or multi-world sampling
/// with score aggregation in the middle game.
/// </summary>
public sealed class SolverService
{
    private readonly CardLibrary _library;
    private readonly CardDb _db;
    private readonly WorkerContext _worker;
    private readonly SolveService _solve;

    public SolverService(CardLibrary library)
    {
        _library = library;
        if (!File.Exists(CardLibrary.CsvPath))
            throw new FileNotFoundException($"找不到卡牌数据库：{CardLibrary.CsvPath}");
        _db = CardDb.Load(CardLibrary.CsvPath);
        _solve = new SolveService(_db);
        _worker = new WorkerContext(id: 0, ttEntries: 1 << 20, memoEntries: 1 << 15);
    }

    /// <summary>Runs one solve. May take up to <see cref="SolveSettings.MaxSeconds"/> (divided across sample worlds).</summary>
    public SolveOutcome Solve(PositionSetup setup, SolveSettings settings,
        Action<DepthRow>? onDepth = null, CancellationToken ct = default)
    {
        Validate(setup);

        int opp = 1 - setup.Current;
        int unknown = setup.HandOf(opp).Count(c => c.Unknown);
        int occupied = setup.OccupiedCount;

        if (unknown == 0)
            return RunSolve(setup, settings, null, false, 0, onDepth, ct);

        if (occupied >= 5 && unknown <= 2)
            return SolveRobustEndgame(setup, settings, unknown, onDepth, ct);

        return SolveSampled(setup, settings, unknown, onDepth, ct);
    }

    // ------------------------------------------------------------------ paths

    private SolveOutcome SolveRobustEndgame(PositionSetup setup, SolveSettings settings, int unknown,
        Action<DepthRow>? onDepth, CancellationToken ct)
    {
        // Concrete placeholder cards fill the unknown slots; the engine replaces them in its scenarios.
        var world = SampleOppHand(setup, unknown, new Random(12345));
        var outcome = RunSolve(setup, settings, world, true, unknown, onDepth, ct);

        if (outcome.Message.Contains("needs_fallback_candidates", StringComparison.Ordinal))
        {
            // Legal enumeration found no candidate: retry once with random fallback candidates (Python does the same).
            var req = BuildRequest(setup, settings, world, true, unknown);
            req.Robust!.FallbackCandidates = BuildFallbackCandidates(world);
            outcome = Run(setup, req, onDepth, ct, true);
        }
        return outcome;
    }

    private SolveOutcome SolveSampled(PositionSetup setup, SolveSettings settings, int unknown,
        Action<DepthRow>? onDepth, CancellationToken ct)
    {
        int samples = Math.Clamp(settings.Samples, 1, 32);
        var rng = new Random(unchecked(Environment.TickCount * 31 + unknown));
        double perSampleSeconds = Math.Max(0.25, settings.MaxSeconds / samples);

        var totals = new Dictionary<(int Slot, int Cell), (double Score, int Count, int CardId, string Name)>();
        List<DepthRow> firstRows = new();
        long nodes = 0, ttHits = 0, cutoffs = 0;
        double searchMs = 0;
        int depthMin = int.MaxValue;
        var clock = Stopwatch.StartNew();

        for (int i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            var world = SampleOppHand(setup, unknown, rng);
            var single = new SolveSettings
            {
                MaxSeconds = perSampleSeconds,
                MaxDepth = settings.MaxDepth,
                Aggressive = settings.Aggressive,
                Samples = settings.Samples,
            };
            var outcome = RunSolve(setup, single, world, false, unknown, onDepth, ct);

            if (i == 0) firstRows = outcome.Depths;
            nodes += outcome.Nodes;
            ttHits += outcome.TtHits;
            cutoffs += outcome.Cutoffs;
            searchMs += outcome.SearchMs;
            depthMin = Math.Min(depthMin, outcome.DepthCompleted);

            if (!outcome.HasMove) continue;
            var key = (outcome.BestSlot, outcome.Row * 3 + outcome.Col);
            var agg = totals.GetValueOrDefault(key);
            totals[key] = (agg.Score + outcome.Score, agg.Count + 1, outcome.CardId,
                agg.Count == 0 ? outcome.CardName : agg.Name);
        }

        if (totals.Count == 0)
        {
            return new SolveOutcome
            {
                Message = "所有样本都没有合法落点。",
                Depths = firstRows,
                SearchMs = searchMs,
                TotalMs = clock.Elapsed.TotalMilliseconds,
                Samples = samples,
                DepthCompleted = depthMin == int.MaxValue ? 0 : depthMin,
            };
        }

        double bestAvg = double.NegativeInfinity;
        (int Slot, int Cell) bestKey = default;
        int bestId = -1;
        string bestName = "";
        foreach (var (key, agg) in totals)
        {
            double avg = agg.Score / Math.Max(1, agg.Count);
            if (avg > bestAvg)
            {
                bestAvg = avg;
                bestKey = key;
                bestId = agg.CardId;
                bestName = agg.Name;
            }
        }

        return new SolveOutcome
        {
            BestSlot = bestKey.Slot,
            Row = bestKey.Cell / 3,
            Col = bestKey.Cell % 3,
            CardId = bestId,
            CardName = bestName,
            Score = bestAvg,
            DepthCompleted = depthMin == int.MaxValue ? 0 : depthMin,
            Nodes = nodes,
            TtHits = ttHits,
            Cutoffs = cutoffs,
            SearchMs = searchMs,
            TotalMs = clock.Elapsed.TotalMilliseconds,
            Depths = firstRows,
            Samples = samples,
            Message = $"对方 {unknown} 张未知牌：采样 {samples} 个可能世界分别求解，推荐平均分最高的着法。",
        };
    }

    private SolveRequest BuildRequest(PositionSetup setup, SolveSettings settings,
        HandCard[]? oppOverride, bool robust, int unknown)
    {
        var req = new SolveRequest
        {
            Protocol = Protocol.Version,
            Rules = setup.Rules,
            State = BuildSnapshot(setup, oppOverride),
            Aggressive = settings.Aggressive,
            MaxDepth = settings.MaxDepth,
            MaxTime = settings.MaxSeconds,
        };
        if (robust) req.Robust = BuildRobust(setup, unknown);
        return req;
    }

    private SolveOutcome RunSolve(PositionSetup setup, SolveSettings settings,
        HandCard[]? oppOverride, bool robust, int unknown, Action<DepthRow>? onDepth, CancellationToken ct)
    {
        var req = BuildRequest(setup, settings, oppOverride, robust, unknown);
        return Run(setup, req, onDepth, ct, robust);
    }

    private SolveOutcome Run(PositionSetup setup, SolveRequest req,
        Action<DepthRow>? onDepth, CancellationToken ct, bool robust)
    {
        var clock = Stopwatch.StartNew();
        var rows = new List<DepthRow>();

        var result = _solve.Solve(req, _worker,
            onDepth: d =>
            {
                string moveText = d.Move is { } m
                    ? $"({m.Row + 1},{m.Col + 1}) {CardName(m.CardId)}"
                    : "—";
                var row = new DepthRow(d.Depth, moveText, d.Score, d.Nodes, d.NodesTotal,
                    d.TtHitsTotal, d.CutoffsTotal, d.ElapsedSeconds);
                lock (rows) rows.Add(row);
                onDepth?.Invoke(row);
            },
            onRobust: null);

        var final = result.Final;
        string message = result.Status != SolveResult.Ok
            ? $"引擎返回状态：{result.Status}"
            : final is null ? "搜索未找到走法（可能超时或没有合法着）。" : "";
        if (robust && result.Robust is not null)
        {
            message += $"信息集残局：{result.Robust.Scenarios} 个场景" +
                       (result.Robust.Overridden ? "，已覆盖标准搜索结果。" : "。");
        }

        // The engine's per-level stats carry the real per-level node counts; live rows only have totals.
        var finalRows = result.Depths.Count > 0
            ? result.Depths.Select(d => new DepthRow(d.Depth,
                d.Move is { } m ? $"({m.Row + 1},{m.Col + 1}) {CardName(m.CardId)}" : "—",
                d.Score, d.Nodes, d.NodesTotal, d.TtHitsTotal, d.CutoffsTotal, d.ElapsedSeconds)).ToList()
            : rows;

        return new SolveOutcome
        {
            BestSlot = final?.Slot ?? -1,
            Row = final?.Row ?? -1,
            Col = final?.Col ?? -1,
            CardId = final?.CardId ?? -1,
            CardName = CardName(final?.CardId ?? -1),
            Score = final is null ? 0 : result.StandardScore,
            DepthCompleted = result.DepthCompleted,
            Nodes = result.Nodes,
            TtHits = result.TtHits,
            Cutoffs = result.Cutoffs,
            SearchMs = result.SearchMs,
            TotalMs = result.TotalMs > 0 ? result.TotalMs : clock.Elapsed.TotalMilliseconds,
            FromRobust = robust && result.Robust?.Overridden == true,
            Scenarios = result.Robust?.Scenarios ?? 0,
            Message = message,
            Depths = finalRows,
        };
    }

    // ------------------------------------------------------------------ snapshots

    private static void Validate(PositionSetup setup)
    {
        foreach (var hand in setup.HandOf(setup.Current))
        {
            if (hand.Unknown)
                throw new InvalidOperationException("当前行动方的手牌不能是未知牌：请先选好要出的卡。");
            if (hand.Card is null)
                throw new InvalidOperationException("当前行动方的手牌有空位：请补齐或删除该手牌。");
        }
        if (setup.HandOf(setup.Current).Count == 0)
            throw new InvalidOperationException("当前行动方没有手牌。");
    }

    private static CardSnapshot SnapshotOf(CardInfo card, int owner, bool canUse = true) =>
        new(card.Up, card.Right, card.Down, card.Left, (byte)owner, card.Id, card.Type, canUse);

    private static CardSnapshot UnknownSnapshot(int owner) => new(0, 0, 0, 0, (byte)owner, -1, null, true);

    /// <summary>Builds the engine snapshot; <paramref name="oppOverride"/> fills the unknown opponent slots.</summary>
    public static StateSnapshot BuildSnapshot(PositionSetup setup, HandCard[]? oppOverride = null)
    {
        var s = new StateSnapshot { Current = setup.Current, Rules = setup.Rules };
        for (int cell = 0; cell < 9; cell++)
        {
            var b = setup.Board[cell];
            if (b is not null) s.Board[cell] = SnapshotOf(b.Card, b.Owner);
        }

        int opp = 1 - setup.Current;
        for (int p = 0; p < 2; p++)
        {
            int oppSlot = 0;
            foreach (var card in setup.HandOf(p))
            {
                if (card.Unknown && p == opp)
                {
                    CardInfo? world = oppOverride is not null && oppSlot < oppOverride.Length
                        ? oppOverride[oppSlot].Card
                        : null;
                    oppSlot++;
                    s.Hands[p].Add(world is null ? UnknownSnapshot(p) : SnapshotOf(world, p, card.CanUse));
                }
                else if (card.Card is not null)
                {
                    s.Hands[p].Add(SnapshotOf(card.Card, p, card.CanUse));
                }
                else
                {
                    s.Hands[p].Add(UnknownSnapshot(p));
                }
            }
        }

        // 同类强化 / 同类弱化: the Python service applies type modifiers before handing the state to the engine.
        if ((setup.Rules & (RuleSet.Ascension | RuleSet.Descension)) != 0)
        {
            var g = GameState.FromSnapshot(s);
            g.RecalculateTypeModifiers();
            s = g.ToSnapshot();
        }
        return s;
    }

    private static RobustRequest BuildRobust(PositionSetup setup, int unknown)
    {
        int opp = 1 - setup.Current;
        var generated = new bool[2][];
        for (int p = 0; p < 2; p++)
        {
            var hand = setup.HandOf(p);
            generated[p] = new bool[hand.Count];
            for (int k = 0; k < hand.Count; k++)
                generated[p][k] = hand[k].Unknown && p == opp;
        }

        return new RobustRequest
        {
            AiPlayer = setup.Current,
            Opponent = opp,
            Generated = generated,
            SampleCount = unknown == 0 ? 1 : Math.Min(24, Math.Max(16, unknown * 12)),
            NpcIds = new List<int>(),
            Handler = true,
        };
    }

    private static List<UnknownCandidate> BuildFallbackCandidates(HandCard[] world)
    {
        var list = new List<UnknownCandidate>();
        foreach (var card in world)
        {
            if (card.Card is not { } c) continue;
            list.Add(new UnknownCandidate(c.Id, (byte)c.Up, (byte)c.Right, (byte)c.Down, (byte)c.Left,
                string.IsNullOrEmpty(c.Type) ? (byte)0 : CardTypes.IdOf(c.Type)));
        }
        return list;
    }

    /// <summary>Draws distinct cards for the unknown opponent slots, avoiding cards already visible.</summary>
    private HandCard[] SampleOppHand(PositionSetup setup, int unknown, Random rng)
    {
        var seen = new HashSet<int>();
        foreach (var b in setup.Board)
            if (b is not null) seen.Add(b.Card.Id);
        foreach (var p in new[] { 0, 1 })
            foreach (var h in setup.HandOf(p))
                if (h.Card is not null) seen.Add(h.Card.Id);

        var pool = new List<CardInfo>();
        foreach (var card in _library.ValidCards())
            if (!seen.Contains(card.Id)) pool.Add(card);

        var result = new HandCard[unknown];
        for (int i = 0; i < unknown; i++)
        {
            if (pool.Count == 0)
            {
                result[i] = new HandCard { Card = null, Unknown = true };
                continue;
            }
            int pick = rng.Next(pool.Count);
            result[i] = new HandCard { Card = pool[pick], Unknown = true };
            pool.RemoveAt(pick);
        }
        return result;
    }

    private string CardName(int id) => id < 0 ? "未知" : _library.Find(id)?.Name ?? $"#{id}";
}
