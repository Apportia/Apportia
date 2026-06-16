using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Apportia.ViewModels;

public sealed class CategoryNode : INotifyPropertyChanged
{
    private readonly Dictionary<string, SubCategoryNode> _subCategories =
        new(StringComparer.OrdinalIgnoreCase);

    internal CategoryNode(string category, ColumnWidths columns)
    {
        Category = category;
        Columns = columns;
    }

    public string Category { get; }
    public bool HasCategory => !string.IsNullOrEmpty(Category);
    public ColumnWidths Columns { get; }
    public List<AppNode> Nodes { get; } = [];

    public bool IsExpanded
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Notify();
        }
    } = true;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<SubCategoryNode>? SubCategoryExpansionChanged;

    internal SubCategoryNode GetOrCreateSubCategory(string name)
    {
        if (_subCategories.TryGetValue(name, out var node))
            return node;
        node = new SubCategoryNode(name, Columns);
        node.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SubCategoryNode.IsExpanded) && s is SubCategoryNode sub)
                SubCategoryExpansionChanged?.Invoke(this, sub);
        };
        _subCategories[name] = node;
        return node;
    }

    private void Notify([CallerMemberName] string? n = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}