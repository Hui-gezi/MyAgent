using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace MyAgent.Desktop;

public static class MarkdownRenderer
{
    private static readonly Regex FenceRegex = new("```([\\w+-]*)\\r?\\n?([\\s\\S]*?)(?:```|$)", RegexOptions.Compiled);
    private static readonly Regex InlineRegex = new("(`[^`]+`)|(~~[^~]+~~)|(\\*\\*[^*]+\\*\\*)|(\\*[^*\\n]+\\*)|(\\[[^\\]]+\\]\\(https?://[^)\\s]+\\))|(https?://[^\\s<>()]+)", RegexOptions.Compiled);
    private static readonly Regex HrRegex = new("^\\s{0,3}(?:-{3,}|\\*{3,}|_{3,})\\s*$", RegexOptions.Compiled);
    private static readonly Regex TableSeparatorRegex = new("^\\s*\\|?\\s*:?-{2,}:?\\s*(?:\\|\\s*:?-{2,}:?\\s*)*\\|?\\s*$", RegexOptions.Compiled);

    private static void Dyn(FrameworkElement target, DependencyProperty property, string key) =>
        target.SetResourceReference(property, key);

    private static void Dyn(FrameworkContentElement target, DependencyProperty property, string key) =>
        target.SetResourceReference(property, key);

    public static FlowDocument Render(string source)
    {
        var doc = new FlowDocument
        {
            FontFamily = (FontFamily)Application.Current.FindResource("TextFont"),
            FontSize = 14,
            PagePadding = new Thickness(0),
        };
        Dyn(doc, FlowDocument.ForegroundProperty, "TextPrimary");
        var lastIndex = 0;
        foreach (Match match in FenceRegex.Matches(source))
        {
            if (match.Index > lastIndex) RenderBlocks(doc.Blocks, source[lastIndex..match.Index]);
            doc.Blocks.Add(new BlockUIContainer(RenderFence(match.Groups[1].Value, match.Groups[2].Value))
            { Margin = new Thickness(0, 10, 0, 10) });
            lastIndex = match.Index + match.Length;
        }
        if (lastIndex < source.Length) RenderBlocks(doc.Blocks, source[lastIndex..]);
        return doc;
    }

    private static Paragraph NewParagraph(double size, string foregroundKey, bool bold = false)
    {
        var paragraph = new Paragraph
        {
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            LineHeight = size + 10,
        };
        Dyn(paragraph, Paragraph.ForegroundProperty, foregroundKey);
        return paragraph;
    }

    private static void RenderBlocks(BlockCollection blocks, string text)
    {
        var paragraph = new List<string>();
        var listItems = new List<(string Marker, string Content)>();
        var listOrdered = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var block = NewParagraph(14, "TextPrimary");
            block.Margin = new Thickness(0, 8, 0, 8);
            block.LineHeight = 24;
            for (var i = 0; i < paragraph.Count; i++)
            {
                if (i > 0) block.Inlines.Add(new LineBreak());
                AddInlines(block.Inlines, paragraph[i]);
            }
            blocks.Add(block);
            paragraph.Clear();
        }

