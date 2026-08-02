using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Apportia.Platform;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Apportia.Views;

public sealed class LeftoverNode(string name, bool isFolder, LeftoverNode? parent, string? relativePath)
    : INotifyPropertyChanged
{
    private const string FileIcon = "avares://Apportia/Assets/Emoji/1f4c4.svg";
    private static readonly string FolderIcon = MainWindow.OpenFolderIconPath;

    private bool _isChecked;
    private bool _suppress;

    public string Name { get; } = name;
    public bool IsFolder { get; } = isFolder;
    public string? RelativePath { get; } = relativePath;
    public LeftoverNode? Parent { get; } = parent;
    public ObservableCollection<LeftoverNode> Children { get; } = [];
    public string IconPath => IsFolder ? FolderIcon : FileIcon;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();

            if (_suppress) return;

            foreach (var child in Children)
                child.SetCheckedCascade(value);

            Parent?.RefreshFromChildren();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetCheckedCascade(bool value)
    {
        _suppress = true;
        try
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                OnPropertyChanged(nameof(IsChecked));
            }

            foreach (var child in Children)
                child.SetCheckedCascade(value);
        }
        finally
        {
            _suppress = false;
        }
    }

    private void RefreshFromChildren()
    {
        if (Children.Count == 0) return;
        var allChecked = Children.All(c => c.IsChecked);
        if (_isChecked == allChecked)
        {
            Parent?.RefreshFromChildren();
            return;
        }

        _suppress = true;
        try
        {
            _isChecked = allChecked;
            OnPropertyChanged(nameof(IsChecked));
        }
        finally
        {
            _suppress = false;
        }

        Parent?.RefreshFromChildren();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public partial class UpdateLeftoverFilesDialog : Window
{
    private readonly List<LeftoverNode> _allFiles = [];

    public UpdateLeftoverFilesDialog()
    {
        InitializeComponent();
    }

    public UpdateLeftoverFilesDialog(
        IReadOnlyList<string> relativePaths,
        IReadOnlyCollection<string>? preChecked = null,
        bool rememberDefault = false) : this()
    {
        var roots = BuildTree(relativePaths);
        Tree.ItemsSource = roots;

        if (preChecked is { Count: > 0 })
        {
            var lookup = new HashSet<string>(preChecked, StringComparer.OrdinalIgnoreCase);
            foreach (var file in _allFiles)
                if (file.RelativePath != null && lookup.Contains(file.RelativePath))
                    file.IsChecked = true;
        }

        RememberCheckBox.IsChecked = rememberDefault;

        foreach (var file in _allFiles)
            file.PropertyChanged += OnFileChanged;
        DeleteButton.IsEnabled = _allFiles.Any(f => f.IsChecked);
    }

    public IReadOnlyList<string> SelectedForDeletion { get; private set; } = [];
    public bool Remember { get; private set; }
    public bool Confirmed { get; private set; }

    private void OnFileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LeftoverNode.IsChecked))
            DeleteButton.IsEnabled = _allFiles.Any(f => f.IsChecked);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
    }

    private ObservableCollection<LeftoverNode> BuildTree(IReadOnlyList<string> relativePaths)
    {
        var roots = new ObservableCollection<LeftoverNode>();
        var folderIndex = new Dictionary<string, LeftoverNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var rel in relativePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var normalized = rel.Replace('\\', '/');
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            LeftoverNode? parent = null;
            var siblings = roots;
            var accumulated = string.Empty;

            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                var isLast = i == parts.Length - 1;
                accumulated = accumulated.Length == 0 ? part : accumulated + "/" + part;

                if (isLast)
                {
                    var fileNode = new LeftoverNode(part, false, parent, rel);
                    siblings.Add(fileNode);
                    _allFiles.Add(fileNode);
                }
                else
                {
                    if (!folderIndex.TryGetValue(accumulated, out var folder))
                    {
                        folder = new LeftoverNode(part, true, parent, null);
                        siblings.Add(folder);
                        folderIndex[accumulated] = folder;
                    }

                    parent = folder;
                    siblings = folder.Children;
                }
            }
        }

        return roots;
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        SelectedForDeletion = _allFiles
                              .Where(f => f is { IsChecked: true, RelativePath: not null })
                              .Select(f => f.RelativePath!)
                              .ToArray();
        Remember = RememberCheckBox.IsChecked == true;
        Confirmed = true;
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Remember = RememberCheckBox.IsChecked == true;
        Confirmed = true;
        Close();
    }
}