namespace Apportia.ViewModels;

public sealed class GridTileRow(IReadOnlyList<AppNode> tiles)
{
    public IReadOnlyList<AppNode> Tiles { get; } = tiles;
}
