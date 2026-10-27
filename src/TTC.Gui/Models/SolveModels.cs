namespace TTC.Gui.Models;

/// <summary>One completed iterative-deepening level, shown in the live stats table.</summary>
public sealed record DepthRow(
    int Depth,
    string MoveText,
    double Score,
    long Nodes,
    long NodesTotal,
    long TtHits,
    long Cutoffs,
    double ElapsedSeconds);

/// <summary>Everything the solve page shows after a search.</summary>
public sealed class SolveOutcome
{
    /// <summary>Hand slot index (of the side to move) the recommendation plays, or -1.</summary>
    public int BestSlot { get; init; } = -1;

    public int Row { get; init; } = -1;
    public int Col { get; init; } = -1;
    public int CardId { get; init; } = -1;
    public string CardName { get; init; } = "";
    public double Score { get; init; }
    public int DepthCompleted { get; init; }
    public long Nodes { get; init; }
    public long TtHits { get; init; }
    public long Cutoffs { get; init; }
    public double SearchMs { get; init; }
    public double TotalMs { get; init; }

    /// <summary>Depth-by-depth progress (live during the search, final afterwards).</summary>
    public List<DepthRow> Depths { get; init; } = new();

    /// <summary>True when the information-set endgame overrode the standard search move.</summary>
    public bool FromRobust { get; init; }
    public int Scenarios { get; init; }
    public string Message { get; init; } = "";

    public bool HasMove => BestSlot >= 0;

    /// <summary>Aggregation over sampled unknown-hand worlds, when used.</summary>
    public int Samples { get; init; } = 1;

    public string MoveText => HasMove ? $"第 {Row + 1} 行 第 {Col + 1} 列" : "无合法落点";
}
