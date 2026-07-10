using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Apportia.ViewModels;

public sealed class SubCategoryNode(string name, ColumnWidths columns) : INotifyPropertyChanged
{
    public string Name { get; } = name;
    public ColumnWidths Columns { get; } = columns;

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

    private void Notify([CallerMemberName] string? n = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}