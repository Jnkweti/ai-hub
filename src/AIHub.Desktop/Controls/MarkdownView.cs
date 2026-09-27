using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Block = System.Windows.Documents.Block;
using Inline = System.Windows.Documents.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;

namespace AIHub.Desktop.Controls;

public sealed class MarkdownView : RichTextBox
{
    private const int PreviewCharacters = 100000;
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder { MaximumNestingDepth = 32 }.UsePipeTables().UseEmphasisExtras().DisableHtml().Build();
    private sealed class RenderBudget
    {
        private int remaining = 5000;
        public void Visit(int depth) { if (--remaining < 0 || depth > 32) throw new InvalidOperationException("Rich text complexity limit reached."); }
    }
    private readonly DispatcherTimer renderTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
    private string rendered = "";
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata("", (d, _) => ((MarkdownView)d).Schedule()));
    public string Markdown { get => (string)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }
    public MarkdownView()
    {
        IsReadOnly = true; IsDocumentEnabled = true; BorderThickness = new(0); Padding = new(0);
        Background = Brushes.Transparent; Foreground = Theme.Brush("TextBrush"); SelectionBrush = Theme.Brush("SelectionBrush");
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Loaded += (_, _) => RenderMarkdown(); Unloaded += (_, _) => renderTimer.Stop();
        renderTimer.Tick += (_, _) => { renderTimer.Stop(); RenderMarkdown(); };
        SelectionChanged += (_, _) => { if (Selection.IsEmpty && rendered != Markdown) Schedule(); };
    }
    private void Schedule() { if (IsLoaded && !renderTimer.IsEnabled) renderTimer.Start(); }
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (VisualTreeHelper.GetParent(this) is UIElement parent)
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this });
    }
    private void RenderMarkdown()
    {
        if (rendered == Markdown || !Selection.IsEmpty) return;
        var source = Markdown ?? "";
        var preview = source[..Math.Min(source.Length, PreviewCharacters)];
        var document = new FlowDocument { FontFamily = Theme.Font("BodyFont"), FontSize = 15, Foreground = Foreground, PagePadding = new(0), LineHeight = 25 };
        var budget = new RenderBudget();
        try { foreach (var block in Markdig.Markdown.Parse(preview, Pipeline)) document.Blocks.Add(RenderBlock(block, budget, 0)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        { document.Blocks.Clear(); document.Blocks.Add(new Paragraph(new Run(preview))); }
        if (source.Length > preview.Length)
            document.Blocks.Add(new Paragraph(new Run("Long message preview limited to 100,000 characters. Copy or export the message to read it in full.")) { Foreground = Theme.Brush("MutedBrush") });
        rendered = source; Document = document;
    }
    private static Block RenderBlock(Markdig.Syntax.Block block, RenderBudget budget, int depth)
    {
        budget.Visit(depth);
        switch (block)
        {
            case HeadingBlock heading:
                var title = Paragraph(heading.Inline, budget, depth); title.FontSize = heading.Level switch { 1 => 23, 2 => 20, 3 => 17, _ => 15 };
                title.FontFamily = Theme.Font("DisplayFont"); title.FontWeight = FontWeights.SemiBold; title.Foreground = Theme.Brush("TextBrush"); title.Margin = new(0, 8, 0, 12); return title;
            case ParagraphBlock paragraph: return Paragraph(paragraph.Inline, budget, depth);
            case CodeBlock code:
                return new Paragraph(new Run(code.Lines.ToString())) { FontFamily = Theme.Font("CodeFont"), FontSize = 13, LineHeight = 21, Background = Theme.Brush("PanelBrush"), Foreground = Theme.Brush("CodeBrush"), Padding = new(16,12,16,12), BorderBrush = Theme.Brush("LineBrush"), BorderThickness = new(2,0,0,0), Margin = new(0,8,0,14) };
            case QuoteBlock quote:
                var quoted = new Section { BorderThickness = new(2,0,0,0), BorderBrush = Theme.Brush("CyanBrush"), Padding = new(14,3,0,3), Margin = new(0,8,0,12), Foreground = Theme.Brush("MutedBrush") };
                foreach (var child in quote) quoted.Blocks.Add(RenderBlock(child, budget, depth + 1)); return quoted;
            case ListBlock list:
                var result = new System.Windows.Documents.List { MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Padding = new(21,0,0,0), Margin = new(0,3,0,10) };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0) result.StartIndex = start;
                foreach (var child in list.OfType<ListItemBlock>())
                { var item = new ListItem { Margin = new(0,0,0,3) }; foreach (var content in child) item.Blocks.Add(RenderBlock(content, budget, depth + 1)); result.ListItems.Add(item); }
                return result;
            case MdTable table:
                var renderedTable = new System.Windows.Documents.Table { CellSpacing = 0, Margin = new(0,8,0,12), FontSize = 13 };
                var rows = new TableRowGroup(); renderedTable.RowGroups.Add(rows);
                foreach (var row in table.OfType<MdTableRow>())
                {
                    var resultRow = new TableRow(); rows.Rows.Add(resultRow);
                    foreach (var cell in row.OfType<MdTableCell>())
                    {
                        var resultCell = new TableCell { BorderThickness = new(0,0,0,1), BorderBrush = Theme.Brush("LineBrush"), Padding = new(10,8,10,8), Background = row.IsHeader ? Theme.Brush("SurfaceBrush") : Brushes.Transparent, FontWeight = row.IsHeader ? FontWeights.SemiBold : FontWeights.Normal };
                        foreach (var child in cell) resultCell.Blocks.Add(RenderBlock(child, budget, depth + 1)); resultRow.Cells.Add(resultCell);
                    }
                }
                return renderedTable;
            case ThematicBreakBlock: return new Paragraph { BorderThickness = new(0,0,0,1), BorderBrush = Theme.Brush("LineBrush"), Margin = new(0,8,0,12), FontSize = 1, LineHeight = 1 };
            case ContainerBlock container:
                var section = new Section(); foreach (var child in container) section.Blocks.Add(RenderBlock(child, budget, depth + 1)); return section;
            case LeafBlock leaf: return Paragraph(leaf.Inline, budget, depth);
            default: return new Paragraph();
        }
    }
    private static Paragraph Paragraph(ContainerInline? inline, RenderBudget budget, int depth)
    {
        var paragraph = new Paragraph { Margin = new(0,0,0,8) };
        if (inline is not null) foreach (var child in inline) paragraph.Inlines.Add(RenderInline(child, budget, depth + 1));
        return paragraph;
    }
    private static Inline RenderInline(Markdig.Syntax.Inlines.Inline inline, RenderBudget budget, int depth)
    {
        budget.Visit(depth);
        switch (inline)
        {
            case LiteralInline literal: return new Run(literal.Content.ToString());
            case CodeInline code: return new Run(code.Content) { FontFamily = Theme.Font("CodeFont"), FontSize = 13, Background = Theme.Brush("SurfaceBrush"), Foreground = Theme.Brush("CodeBrush") };
            case LineBreakInline line: return line.IsHard ? new LineBreak() : new Run(" ");
            case EmphasisInline emphasis:
                Span span = emphasis.DelimiterChar == '~' ? new Span { TextDecorations = TextDecorations.Strikethrough } : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                foreach (var child in emphasis) span.Inlines.Add(RenderInline(child, budget, depth + 1)); return span;
            case LinkInline link:
                if (!link.IsImage && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                {
                    var hyperlink = new Hyperlink { NavigateUri = uri, Foreground = Theme.Brush("CyanBrush"), ToolTip = uri.AbsoluteUri };
                    foreach (var child in link) hyperlink.Inlines.Add(RenderInline(child, budget, depth + 1));
                    hyperlink.RequestNavigate += (_, e) => { try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { } e.Handled = true; };
                    return hyperlink;
                }
                var label = new Span(); foreach (var child in link) label.Inlines.Add(RenderInline(child, budget, depth + 1)); return label;
            case ContainerInline container:
                var group = new Span(); foreach (var child in container) group.Inlines.Add(RenderInline(child, budget, depth + 1)); return group;
            default: return new Run(inline.ToString());
        }
    }
}
