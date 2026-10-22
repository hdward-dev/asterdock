using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace VMCube.Module.Views;

public abstract class CardModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
}

public sealed class MachineCardModel : CardModel
{
    private Bitmap? _preview;
    private string _previewCaption = "";
    public required string Name { get; init; }
    public required string Spec { get; init; }
    public required string Protocol { get; init; }
    public required string StateText { get; init; }
    public required string Placeholder { get; init; }
    public required string PrimaryTip { get; init; }
    public required Geometry PrimaryIcon { get; init; }
    public required string FavoriteTip { get; init; }
    public string OpenDesktopName => "打开桌面 " + Name;
    public bool IsRunning { get; init; }
    public bool IsPaused { get; init; }
    public bool ShowStarOutline => !IsFavorite;
    public bool IsQuiet => !IsRunning && !IsPaused;
    public bool IsNotRunning => !IsRunning;
    public bool ShowPreview { get; init; }
    public bool IsFavorite { get; init; }
    public bool IsSelected { get; init; }
    public bool CanPrimary { get; init; }
    public bool CanPause { get; init; }
    public bool CanPower { get; init; }
    public bool ActionsEnabled { get; init; }
    public Bitmap? Preview
    {
        get => _preview;
        set
        {
            if (!Set(ref _preview, value)) return;
            Raise(nameof(HasPreview));
            Raise(nameof(ShowPlaceholder));
        }
    }
    public bool HasPreview => _preview is not null;
    public bool ShowPlaceholder => !HasPreview;
    public string PreviewCaption { get => _previewCaption; set => Set(ref _previewCaption, value); }
    public Func<Task> OpenPreview { get; init; } = () => Task.CompletedTask;
    public Func<Task> Primary { get; init; } = () => Task.CompletedTask;
    public Func<Task> Pause { get; init; } = () => Task.CompletedTask;
    public Func<Task> Power { get; init; } = () => Task.CompletedTask;
    public Func<Task> Favorite { get; init; } = () => Task.CompletedTask;
    public Action Details { get; init; } = () => { };
}

public sealed class EmptyMachineModel
{
    public required string Title { get; init; }
    public required string Message { get; init; }
}

public sealed class VmDetailsModel
{
    public bool IsEmpty { get; init; }
    public bool HasVm => !IsEmpty;
    public string Name { get; init; } = "";
    public string Spec { get; init; } = "";
    public string StateText { get; init; } = "";
    public bool IsRunning { get; init; }
    public bool IsPaused { get; init; }
    public bool IsQuiet => !IsRunning && !IsPaused;
    public bool IsNotRunning => !IsRunning;
    public bool ShowImported { get; init; }
    public string ImportedSummary { get; init; } = "";
    public string DiskText { get; init; } = "";
    public bool ShowRemoteConsole { get; init; }
    public bool ShowStart { get; init; }
    public bool ShowEject { get; init; }
    public bool ShowConnect { get; init; }
    public bool ShowPause { get; init; }
    public bool ShowShutdown { get; init; }
    public bool ShowResume { get; init; }
    public bool ShowForce { get; init; }
    public bool ActionsEnabled { get; init; } = true;
    public bool ShowLocalNote { get; init; }
    public string Footer { get; init; } = "";
    public Func<Task> Start { get; init; } = () => Task.CompletedTask;
    public Func<Task> Eject { get; init; } = () => Task.CompletedTask;
    public Func<Task> Connect { get; init; } = () => Task.CompletedTask;
    public Func<Task> Pause { get; init; } = () => Task.CompletedTask;
    public Func<Task> Shutdown { get; init; } = () => Task.CompletedTask;
    public Func<Task> Resume { get; init; } = () => Task.CompletedTask;
    public Func<Task> Force { get; init; } = () => Task.CompletedTask;
}
