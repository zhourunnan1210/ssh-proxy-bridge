using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SshProxyBridge.App;

public static class MarkdownDocumentRenderer
{
    private static readonly FontFamily YaHei = new("Microsoft YaHei UI, Microsoft YaHei");
    private static readonly Brush Primary = new SolidColorBrush(Color.FromRgb(23, 32, 51));
    private static readonly Brush Secondary = new SolidColorBrush(Color.FromRgb(71, 85, 105));
    private static readonly Brush Border = new SolidColorBrush(Color.FromRgb(226, 232, 240));

    public static FlowDocument BuildDocument(string markdown)
    {
        var document = new FlowDocument
        {
            FontFamily = YaHei,
            FontSize = 14,
            Foreground = Primary,
            PagePadding = new Thickness(34, 28, 34, 40),
            LineHeight = 24
        };
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var index = 0; index < lines.Length;)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var code = new StringBuilder();
                index++;
                while (index < lines.Length
                       && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.AppendLine(lines[index]);
                    index++;
                }
                if (index < lines.Length)
                    index++;
                document.Blocks.Add(CreateCodeBlock(code.ToString().TrimEnd()));
                continue;
            }

            if (TryReadHeading(line, out var level, out var heading))
            {
                var paragraph = new Paragraph
                {
                    FontFamily = YaHei,
                    FontSize = level switch { 1 => 27, 2 => 22, 3 => 18, _ => 16 },
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Primary,
                    Margin = new Thickness(0, level == 1 ? 4 : 20, 0, 9),
                    LineHeight = double.NaN
                };
                AddInline(paragraph, heading);
                document.Blocks.Add(paragraph);
                index++;
                continue;
            }

            if (IsTableStart(lines, index))
            {
                document.Blocks.Add(CreateTable(lines, ref index));
                continue;
            }

            if (IsBullet(line, out _))
            {
                document.Blocks.Add(CreateList(lines, ref index, ordered: false));
                continue;
            }

            if (IsOrderedItem(line, out _))
            {
                document.Blocks.Add(CreateList(lines, ref index, ordered: true));
                continue;
            }

            if (line.TrimStart().StartsWith('>'))
            {
                var quote = new StringBuilder();
                while (index < lines.Length && lines[index].TrimStart().StartsWith('>'))
                {
                    if (quote.Length > 0)
                        quote.Append(' ');
                    quote.Append(lines[index].TrimStart().TrimStart('>').TrimStart());
                    index++;
                }
                document.Blocks.Add(CreateQuoteBlock(quote.ToString()));
                continue;
            }

            if (Regex.IsMatch(line.Trim(), "^-{3,}$"))
            {
                document.Blocks.Add(new BlockUIContainer(new Border
                {
                    Height = 1,
                    Background = Border,
                    Margin = new Thickness(0, 14, 0, 14)
                }));
                index++;
                continue;
            }

            var text = new StringBuilder(line.Trim());
            index++;
            while (index < lines.Length && !IsBlockBoundary(lines, index))
            {
                text.Append(' ').Append(lines[index].Trim());
                index++;
            }
            var body = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, 10),
                Foreground = Secondary
            };
            AddInline(body, text.ToString());
            document.Blocks.Add(body);
        }

        return document;
    }

    private static Block CreateCodeBlock(string code) =>
        new BlockUIContainer(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 7, 0, 14),
            Child = new TextBlock
            {
                Text = code,
                FontFamily = YaHei,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                TextWrapping = TextWrapping.Wrap
            }
        });

    private static Block CreateQuoteBlock(string text) =>
        new BlockUIContainer(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(239, 246, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246)),
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(0, 5, 0, 12),
            Child = new TextBlock
            {
                Text = StripInlineMarkers(text),
                FontFamily = YaHei,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175)),
                TextWrapping = TextWrapping.Wrap
            }
        });

    private static System.Windows.Documents.List CreateList(
        IReadOnlyList<string> lines,
        ref int index,
        bool ordered)
    {
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(20, 0, 0, 12),
            Padding = new Thickness(8, 0, 0, 0)
        };

        while (index < lines.Count)
        {
            var matches = ordered
                ? IsOrderedItem(lines[index], out var content)
                : IsBullet(lines[index], out content);
            if (!matches)
                break;

            var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 3), Foreground = Secondary };
            AddInline(paragraph, content);
            list.ListItems.Add(new ListItem(paragraph));
            index++;
        }

        return list;
    }

    private static Table CreateTable(IReadOnlyList<string> lines, ref int index)
    {
        var headers = SplitTableRow(lines[index]);
        index += 2;
        var rows = new List<IReadOnlyList<string>>();
        while (index < lines.Count && lines[index].Contains('|') && !string.IsNullOrWhiteSpace(lines[index]))
        {
            rows.Add(SplitTableRow(lines[index]));
            index++;
        }

        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 7, 0, 16)
        };
        for (var column = 0; column < headers.Count; column++)
            table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        group.Rows.Add(CreateTableRow(headers, header: true));
        foreach (var row in rows)
        {
            var normalized = Enumerable.Range(0, headers.Count)
                .Select(column => column < row.Count ? row[column] : string.Empty)
                .ToArray();
            group.Rows.Add(CreateTableRow(normalized, header: false));
        }
        table.RowGroups.Add(group);
        return table;
    }

    private static TableRow CreateTableRow(IReadOnlyList<string> values, bool header)
    {
        var row = new TableRow();
        foreach (var value in values)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = header ? Primary : Secondary
            };
            AddInline(paragraph, value);
            row.Cells.Add(new TableCell(paragraph)
            {
                Background = header ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : Brushes.White,
                BorderBrush = Border,
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(9, 7, 9, 7)
            });
        }
        return row;
    }

    private static void AddInline(Paragraph paragraph, string text)
    {
        var matches = Regex.Matches(text, @"\*\*(.+?)\*\*|`(.+?)`|\[([^\]]+)\]\(([^)]+)\)");
        var position = 0;
        foreach (Match match in matches)
        {
            if (match.Index > position)
                paragraph.Inlines.Add(new Run(text[position..match.Index]));

            if (match.Groups[1].Success)
            {
                paragraph.Inlines.Add(new Bold(new Run(match.Groups[1].Value)));
            }
            else if (match.Groups[2].Success)
            {
                paragraph.Inlines.Add(new Run(match.Groups[2].Value)
                {
                    Background = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                    Foreground = new SolidColorBrush(Color.FromRgb(190, 24, 93))
                });
            }
            else
            {
                paragraph.Inlines.Add(new Run(match.Groups[3].Value)
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                    TextDecorations = TextDecorations.Underline
                });
            }
            position = match.Index + match.Length;
        }

        if (position < text.Length)
            paragraph.Inlines.Add(new Run(text[position..]));
    }

    private static bool IsBlockBoundary(IReadOnlyList<string> lines, int index)
    {
        var line = lines[index];
        return string.IsNullOrWhiteSpace(line)
               || line.TrimStart().StartsWith("```", StringComparison.Ordinal)
               || TryReadHeading(line, out _, out _)
               || IsTableStart(lines, index)
               || IsBullet(line, out _)
               || IsOrderedItem(line, out _)
               || line.TrimStart().StartsWith('>')
               || Regex.IsMatch(line.Trim(), "^-{3,}$");
    }

    private static bool TryReadHeading(string line, out int level, out string text)
    {
        var match = Regex.Match(line, "^(#{1,6})\\s+(.+)$");
        level = match.Success ? match.Groups[1].Value.Length : 0;
        text = match.Success ? match.Groups[2].Value.Trim() : string.Empty;
        return match.Success;
    }

    private static bool IsBullet(string line, out string content)
    {
        var match = Regex.Match(line, "^\\s*[-*+]\\s+(.+)$");
        content = match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        return match.Success;
    }

    private static bool IsOrderedItem(string line, out string content)
    {
        var match = Regex.Match(line, "^\\s*\\d+[.)]\\s+(.+)$");
        content = match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        return match.Success;
    }

    private static bool IsTableStart(IReadOnlyList<string> lines, int index) =>
        index + 1 < lines.Count
        && lines[index].Contains('|')
        && IsTableSeparator(lines[index + 1]);

    private static bool IsTableSeparator(string line)
    {
        var cells = SplitTableRow(line);
        return cells.Count > 0 && cells.All(cell => Regex.IsMatch(cell, "^:?-{3,}:?$"));
    }

    private static IReadOnlyList<string> SplitTableRow(string line) =>
        line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();

    private static string StripInlineMarkers(string text) =>
        Regex.Replace(text, @"\*\*(.+?)\*\*|`(.+?)`|\[([^\]]+)\]\(([^)]+)\)",
            match => match.Groups[1].Success
                ? match.Groups[1].Value
                : match.Groups[2].Success
                    ? match.Groups[2].Value
                    : match.Groups[3].Value);

}
