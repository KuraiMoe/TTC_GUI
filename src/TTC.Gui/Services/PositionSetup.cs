using TTC.Core;
using TTC.Gui.Models;

namespace TTC.Gui.Services;

/// <summary>A card placed on the board: its data plus the side that owns it now (0=red, 1=blue).</summary>
public sealed class BoardCard
{
    public required CardInfo Card { get; init; }
    public int Owner { get; init; }
}

/// <summary>A hand card: either a known card or an unknown slot (opponent hidden hand).</summary>
public sealed class HandCard
{
    public CardInfo? Card { get; set; }
    public bool Unknown { get; set; }
    public bool CanUse { get; set; } = true;
}

/// <summary>
/// An editable position. Player indices follow the engine: 0 = 红方 (red), 1 = 蓝方 (blue).
/// </summary>
public sealed class PositionSetup
{
    public int Current { get; set; } = 1;
    public RuleSet Rules { get; set; }
    public BoardCard?[] Board { get; } = new BoardCard?[9];
    public List<HandCard> HandRed { get; } = new();
    public List<HandCard> HandBlue { get; } = new();

    public List<HandCard> HandOf(int player) => player == 0 ? HandRed : HandBlue;

    public string PlayerName(int player) => player == 0 ? "红方" : "蓝方";

    public int OccupiedCount
    {
        get
        {
            int n = 0;
            foreach (var cell in Board)
                if (cell is not null) n++;
            return n;
        }
    }
}

public sealed class SolveSettings
{
    /// <summary>Wall-clock limit for the whole solve, seconds.</summary>
    public double MaxSeconds { get; set; } = 5.0;

    public int MaxDepth { get; set; } = 100;

    /// <summary>Aggressive search mode (matches the Python service flag).</summary>
    public bool Aggressive { get; set; }

    /// <summary>How many unknown-hand worlds to sample outside the endgame (min 1).</summary>
    public int Samples { get; set; } = 12;
}
