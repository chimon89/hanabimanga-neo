using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace hanabimanga.Controls
{
    // 轻量 Markdown 渲染:把公告正文渲染为遵循 WinUI 字体阶梯的原生控件。
    // 支持的块级语法:# / ## / ### 标题、段落、有序 / 无序列表、> 引用块、--- 分割线;
    // 行内语法:**粗体**、*斜体*、`代码`、[文本](链接)。
    public static class MarkdownRenderer
    {
        public static void Render(Panel host, string? markdown)
        {
            host.Children.Clear();
            if (string.IsNullOrWhiteSpace(markdown)) return;

            var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var i = 0;
            while (i < lines.Length)
            {
                var trimmed = lines[i].Trim();

                if (trimmed.Length == 0) { i++; continue; }

                if (IsHorizontalRule(trimmed))
                {
                    host.Children.Add(CreateRule());
                    i++;
                    continue;
                }

                if (TryHeading(trimmed) is { } heading)
                {
                    host.Children.Add(CreateHeading(heading.Level, heading.Text));
                    i++;
                    continue;
                }

                // 引用块:连续的 > 行
                if (trimmed.StartsWith(">"))
                {
                    var quote = new List<string>();
                    while (i < lines.Length && lines[i].Trim().StartsWith(">"))
                    {
                        quote.Add(lines[i].Trim().TrimStart('>').Trim());
                        i++;
                    }
                    host.Children.Add(CreateBlockquote(string.Join("\n", quote)));
                    continue;
                }

                // 列表:连续的列表行
                if (TryListItem(trimmed, out _, out _))
                {
                    var items = new List<(bool Ordered, string Content)>();
                    while (i < lines.Length && lines[i].Trim().Length > 0 &&
                           TryListItem(lines[i].Trim(), out var ordered, out var content))
                    {
                        items.Add((ordered, content));
                        i++;
                    }
                    host.Children.Add(CreateList(items));
                    continue;
                }

                // 独占一行的图片
                if (TryImage(trimmed) is { } image)
                {
                    host.Children.Add(CreateImageBlock(image.Alt, image.Url));
                    i++;
                    continue;
                }

                // 段落:连续的普通行
                var paragraph = new List<string>();
                while (i < lines.Length)
                {
                    var t = lines[i].Trim();
                    if (t.Length == 0 || IsHorizontalRule(t) || TryHeading(t) is not null ||
                        t.StartsWith(">") || TryListItem(t, out _, out _) || TryImage(t) is not null)
                    {
                        break;
                    }
                    paragraph.Add(t);
                    i++;
                }
                host.Children.Add(CreateParagraph(string.Join("\n", paragraph)));
            }
        }

        // ---------- 块级 ----------

        private static (int Level, string Text)? TryHeading(string line)
        {
            var hashes = 0;
            while (hashes < line.Length && line[hashes] == '#') hashes++;
            if (hashes is >= 1 and <= 6 && hashes < line.Length && line[hashes] == ' ')
            {
                return (hashes, line[(hashes + 1)..].Trim());
            }
            return null;
        }

        private static bool IsHorizontalRule(string line)
        {
            if (line.Length < 3) return false;
            return AllChars(line, '-') || AllChars(line, '*') || AllChars(line, '_');
        }

        private static bool AllChars(string s, char c)
        {
            foreach (var ch in s)
            {
                if (ch != c) return false;
            }
            return true;
        }

        private static bool TryListItem(string line, out bool ordered, out string content)
        {
            ordered = false;
            content = "";

            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ "))
            {
                content = line[2..].Trim();
                return true;
            }
            if (line.StartsWith("•"))
            {
                content = line[1..].Trim();
                return true;
            }

            var match = Regex.Match(line, @"^(\d+)[.)]\s+(.*)$");
            if (match.Success)
            {
                ordered = true;
                content = match.Groups[2].Value.Trim();
                return true;
            }
            return false;
        }

        private static TextBlock CreateHeading(int level, string text)
        {
            var styleKey = level switch
            {
                1 => "TitleTextBlockStyle",
                2 => "SubtitleTextBlockStyle",
                _ => "BodyStrongTextBlockStyle",
            };
            var heading = new TextBlock
            {
                Style = Resource<Style>(styleKey),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Margin = level == 1 ? new Thickness(0) : new Thickness(0, 8, 0, 0),
            };
            AppendInlines(heading.Inlines, text);
            return heading;
        }

        private static TextBlock CreateParagraph(string text)
        {
            var paragraph = CreateBodyTextBlock(text);
            return paragraph;
        }

        private static TextBlock CreateBodyTextBlock(string text)
        {
            var block = new TextBlock
            {
                Style = Resource<Style>("BodyTextBlockStyle"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
            AppendInlines(block.Inlines, text);
            return block;
        }

        private static Border CreateBlockquote(string text)
        {
            var block = new TextBlock
            {
                Style = Resource<Style>("BodyTextBlockStyle"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
            };
            AppendInlines(block.Inlines, text);

            return new Border
            {
                BorderThickness = new Thickness(3, 0, 0, 0),
                BorderBrush = Resource<Brush>("AccentFillColorDefaultBrush"),
                Padding = new Thickness(12, 2, 0, 2),
                Child = block,
            };
        }

        private static Border CreateRule() => new()
        {
            Height = 1,
            Margin = new Thickness(0, 4, 0, 4),
            Background = Resource<Brush>("DividerStrokeColorDefaultBrush"),
        };

        private static (string Alt, string Url)? TryImage(string line)
        {
            var match = Regex.Match(line, @"^!\[([^\]]*)\]\(([^)]+)\)$");
            return match.Success
                ? (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim())
                : null;
        }

        private static UIElement CreateImageBlock(string alt, string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return CreateParagraph(string.IsNullOrWhiteSpace(alt) ? url : alt);
            }

            var element = CreateImageElement(uri);
            if (string.IsNullOrWhiteSpace(alt)) return element;

            var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(element);
            panel.Children.Add(new TextBlock
            {
                Text = alt,
                Style = Resource<Style>("CaptionTextBlockStyle"),
                Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            return panel;
        }

        private static Border CreateImageElement(Uri uri) => new()
        {
            CornerRadius = new CornerRadius(8),
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Resource<Brush>("CardBackgroundFillColorDefaultBrush"),
            Child = new Image
            {
                Source = new BitmapImage(uri),
                Stretch = Stretch.Uniform,
            },
        };

        private static StackPanel CreateList(IReadOnlyList<(bool Ordered, string Content)> items)
        {
            var panel = new StackPanel { Spacing = 4 };
            var number = 0;
            foreach (var (ordered, content) in items)
            {
                number++;

                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var marker = new TextBlock
                {
                    Style = Resource<Style>("BodyTextBlockStyle"),
                    Text = ordered ? $"{number}." : "•",
                    MinWidth = 18,
                    Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
                };
                Grid.SetColumn(marker, 0);

                var body = CreateBodyTextBlock(content);
                Grid.SetColumn(body, 1);

                row.Children.Add(marker);
                row.Children.Add(body);
                panel.Children.Add(row);
            }
            return panel;
        }

        // ---------- 行内 ----------

        private static void AppendInlines(InlineCollection target, string text)
        {
            var segments = text.Split('\n');
            for (var s = 0; s < segments.Length; s++)
            {
                if (s > 0) target.Add(new LineBreak());
                ParseInlineSegment(target, segments[s]);
            }
        }

        private static void ParseInlineSegment(InlineCollection target, string text)
        {
            var literal = new StringBuilder();
            var pos = 0;

            void FlushLiteral()
            {
                if (literal.Length > 0)
                {
                    target.Add(new Run { Text = literal.ToString() });
                    literal.Clear();
                }
            }

            while (pos < text.Length)
            {
                var c = text[pos];

                // ![替代文本](图片链接)
                if (c == '!' && pos + 1 < text.Length && text[pos + 1] == '[')
                {
                    var altClose = text.IndexOf(']', pos + 2);
                    if (altClose > pos && altClose + 1 < text.Length && text[altClose + 1] == '(')
                    {
                        var urlEnd = text.IndexOf(')', altClose + 2);
                        if (urlEnd > altClose)
                        {
                            FlushLiteral();
                            var alt = text.Substring(pos + 2, altClose - pos - 2);
                            var url = text.Substring(altClose + 2, urlEnd - altClose - 2).Trim();
                            target.Add(CreateInlineImage(alt, url));
                            pos = urlEnd + 1;
                            continue;
                        }
                    }
                }

                // [文本](链接)
                if (c == '[')
                {
                    var close = text.IndexOf(']', pos + 1);
                    if (close > pos && close + 1 < text.Length && text[close + 1] == '(')
                    {
                        var urlClose = text.IndexOf(')', close + 2);
                        if (urlClose > close)
                        {
                            FlushLiteral();
                            var linkText = text.Substring(pos + 1, close - pos - 1);
                            var url = text.Substring(close + 2, urlClose - close - 2).Trim();
                            target.Add(CreateLink(linkText, url));
                            pos = urlClose + 1;
                            continue;
                        }
                    }
                }

                // **粗体**
                if (c == '*' && pos + 1 < text.Length && text[pos + 1] == '*')
                {
                    var end = text.IndexOf("**", pos + 2, StringComparison.Ordinal);
                    if (end > pos + 1)
                    {
                        FlushLiteral();
                        var bold = new Bold();
                        ParseInlineSegment(bold.Inlines, text.Substring(pos + 2, end - pos - 2));
                        target.Add(bold);
                        pos = end + 2;
                        continue;
                    }
                }

                // *斜体* 或 _斜体_
                if (c is '*' or '_')
                {
                    var end = text.IndexOf(c, pos + 1);
                    if (end > pos + 1)
                    {
                        FlushLiteral();
                        var italic = new Italic();
                        ParseInlineSegment(italic.Inlines, text.Substring(pos + 1, end - pos - 1));
                        target.Add(italic);
                        pos = end + 1;
                        continue;
                    }
                }

                // `代码`
                if (c == '`')
                {
                    var end = text.IndexOf('`', pos + 1);
                    if (end > pos)
                    {
                        FlushLiteral();
                        target.Add(new Run
                        {
                            Text = text.Substring(pos + 1, end - pos - 1),
                            FontFamily = new FontFamily("Consolas"),
                        });
                        pos = end + 1;
                        continue;
                    }
                }

                literal.Append(c);
                pos++;
            }
            FlushLiteral();
        }

        private static Inline CreateLink(string text, string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var link = new Hyperlink { NavigateUri = uri };
                ParseInlineSegment(link.Inlines, text);
                return link;
            }

            var run = new Run { Text = text };
            return run;
        }

        private static Inline CreateInlineImage(string alt, string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return new InlineUIContainer { Child = CreateImageElement(uri) };
            }
            return new Run { Text = string.IsNullOrWhiteSpace(alt) ? url : alt };
        }

        private static T Resource<T>(string key) => (T)Application.Current.Resources[key];
    }
}
