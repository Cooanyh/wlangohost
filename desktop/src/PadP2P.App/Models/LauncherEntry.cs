using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace PadP2P.App.Models;

public sealed class LauncherEntry : INotifyPropertyChanged
{
    public const string SuperDisplayId = "superdisplay";

    private string _name = string.Empty;
    private string _executablePath = string.Empty;
    private bool _isEnabled = true;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value ?? string.Empty);
    }

    public string ExecutablePath
    {
        get => _executablePath;
        set => SetField(ref _executablePath, value ?? string.Empty);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    [JsonIgnore]
    public bool IsSuperDisplay => string.Equals(Id, SuperDisplayId, StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
