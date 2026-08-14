using CommunityToolkit.Mvvm.ComponentModel;
using OLEDSaver.Services;

namespace OLEDSaver.ViewModels;

/// <summary>One monitor in the "blank these displays" list.</summary>
public sealed class DisplayOptionViewModel : ObservableObject
{
    private readonly Action<DisplayOptionViewModel> _selectionChanged;
    private bool _isSelected;

    public DisplayOptionViewModel(DisplayInfo display, bool isSelected, Action<DisplayOptionViewModel> selectionChanged)
    {
        Display = display;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
    }

    public DisplayInfo Display { get; }

    /// <summary>
    /// What a tick is saved under: the monitor, not the GDI slot it currently
    /// occupies. See <see cref="DisplayInfo.StableId"/>.
    /// </summary>
    public string Id => Display.StableId;

    public string Label => Display.Label;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                _selectionChanged(this);
        }
    }
}
