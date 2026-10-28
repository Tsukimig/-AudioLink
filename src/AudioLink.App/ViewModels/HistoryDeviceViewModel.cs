namespace AudioLink.ViewModels;

/// <summary>Deletion selection is temporary and never changes the playback selection.</summary>
public sealed class HistoryDeviceViewModel(OutputDeviceViewModel device, Action selectionChanged) : ObservableObject
{
    private bool _selected, _canDelete;
    private string _status = "";
    internal OutputDeviceViewModel Device { get; } = device;
    public string Id => Device.Id;
    public string Name => Device.Name;
    public bool CanDelete => _canDelete;
    public string Status => _status;
    public bool IsSelected
    {
        get => _selected;
        set { if (Set(ref _selected, value && CanDelete)) selectionChanged(); }
    }
    internal void Update(bool canDelete, string status)
    {
        Set(ref _canDelete, canDelete, nameof(CanDelete));
        Set(ref _status, status, nameof(Status));
        if (!canDelete) IsSelected = false;
        Raise(nameof(Name));
    }
}
