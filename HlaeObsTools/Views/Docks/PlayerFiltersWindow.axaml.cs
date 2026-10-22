using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.ViewModels;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public sealed class PlayerFilterRow : ViewModelBase
{
    public string[] MatchOptions { get; } = { "Exact name", "Contains" };
    private string _name = string.Empty;
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    private int _matchIndex;
    public int MatchIndex { get => _matchIndex; set => SetProperty(ref _matchIndex, value); }
    public PlayerFilterRule ToRule() => new() { Name = Name, Match = MatchIndex == 1 ? "contains" : "exact" };
}

public sealed class PlayerFiltersDialogModel : ViewModelBase
{
    public SettingsDockViewModel Settings { get; }
    public ObservableCollection<PlayerFilterRow> Rules { get; } = new();
    public ObservableCollection<string> Roster { get; } = new();
    private string? _selectedPlayer;
    public string? SelectedPlayer
    {
        get => _selectedPlayer;
        set { SetProperty(ref _selectedPlayer, value); OnPropertyChanged(nameof(CanAddPlayer)); }
    }
    public bool CanAddPlayer => SelectedPlayer != null;
    public bool CanSave => Rules.All(r => !string.IsNullOrWhiteSpace(r.Name) && !r.Name.Contains('\0'));
    private string _preview = string.Empty;
    public string Preview { get => _preview; private set => SetProperty(ref _preview, value); }

    public PlayerFiltersDialogModel(SettingsDockViewModel settings)
    {
        Settings = settings;
        foreach (var rule in settings.PlayerFilters)
            Add(rule.Name, rule.Match == "contains" ? 1 : 0);
        RefreshRoster();
    }

    public void Add(string name = "", int matchIndex = 0)
    {
        var row = new PlayerFilterRow { Name = name, MatchIndex = matchIndex };
        row.PropertyChanged += RowChanged;
        Rules.Add(row);
        RefreshPreview();
    }

    public void Remove(PlayerFilterRow row)
    {
        row.PropertyChanged -= RowChanged;
        Rules.Remove(row);
        RefreshPreview();
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e) => RefreshPreview();

    public void RefreshRoster()
    {
        var names = Settings.GetPlayerFilterRoster();
        if (!names.SequenceEqual(Roster))
        {
            var selected = SelectedPlayer;
            Roster.Clear();
            foreach (var name in names) Roster.Add(name);
            SelectedPlayer = names.Contains(selected) ? selected : null;
        }
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        OnPropertyChanged(nameof(CanSave));
        var matches = Roster.Select(name => new
        {
            Name = name,
            Rules = Rules.Select((r, i) => (Rule: r.ToRule(), Number: i + 1))
                .Where(r => r.Rule.Matches(name)).Select(r => $"rule {r.Number}").ToArray()
        }).Where(p => p.Rules.Length > 0)
          .Select(p => $"{p.Name} ({string.Join(", ", p.Rules)})").ToArray();
        Preview = Roster.Count == 0 ? "No current player data" : matches.Length == 0
            ? "No matching players" : string.Join(", ", matches);
    }
}

public partial class PlayerFiltersWindow : Window
{
    private readonly DispatcherTimer _rosterTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    public PlayerFiltersWindow() { InitializeComponent(); }
    public PlayerFiltersWindow(SettingsDockViewModel settings) : this()
    {
        DataContext = new PlayerFiltersDialogModel(settings);
        _rosterTimer.Tick += (_, _) => Model.RefreshRoster();
        Opened += (_, _) => _rosterTimer.Start();
        Closed += (_, _) => _rosterTimer.Stop();
    }
    private PlayerFiltersDialogModel Model => (PlayerFiltersDialogModel)DataContext!;
    private void AddRule_Click(object? sender, RoutedEventArgs e) => Model.Add();
    private void RemoveRule_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PlayerFilterRow row }) Model.Remove(row);
    }
    private void AddPlayer_Click(object? sender, RoutedEventArgs e)
    {
        if (Model.SelectedPlayer is string name) Model.Add(name);
    }
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!Model.CanSave) return;
        Model.Settings.SavePlayerFilters(Model.Rules.Select(r => r.ToRule()));
        Close();
    }
}
