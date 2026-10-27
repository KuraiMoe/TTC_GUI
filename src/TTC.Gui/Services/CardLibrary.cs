using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using TTC.Gui.Models;

namespace TTC.Gui.Services;

/// <summary>Loads 幻卡数据库.csv (id, name, four edges, star, type) and offers lookup/search (by name, id or pinyin).</summary>
public sealed class CardLibrary
{
    private readonly List<CardInfo> _cards = new();
    private readonly Dictionary<int, CardInfo> _byId = new();
    private readonly Dictionary<int, (string Initials, string Full)> _pinyin = new();

    public IReadOnlyList<CardInfo> Cards => _cards;

    public static string DataDirectory => Path.Combine(AppContext.BaseDirectory, "Data");

    public static string CsvPath => Path.Combine(DataDirectory, "幻卡数据库.csv");

    public static string PinyinPath => Path.Combine(DataDirectory, "card_pinyin.json");

    public static CardLibrary Load()
    {
        string path = CsvPath;
        if (!File.Exists(path))
        {
            // Fall back to the source-tree layout when running from the project output.
            string alt = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "data", "幻卡数据库.csv"));
            if (File.Exists(alt)) path = alt;
            else throw new FileNotFoundException($"找不到卡牌数据库：{CsvPath}");
        }

        var lib = new CardLibrary();
        lib.LoadFrom(path);
        lib.LoadPinyin(Path.Combine(Path.GetDirectoryName(path)!, "card_pinyin.json"));
        return lib;
    }

    private void LoadPinyin(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path));
            if (map is null) return;
            foreach (var (id, py) in map)
            {
                if (py.Length >= 2 && int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cid))
                    _pinyin[cid] = (py[0], py[1]);
            }
        }
        catch
        {
            // Pinyin search is optional; name/id search keeps working without it.
        }
    }

    private void LoadFrom(string path)
    {
        using var reader = new StreamReader(path, System.Text.Encoding.UTF8);
        string? headerLine = reader.ReadLine() ?? throw new InvalidDataException("卡牌数据库为空");
        var headers = headerLine.Split(',');
        int Col(string name)
        {
            for (int i = 0; i < headers.Length; i++)
                if (string.Equals(headers[i].Trim(), name, StringComparison.Ordinal)) return i;
            throw new InvalidDataException($"卡牌数据库缺少列：{name}");
        }

        int cId = Col("序号"), cName = Col("名称"), cTop = Col("Top"), cBottom = Col("Bottom"),
            cLeft = Col("Left"), cRight = Col("Right"), cStar = Col("星级"), cType = Col("TripleTriadCardType");
        int max = Math.Max(Math.Max(Math.Max(cId, cName), Math.Max(cTop, cBottom)),
            Math.Max(Math.Max(cLeft, cRight), Math.Max(cStar, cType)));

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length <= max) continue;
            if (!int.TryParse(parts[cId], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) continue;

            string type = parts[cType].Trim();
            var card = new CardInfo(
                id,
                parts[cName].Trim(),
                ParseEdge(parts[cTop]),
                ParseEdge(parts[cRight]),
                ParseEdge(parts[cBottom]),
                ParseEdge(parts[cLeft]),
                int.TryParse(parts[cStar], NumberStyles.Integer, CultureInfo.InvariantCulture, out int star) ? star : 0,
                string.IsNullOrEmpty(type) ? null : type);

            _cards.Add(card);
            _byId.TryAdd(id, card);
        }

        _cards.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    private static int ParseEdge(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    public CardInfo? Find(int id) => _byId.GetValueOrDefault(id);

    /// <summary>Fuzzy search over name and id, with optional star filter (0 = no filter).</summary>
    public List<CardInfo> Search(string? text, int starFilter = 0, int limit = 300)
    {
        text = text?.Trim().ToLowerInvariant() ?? "";
        var result = new List<CardInfo>(Math.Min(limit, 64));
        foreach (var card in _cards)
        {
            if (starFilter > 0 && card.Star != starFilter) continue;
            if (text.Length > 0 && !Matches(card, text)) continue;
            result.Add(card);
            if (result.Count >= limit) break;
        }
        return result;
    }

    /// <summary>Name, id, pinyin initials (e.g. "ddn" → 渡渡鸟) or full pinyin.</summary>
    private bool Matches(CardInfo card, string text)
    {
        if (card.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        if (card.Id.ToString(CultureInfo.InvariantCulture).StartsWith(text, StringComparison.Ordinal)) return true;
        if (_pinyin.TryGetValue(card.Id, out var py))
        {
            if (py.Initials.StartsWith(text, StringComparison.Ordinal)) return true;
            if (py.Initials.Contains(text, StringComparison.Ordinal)) return true;
            if (py.Full.Contains(text, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Cards valid for the solver: real id and edges in 1..10.</summary>
    public List<CardInfo> ValidCards()
    {
        var result = new List<CardInfo>(_cards.Count);
        foreach (var card in _cards)
        {
            if (card.Id <= 0) continue;
            if (card.Up is < 1 or > 10 || card.Right is < 1 or > 10 || card.Down is < 1 or > 10 || card.Left is < 1 or > 10)
                continue;
            if (card.Star < 1) continue;
            result.Add(card);
        }
        return result;
    }
}
