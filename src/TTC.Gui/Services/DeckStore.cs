using System.IO;
using System.Text.Json;

namespace TTC.Gui.Services;

/// <summary>One saved deck: a name plus up to five card ids.</summary>
public sealed class SavedDeck
{
    public string Name { get; set; } = "";
    public List<int> CardIds { get; set; } = new();
}

/// <summary>Loads and saves the user's decks as JSON under %APPDATA%\TTC_GUI\decks.json.</summary>
public sealed class DeckStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;

    public DeckStore(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TTC_GUI", "decks.json");

    public List<SavedDeck> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<SavedDeck>();
            var decks = JsonSerializer.Deserialize<List<SavedDeck>>(File.ReadAllText(_path), JsonOptions);
            return decks ?? new List<SavedDeck>();
        }
        catch
        {
            return new List<SavedDeck>();
        }
    }

    public void Save(IEnumerable<SavedDeck> decks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(decks.ToList(), JsonOptions));
    }
}
