using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using PadP2P.App.Models;
using PadP2P.App.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PadP2P.App;

public partial class LauncherSelectionWindow : FluentWindow
{
    internal LauncherSelectionWindow(IEnumerable<LauncherEntry> entries)
    {
        InitializeComponent();
        Choices = new ObservableCollection<LauncherChoice>(entries.Select(entry => new LauncherChoice(entry)));
        foreach (var choice in Choices)
            choice.PropertyChanged += OnChoicePropertyChanged;
        DataContext = this;
        IconThemeService.ApplyTo(this);
        SystemThemeWatcher.Watch(this);
    }

    public ObservableCollection<LauncherChoice> Choices { get; }

    public IReadOnlyList<LauncherEntry> SelectedEntries => Choices
        .Where(choice => choice.IsSelected)
        .Select(choice => choice.Entry)
        .ToList();

    private void OnChoicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LauncherChoice.IsSelected))
            LaunchSelectedButton.IsEnabled = Choices.Any(choice => choice.IsSelected);
    }

    private void LaunchSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntries.Count > 0)
            DialogResult = true;
    }
}

public sealed class LauncherChoice : INotifyPropertyChanged
{
    private bool _isSelected;

    public LauncherChoice(LauncherEntry entry) => Entry = entry;

    public LauncherEntry Entry { get; }
    public string Name => string.IsNullOrWhiteSpace(Entry.Name)
        ? System.IO.Path.GetFileNameWithoutExtension(Entry.ExecutablePath)
        : Entry.Name;
    public string ExecutablePath => Entry.ExecutablePath;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
