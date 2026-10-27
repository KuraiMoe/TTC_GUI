using System.Collections.ObjectModel;
using System.Windows.Input;
using TTC.Gui.Models;
using TTC.Gui.Services;

namespace TTC.Gui.ViewModels;

/// <summary>One saved deck shown in the 我的卡组 list.</summary>
public sealed class DeckItemViewModel : ObservableObject
{
    private readonly CardLibrary _library;

    public DeckItemViewModel(SavedDeck deck, CardLibrary library)
    {
        Deck = deck;
        _library = library;
        Refresh();
    }

    public SavedDeck Deck { get; }

    public string Name { get; private set; } = "";
    public string Preview { get; private set; } = "";

    public IReadOnlyList<CardInfo?> Cards => Deck.CardIds.Select(id => _library.Find(id)).ToList();

    public void Refresh()
    {
        Name = Deck.Name;
        var names = Deck.CardIds
            .Select(id => _library.Find(id)?.Name ?? $"#{id}")
            .ToList();
        string joined = string.Join("、", names);
        Preview = joined.Length > 40 ? joined[..38] + "…" : joined;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(Cards));
    }
}

/// <summary>The 我的卡组 page: a five-slot deck editor plus the saved deck list.</summary>
public sealed class DeckViewModel : ObservableObject
{
    private readonly CardLibrary _library;
    private readonly DeckStore _store;
    private string _deckName = "";
    private string _statusText = "点击卡位选卡，保存后可在对局中一键载入。";
    private DeckItemViewModel? _editing;

    public DeckViewModel(CardLibrary library, DeckStore? store = null)
    {
        _library = library;
        _store = store ?? new DeckStore();

        for (int i = 0; i < 5; i++)
            EditorSlots.Add(new SlotViewModel { HandSlot = i, Side = 1, Title = $"卡 {i + 1}" });

        foreach (var deck in _store.Load())
            Decks.Add(new DeckItemViewModel(deck, _library));

        SaveCommand = new RelayCommand(SaveEditor);
        ClearEditorCommand = new RelayCommand(ClearEditor);
    }

    /// <summary>Five editable card slots in one row.</summary>
    public ObservableCollection<SlotViewModel> EditorSlots { get; } = new();

    public ObservableCollection<DeckItemViewModel> Decks { get; } = new();

    /// <summary>The view wires this to the card picker dialog.</summary>
    public Func<SlotViewModel, PickedCard?>? PickCard { get; set; }

    /// <summary>Wired by the main window: load a deck into the match (对局求解 page).</summary>
    public Action<SavedDeck>? ApplyToMatch { get; set; }

    public ICommand SaveCommand { get; }
    public ICommand ClearEditorCommand { get; }

    public string DeckName { get => _deckName; set => Set(ref _deckName, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public void OpenPicker(SlotViewModel slot)
    {
        if (PickCard is null) return;
        var picked = PickCard(slot);
        if (picked is null) return;
        slot.Card = picked.Clear ? null : picked.Card;
        slot.Unknown = false;
    }

    public void SaveEditor()
    {
        var ids = EditorSlots.Where(s => s.Card is not null).Select(s => s.Card!.Id).ToList();
        if (ids.Count == 0)
        {
            StatusText = "编辑区还没有选卡。";
            return;
        }

        string name = DeckName.Trim();
        if (name.Length == 0) name = $"卡组 {Decks.Count + 1}";

        if (_editing is not null)
        {
            _editing.Deck.Name = name;
            _editing.Deck.CardIds = ids;
            _editing.Refresh();
            StatusText = $"已更新「{name}」（{ids.Count} 张）。";
        }
        else
        {
            var existing = Decks.FirstOrDefault(d => d.Name == name);
            if (existing is not null)
            {
                existing.Deck.CardIds = ids;
                existing.Refresh();
                StatusText = $"已更新「{name}」（{ids.Count} 张）。";
            }
            else
            {
                Decks.Add(new DeckItemViewModel(new SavedDeck { Name = name, CardIds = ids }, _library));
                StatusText = $"已保存「{name}」（{ids.Count} 张）。";
            }
        }

        Persist();
        DeckName = "";
        _editing = null;
    }

    public void ClearEditor()
    {
        foreach (var slot in EditorSlots)
        {
            slot.Card = null;
            slot.Unknown = false;
        }
        DeckName = "";
        _editing = null;
        StatusText = "编辑区已清空。";
    }

    public void EditDeck(DeckItemViewModel item)
    {
        _editing = item;
        DeckName = item.Deck.Name;
        for (int i = 0; i < EditorSlots.Count; i++)
        {
            EditorSlots[i].Card = i < item.Deck.CardIds.Count ? _library.Find(item.Deck.CardIds[i]) : null;
            EditorSlots[i].Unknown = false;
        }
        StatusText = $"正在编辑「{item.Deck.Name}」，保存即更新。";
    }

    public void DeleteDeck(DeckItemViewModel item)
    {
        Decks.Remove(item);
        if (ReferenceEquals(_editing, item)) _editing = null;
        Persist();
        StatusText = $"已删除卡组「{item.Deck.Name}」。";
    }

    /// <summary>Load a saved deck into the match and let the main window switch pages.</summary>
    public void UseDeck(DeckItemViewModel item)
    {
        ApplyToMatch?.Invoke(item.Deck);
        StatusText = $"已将「{item.Deck.Name}」载入对局。";
    }

    private void Persist() => _store.Save(Decks.Select(d => d.Deck));
}