        void FlushList()
        {
            if (listItems.Count == 0) return;
            var isTask = listItems.All(i => Regex.IsMatch(i.Content, @"^\[([ xX])\]\s+"));
            var list = new List
            {
                MarkerStyle = isTask ? TextMarkerStyle.None : listOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = new Thickness(0, 8, 0, 8),
                Padding = new Thickness(24, 0, 0, 0),
            };
            var index = 1;
            foreach (var (marker, content) in listItems)
            {
                var task = Regex.Match(content, @"^\[([ xX])\]\s+(.*)$");
                var bodyText = task.Success ? task.Groups[2].Value : content;
                var itemParagraph = NewParagraph(14, task.Success && task.Groups[1].Value != " " ? "TextTertiary" : "TextPrimary");
                itemParagraph.Margin = new Thickness(0, 3, 0, 3);
                itemParagraph.LineHeight = 24;
                if (task.Success) itemParagraph.Inlines.Add(new Run(task.Groups[1].Value == " " ? "☐ " : "☑ "));
                else if (listOrdered) itemParagraph.Inlines.Add(new Run($"{index++}. "));
                AddInlines(itemParagraph.Inlines, bodyText);
                list.ListItems.Add(new ListItem(itemParagraph));
            }
            blocks.Add(list);
            listItems.Clear();
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var heading = Regex.Match(line, @"^(#{1,4})\s+(.*)$");
            var unordered = Regex.Match(line, @"^\s*[-*]\s+(.*)$");
            var ordered = Regex.Match(line, @"^\s*\d+\.\s+(.*)$");
            var quote = Regex.Match(line, @"^>\s?(.*)$");

            if (heading.Success)
            {
                FlushParagraph(); FlushList();
                var level = Math.Min(heading.Groups[1].Value.Length + 1, 4);
                var size = level switch { 2 => 17, 3 => 15, 4 => 14, _ => 19 };
                var block = NewParagraph(size, "TextPrimary", bold: true);
                block.Margin = new Thickness(0, 14, 0, 8);
                AddInlines(block.Inlines, heading.Groups[2].Value);
                blocks.Add(block);
            }
            else if (line.Contains('|') && i + 1 < lines.Length && TableSeparatorRegex.IsMatch(lines[i + 1]))
            {
                FlushParagraph(); FlushList();
                var alignments = SplitCells(lines[i + 1]).Select(c =>
                    c.StartsWith(':') && c.EndsWith(':') ? TextAlignment.Center
                    : c.EndsWith(':') ? TextAlignment.Right
                    : TextAlignment.Left).ToArray();
                var rows = new List<string[]> { SplitCells(line) };
                i++;
                while (i + 1 < lines.Length && lines[i + 1].Contains('|') && lines[i + 1].Trim().Length > 0)
                    rows.Add(SplitCells(lines[++i]));
                blocks.Add(RenderTable(rows, alignments));
            }
            else if (HrRegex.IsMatch(line))
            {
                if (paragraph.Count > 0 && (line.TrimStart().StartsWith('-') || line.TrimStart().StartsWith('=')))
                {
                    var block = NewParagraph(line.TrimStart().StartsWith('=') ? 19 : 17, "TextPrimary", bold: true);
                    block.Margin = new Thickness(0, 14, 0, 8);
                    foreach (var p in paragraph) AddInlines(block.Inlines, p);
                    blocks.Add(block);
                    paragraph.Clear();
                    FlushList();
                }
                else
                {
                    FlushParagraph(); FlushList();
                    var rule = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 12) };
                    Dyn(rule, Border.BackgroundProperty, "BorderL1");
                    blocks.Add(new BlockUIContainer(rule));
                }
            }
            else if (unordered.Success || ordered.Success)
            {
                FlushParagraph();
                var orderedNow = ordered.Success;
                if (listItems.Count > 0 && listOrdered != orderedNow) FlushList();
                listOrdered = orderedNow;
                listItems.Add((orderedNow ? "ol" : "ul", (ordered.Success ? ordered : unordered).Groups[1].Value));
            }
            else if (quote.Success)
            {
                FlushParagraph(); FlushList();
                var section = new Section
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 4, 12, 4),
                    Margin = new Thickness(0, 8, 0, 8),
                };
                Dyn(section, Section.BorderBrushProperty, "BorderL2");
                var block = NewParagraph(14, "TextSecondary");
                block.Margin = new Thickness(0);
                AddInlines(block.Inlines, quote.Groups[1].Value);
                section.Blocks.Add(block);
                blocks.Add(section);
            }
            else if (line.Trim().Length == 0)
            {
                FlushParagraph(); FlushList();
            }
            else
            {
                FlushList();
                paragraph.Add(line);
            }
        }
        FlushParagraph(); FlushList();
    }

    private static string[] SplitCells(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(c => c.Trim()).ToArray();
    }

    private static Table RenderTable(List<string[]> rows, TextAlignment[] alignments)
    {
        var cols = rows.Max(r => r.Length);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 10, 0, 10), BorderThickness = new Thickness(1) };
        Dyn(table, Table.BorderBrushProperty, "BorderL2");
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new TableRow();
            group.Rows.Add(row);
            for (var c = 0; c < cols; c++)
            {
                var text = c < rows[r].Length ? rows[r][c] : "";
                var paragraph = NewParagraph(13, "TextPrimary", bold: r == 0);
                paragraph.Margin = new Thickness(0);
                paragraph.TextAlignment = c < alignments.Length ? alignments[c] : TextAlignment.Left;
                AddInlines(paragraph.Inlines, text);
                var cell = new TableCell
                {
                    BorderThickness = new Thickness(0, 0, c < cols - 1 ? 1 : 0, r < rows.Count - 1 ? 1 : 0),
                    Padding = new Thickness(10, 5, 10, 5),
                };
                Dyn(cell, TableCell.BorderBrushProperty, "BorderL2");
                cell.Blocks.Add(paragraph);
                row.Cells.Add(cell);
            }
        }
        return table;
    }

    private static void AddInlines(InlineCollection inlines, string text)
    {
        var lastIndex = 0;
        foreach (Match match in InlineRegex.Matches(text))
        {
            if (match.Index > lastIndex) inlines.Add(new Run(text[lastIndex..match.Index]));
            var token = match.Value;
            if (token.StartsWith('`'))
            {
                var code = new TextBlock
                {
                    FontSize = 12.5,
                    FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
                    Text = token[1..^1],
                };
                Dyn(code, TextBlock.ForegroundProperty, "TextPrimary");
                var chip = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    Child = code,
                };
                Dyn(chip, Border.BackgroundProperty, "CodeBg");
                Dyn(chip, Border.BorderBrushProperty, "BorderL1");
                inlines.Add(new InlineUIContainer(chip) { BaselineAlignment = BaselineAlignment.Center });
            }
            else if (token.StartsWith("~~"))
            {
                inlines.Add(new Run(token[2..^2]) { TextDecorations = TextDecorations.Strikethrough });
            }
            else if (token.StartsWith("**"))
            {
                inlines.Add(new Bold(new Run(token[2..^2])));
            }
            else if (token.StartsWith('*'))
            {
                inlines.Add(new Italic(new Run(token[1..^1])));
            }
            else if (token.StartsWith("http"))
            {
                inlines.Add(MakeLink(token, token));
            }
            else
            {
                var link = Regex.Match(token, @"\[([^\]]+)\]\((https?://[^)\s]+)\)");
                inlines.Add(MakeLink(link.Groups[1].Value, link.Groups[2].Value));
            }
            lastIndex = match.Index + match.Length;
        }
        if (lastIndex < text.Length) inlines.Add(new Run(text[lastIndex..]));
    }

    private static Hyperlink MakeLink(string label, string url)
    {
        var hyperlink = new Hyperlink(new Run(label))
        {
            TextDecorations = null,
            NavigateUri = new Uri(url),
        };
        Dyn(hyperlink, TextElement.ForegroundProperty, "Accent");
        hyperlink.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { }
        };
        return hyperlink;
    }

    private static TextBox CodeBox(string text, string? backgroundKey)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            IsUndoEnabled = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontSize = 12.5,
            FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Dyn(box, Control.ForegroundProperty, "TextPrimary");
        if (backgroundKey is not null) Dyn(box, Control.BackgroundProperty, backgroundKey);
        return box;
    }

    private static FrameworkElement RenderFence(string lang, string code)
    {
        var trimmed = code.TrimEnd('\n');
        var isDiff = lang is "diff" or "patch";

        var bodyPanel = new StackPanel();
        if (isDiff)
        {
            foreach (var line in trimmed.Split('\n'))
            {
                var backgroundKey = line.StartsWith('+') ? "DiffAddBg"
                    : line.StartsWith('-') ? "DiffDelBg" : null;
                bodyPanel.Children.Add(CodeBox(line, backgroundKey));
            }
        }
        else
        {
            bodyPanel.Children.Add(CodeBox(trimmed, null));
        }

        var copyBtn = new Button
        {
            Content = "复制",
            FontSize = 11,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(6, 2, 6, 2),
        };
        Dyn(copyBtn, Control.ForegroundProperty, "TextSecondary");
        copyBtn.Click += (_, _) =>
        {
            try { Clipboard.SetText(trimmed); } catch { }
            copyBtn.Content = "已复制";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += (_, _) => { copyBtn.Content = "复制"; timer.Stop(); };
            timer.Start();
        };

        var header = new DockPanel();
        Dyn(header, Panel.BackgroundProperty, "BgLayer2");
        var langText = new TextBlock
        {
            FontSize = 11,
            FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
            Text = string.IsNullOrEmpty(lang) ? "text" : lang,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Dyn(langText, TextBlock.ForegroundProperty, "TextSecondary");
        DockPanel.SetDock(copyBtn, Dock.Right);
        header.Children.Add(copyBtn);
        header.Children.Add(langText);

        var body = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 10, 12, 10),
            Content = bodyPanel,
        };
        Dyn(body, Control.BackgroundProperty, "CodeBg");

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(body);
        var outer = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Child = stack,
        };
        Dyn(outer, Border.BorderBrushProperty, "BorderL1");
        return outer;
    }
}