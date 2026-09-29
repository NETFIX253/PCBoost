using CommunityToolkit.Mvvm.ComponentModel;

namespace PCBoost.Presentation.Common;

public enum StepState { Pending = 0, Running, Done, Failed }

/// <summary>Étape d'une progression (analyse, optimisation). L'état est exposé en texte (accessibilité) et en glyphe.</summary>
public sealed partial class ProgressStepViewModel : ObservableObject
{
    private readonly Func<StepState, string> _stateText;

    public ProgressStepViewModel(int index, string label, Func<StepState, string> stateText)
    {
        Index = index;
        Label = label;
        _stateText = stateText ?? throw new ArgumentNullException(nameof(stateText));
    }

    public int Index { get; }

    public string Label { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(StateText), nameof(IconGlyph), nameof(AccessibleName))]
    public partial StepState State { get; set; }

    public bool IsPending => State == StepState.Pending;

    public bool IsRunning => State == StepState.Running;

    public bool IsDone => State == StepState.Done;

    public bool IsFailed => State == StepState.Failed;

    public string StateText => _stateText(State);

    public string IconGlyph => State switch
    {
        StepState.Done => Glyphs.Success,
        StepState.Running => Glyphs.Running,
        StepState.Failed => Glyphs.Error,
        _ => Glyphs.Pending,
    };

    public string AccessibleName => $"{Label} : {StateText}";
}

/// <summary>Suite d'étapes ordonnées : marque les précédentes terminées, l'étape courante en cours.</summary>
public static class ProgressSteps
{
    public static void Advance(IReadOnlyList<ProgressStepViewModel> steps, int currentIndex, bool currentDone = false)
    {
        ArgumentNullException.ThrowIfNull(steps);
        foreach (var step in steps)
        {
            if (step.Index < currentIndex) step.State = StepState.Done;
            else if (step.Index == currentIndex) step.State = currentDone ? StepState.Done : StepState.Running;
            else step.State = StepState.Pending;
        }
    }

    public static void CompleteAll(IReadOnlyList<ProgressStepViewModel> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        foreach (var step in steps) step.State = StepState.Done;
    }

    public static void Reset(IReadOnlyList<ProgressStepViewModel> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        foreach (var step in steps) step.State = StepState.Pending;
    }

    public static void FailCurrent(IReadOnlyList<ProgressStepViewModel> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        foreach (var step in steps)
            if (step.State == StepState.Running) step.State = StepState.Failed;
    }
}
