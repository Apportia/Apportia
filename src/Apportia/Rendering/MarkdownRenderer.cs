using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Apportia.Rendering;

internal static class MarkdownRenderer
{
    public static Panel Render(string markdown, IBrush textBrush)
    {
        var panel = new StackPanel { Spacing = 3 };
        foreach (var block in ParseBlocks(markdown))
        {
            panel.Children.Add(block.Kind == BlockKind.Heading
                                   ? RenderHeading(block.Text, textBrush)
                                   : RenderBullet(block, textBrush));
        }

        return panel;
    }

    private static TextBlock RenderHeading(string text, IBrush brush)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = brush,
            Margin = new Thickness(0, 10, 0, 2)
        };
    }

    private static Grid RenderBullet(Block block, IBrush brush)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(block.Indent * 20, 0, 0, 0)
        };

        var marker = new TextBlock
        {
            Text = "\u2022",
            Foreground = brush,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top
        };

        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = brush };
        foreach (var inline in ParseInlines(block.Text))
            body.Inlines!.Add(inline);

        Grid.SetColumn(marker, 0);
        Grid.SetColumn(body, 1);
        grid.Children.Add(marker);
        grid.Children.Add(body);
        return grid;
    }

    private static IEnumerable<Block> ParseBlocks(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        Block? current = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.HasValue)
                {
                    yield return current.Value;
                    current = null;
                }

                continue;
            }

            if (line.StartsWith("### "))
            {
                if (current.HasValue)
                {
                    yield return current.Value;
                    current = null;
                }

                yield return new Block(BlockKind.Heading, line[4..], 0);
                continue;
            }

            if (line.StartsWith("## "))
            {
                if (current.HasValue)
                {
                    yield return current.Value;
                    current = null;
                }

                yield return new Block(BlockKind.Heading, line[3..], 0);
                continue;
            }

            if (line.StartsWith("# "))
            {
                if (current.HasValue)
                {
                    yield return current.Value;
                    current = null;
                }

                yield return new Block(BlockKind.Heading, line[2..], 0);
                continue;
            }

            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            if (trimmed.StartsWith("- "))
            {
                if (current.HasValue) yield return current.Value;
                current = new Block(BlockKind.Bullet, trimmed[2..], indent / 2);
                continue;
            }

            if (current.HasValue && indent > 0)
            {
                current = current.Value with { Text = current.Value.Text + " " + trimmed };
                continue;
            }

            if (current.HasValue)
            {
                yield return current.Value;
                current = null;
            }
        }

        if (current.HasValue) yield return current.Value;
    }

    private static IEnumerable<Inline> ParseInlines(string text)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var next = Min3NonNeg(text.IndexOf('*', pos), text.IndexOf('`', pos), text.IndexOf('[', pos));
            if (next < 0)
            {
                if (pos < text.Length) yield return Txt(text[pos..]);
                yield break;
            }

            if (next > pos) yield return Txt(text[pos..next]);

            switch (text[next])
            {
                case '*':
                    if (next + 1 < text.Length && text[next + 1] == '*')
                    {
                        var end = text.IndexOf("**", next + 2, StringComparison.Ordinal);
                        if (end >= 0)
                        {
                            yield return new Run { Text = text[(next + 2)..end], FontWeight = FontWeight.Bold };
                            pos = end + 2;
                        }
                        else
                        {
                            yield return Txt("**");
                            pos = next + 2;
                        }
                    }
                    else
                    {
                        var end = text.IndexOf('*', next + 1);
                        if (end >= 0)
                        {
                            yield return new Run { Text = text[(next + 1)..end], FontStyle = FontStyle.Italic };
                            pos = end + 1;
                        }
                        else
                        {
                            yield return Txt("*");
                            pos = next + 1;
                        }
                    }

                    break;
                case '`':
                {
                    var end = text.IndexOf('`', next + 1);
                    if (end >= 0)
                    {
                        yield return new Run { Text = text[(next + 1)..end], FontFamily = new FontFamily("Cascadia Code,Consolas,monospace") };
                        pos = end + 1;
                    }
                    else
                    {
                        yield return Txt("`");
                        pos = next + 1;
                    }

                    break;
                }
                default:
                {
                    var closeB = text.IndexOf(']', next + 1);
                    if (closeB >= 0 && closeB + 1 < text.Length && text[closeB + 1] == '(')
                    {
                        var closeP = text.IndexOf(')', closeB + 2);
                        if (closeP >= 0)
                        {
                            yield return Txt(text[(next + 1)..closeB]);
                            pos = closeP + 1;
                            continue;
                        }
                    }

                    yield return Txt("[");
                    pos = next + 1;
                    break;
                }
            }
        }
    }

    private static Run Txt(string s)
    {
        return new Run { Text = s };
    }

    private static int Min3NonNeg(int a, int b, int c)
    {
        var min = int.MaxValue;
        if (a >= 0) min = Math.Min(min, a);
        if (b >= 0) min = Math.Min(min, b);
        if (c >= 0) min = Math.Min(min, c);
        return min == int.MaxValue ? -1 : min;
    }

    private enum BlockKind
    {
        Heading,
        Bullet
    }

    private record struct Block(BlockKind Kind, string Text, int Indent);
}