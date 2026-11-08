using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UpdateForge.Application;

namespace UpdateForge.Desktop.ViewModels;

public sealed partial class HistoryViewModel(IHistoryStore store) : ObservableObject
{
    public ObservableCollection<HistoryRecord> Items { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private HistoryRecord? _selected;

    partial void OnSearchChanged(string value) => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        var items = await store.QueryAsync(Search, 500, default);
        Items.Clear();
        foreach (var i in items) Items.Add(i);
    }
}
