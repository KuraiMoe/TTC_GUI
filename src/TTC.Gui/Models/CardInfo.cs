namespace TTC.Gui.Models;

/// <summary>One row of 幻卡数据库.csv.</summary>
public sealed record CardInfo(int Id, string Name, int Up, int Right, int Down, int Left, int Star, string? Type)
{
    /// <summary>Edge by side index: 0=up, 1=right, 2=down, 3=left.</summary>
    public int Edge(int side) => side switch
    {
        0 => Up, 1 => Right, 2 => Down, 3 => Left, _ => 0,
    };

    public string EdgeText(int side) => FormatEdge(Edge(side));

    public string EdgeText0 => EdgeText(0);
    public string EdgeText1 => EdgeText(1);
    public string EdgeText2 => EdgeText(2);
    public string EdgeText3 => EdgeText(3);

    public static string FormatEdge(int value) => value == 10 ? "A" : value.ToString();

    public string StarText => Star > 0 ? new string('★', Star) : "";

    public override string ToString() => Star > 0 ? $"★{Star} {Name}" : Name;
}
