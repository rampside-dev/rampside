using Rampside.Core.Aircraft;

namespace Rampside.Desktop.ViewModels;

public sealed class DoorViewModel : ObservableObject
{
    private DoorState _state;

    public DoorViewModel(MainViewModel owner, DoorId door, string name)
    {
        Door = door;
        Name = Format.Capitalize(name);
        // Unknown counts as closed: the orchestrator then explains why the door cannot be used.
        ToggleCommand = new AsyncCommand(() =>
        {
            var open = State != DoorState.Open;
            return owner.RunAsync(o => o.SetDoor(door, open));
        });
    }

    public DoorId Door { get; }
    public string Name { get; }
    public AsyncCommand ToggleCommand { get; }

    public DoorState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(ActionText));
                OnPropertyChanged(nameof(ActionName));
            }
        }
    }

    public string ActionText => State == DoorState.Open ? "Close" : "Open";

    /// <summary>Accessible name of the button, e.g. "Open forward left door".</summary>
    public string ActionName => $"{ActionText} {Name.ToLowerInvariant()} door";
}
