using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VideoConverterSetup;

public enum StepState { Pending, Active, Done, Skipped, Failed }

/// <summary>One row in the progress list. Only changed on the UI thread.</summary>
public sealed class SetupStep : INotifyPropertyChanged
{
    private StepState _state;
    private string _detail = "";
    private double _fraction;

    public SetupStep(string title, double weight)
    {
        Title = title;
        Weight = weight;
    }

    public string Title { get; }

    /// <summary>1-based position in the list, shown until the step has a state mark.</summary>
    public int Number { get; set; }

    /// <summary>Share of the overall progress bar (roughly the download size in MB).</summary>
    public double Weight { get; set; }

    public StepState State
    {
        get => _state;
        set { if (_state == value) return; _state = value; OnChanged(); }
    }

    public string Detail
    {
        get => _detail;
        set { if (_detail == value) return; _detail = value; OnChanged(); }
    }

    /// <summary>0..1 within this step.</summary>
    public double Fraction
    {
        get => _fraction;
        set { if (_fraction == value) return; _fraction = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
