using System.Collections.ObjectModel;
using Rampside.Core.Simulation;
using Rampside.Hosting;

namespace Rampside.Desktop.ViewModels;

/// <summary>One parking spot in the search results.</summary>
public sealed class ParkingChoiceViewModel : ObservableObject
{
    private bool _isSelected;

    public ParkingChoiceViewModel(ParkingSpot spot, AsyncCommand select)
    {
        Index = spot.Index;
        Name = spot.DisplayName;
        SelectCommand = select;
    }

    public int Index { get; }
    public string Name { get; }
    public AsyncCommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

/// <summary>
/// Moves the aircraft to another stand of the current airport: type part of the name ("121", "gate a"),
/// pick a result, press Move.
/// </summary>
public sealed class RepositionViewModel : ObservableObject
{
    private const int MaxResults = 12;

    private readonly MainViewModel _owner;
    private IReadOnlyList<ParkingSpot> _parkings = Array.Empty<ParkingSpot>();
    private string _airport = string.Empty;
    private string _filter = string.Empty;
    private ParkingChoiceViewModel? _selected;
    private string _hint = string.Empty;

    public RepositionViewModel(MainViewModel owner)
    {
        _owner = owner;
        MoveCommand = new AsyncCommand(MoveAsync, () => _selected is not null);
    }

    public ObservableCollection<ParkingChoiceViewModel> Results { get; } = new();

    public AsyncCommand MoveCommand { get; }

    public bool HasParkings => _parkings.Count > 0;

    /// <summary>Search text: any part of the stand name.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value))
            {
                RefreshResults();
            }
        }
    }

    public string Hint
    {
        get => _hint;
        private set => Set(ref _hint, value);
    }

    public string SelectedText => _selected is null ? "No stand selected" : $"{_selected.Name} · {_airport}";

    public void Update(RampsideSnapshot snapshot)
    {
        var aircraft = snapshot.Aircraft;
        var parkings = snapshot.SimConnected ? aircraft.AirportParkings : Array.Empty<ParkingSpot>();
        var airport = aircraft.AirportIcao ?? string.Empty;
        if (!ReferenceEquals(parkings, _parkings) || airport != _airport)
        {
            _parkings = parkings;
            _airport = airport;
            OnPropertyChanged(nameof(HasParkings));
            RefreshResults();
        }

        Hint = !snapshot.SimConnected ? "Not connected to the simulator."
            : _parkings.Count == 0 ? "The airport's stands load once the aircraft is at rest on the ground near an airport."
            : string.IsNullOrWhiteSpace(_filter) ? $"{_parkings.Count} stands at {_airport}. Type part of a name, e.g. 121 or gate a."
            : Results.Count == 0 ? "No stand matches."
            : string.Empty;
    }

    private void RefreshResults()
    {
        var keep = _selected?.Index;
        Results.Clear();
        _selected = null;

        var filter = _filter.Trim();
        if (filter.Length > 0)
        {
            var matches = _parkings
                .Where(p => p.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Number.ToString() == filter ? 0 : 1) // "121" puts Gate 121 first
                .ThenBy(p => p.NameCode)
                .ThenBy(p => p.Number)
                .ThenBy(p => p.Suffix)
                .Take(MaxResults);

            foreach (var spot in matches)
            {
                ParkingChoiceViewModel? choice = null;
                choice = new ParkingChoiceViewModel(spot, new AsyncCommand(() =>
                {
                    Select(choice);
                    return Task.CompletedTask;
                }));
                Results.Add(choice);
            }
        }

        // Keep the selection if it is still listed; a single match is selected right away.
        Select(Results.FirstOrDefault(r => r.Index == keep) ?? (Results.Count == 1 ? Results[0] : null));
    }

    private void Select(ParkingChoiceViewModel? choice)
    {
        foreach (var result in Results)
        {
            result.IsSelected = ReferenceEquals(result, choice);
        }

        _selected = choice;
        OnPropertyChanged(nameof(SelectedText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private async Task MoveAsync()
    {
        if (_selected is not { } target)
        {
            return;
        }

        await _owner.RunAsync(o => o.RepositionTo(target.Index));
    }
}
