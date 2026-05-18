using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace GitKay.UI;

internal static class SyntaxHighlighting
{
    private const int MaxTokenizedLineLength = 240;

    private static readonly Regex HunkHeaderRegex = new(
        @"^(@@)\s+(-\d+(?:,\d+)?)\s+(\+\d+(?:,\d+)?)\s+(@@)(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IBrush KeywordBrush = new SolidColorBrush(Color.FromRgb(86, 156, 214));
    private static readonly IBrush StringBrush = new SolidColorBrush(Color.FromRgb(206, 145, 120));
    private static readonly IBrush NumberBrush = new SolidColorBrush(Color.FromRgb(181, 206, 168));
    private static readonly IBrush CommentBrush = new SolidColorBrush(Color.FromRgb(87, 166, 74));
    private static readonly IBrush TypeBrush = new SolidColorBrush(Color.FromRgb(78, 201, 176));
    private static readonly IBrush HunkMarkerBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180));
    private static readonly IBrush HunkRangeBrush = new SolidColorBrush(Color.FromRgb(215, 186, 125));

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "abstract", "and", "as", "async", "await", "begin", "bool", "break", "case", "catch", "class",
        "const", "continue", "default", "delegate", "do", "done", "else", "enum", "exception", "false",
        "finally", "fn", "for", "from", "fun", "function", "if", "in", "interface", "internal",
        "let", "match", "member", "module", "mut", "namespace", "new", "null", "open", "or",
        "override", "private", "public", "readonly", "rec", "return", "sealed", "static", "struct",
        "then", "true", "try", "type", "using", "val", "var", "virtual", "when", "where", "while",
        "with", "yield"
    };

    public static InlineCollection BuildCodeInlines(string text, IBrush baseForeground, string? searchQuery = null)
    {
        var collection = new InlineCollection();

        if (string.IsNullOrEmpty(text))
        {
            return collection;
        }

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var index = text.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                AppendSegment(collection, text[..index], baseForeground);
                AppendSearchMatch(collection, text.Substring(index, searchQuery.Length));
                AppendSegment(collection, text[(index + searchQuery.Length)..], baseForeground);
                return collection;
            }
        }

        AppendSegment(collection, text, baseForeground);
        return collection;
    }

    public static InlineCollection BuildHunkHeaderInlines(string header)
    {
        var collection = new InlineCollection();

        if (string.IsNullOrEmpty(header))
        {
            return collection;
        }

        var match = HunkHeaderRegex.Match(header);
        if (!match.Success)
        {
            collection.Add(new Run { Text = header, Foreground = HunkMarkerBrush });
            return collection;
        }

        collection.Add(new Run { Text = match.Groups[1].Value, Foreground = HunkMarkerBrush, FontWeight = FontWeight.SemiBold });
        collection.Add(new Run { Text = " ", Foreground = HunkMarkerBrush });
        collection.Add(new Run { Text = match.Groups[2].Value, Foreground = HunkRangeBrush });
        collection.Add(new Run { Text = " ", Foreground = HunkMarkerBrush });
        collection.Add(new Run { Text = match.Groups[3].Value, Foreground = HunkRangeBrush });
        collection.Add(new Run { Text = " ", Foreground = HunkMarkerBrush });
        collection.Add(new Run { Text = match.Groups[4].Value, Foreground = HunkMarkerBrush, FontWeight = FontWeight.SemiBold });

        var tail = match.Groups[5].Value;
        if (!string.IsNullOrEmpty(tail))
        {
            collection.Add(new Run { Text = tail, Foreground = CommentBrush });
        }

        return collection;
    }

    private static void AppendSearchMatch(InlineCollection collection, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        collection.Add(new Run
        {
            Text = text,
            Foreground = DiffSearchPresentation.MatchForeground,
            FontWeight = DiffSearchPresentation.MatchFontWeight,
            TextDecorations = TextDecorations.Underline
        });
    }

    private static void AppendTokenizedSegment(InlineCollection collection, string text, IBrush baseForeground)
    {
        foreach (var token in Tokenize(text))
        {
            collection.Add(CreateRun(token, baseForeground));
        }
    }

    private static void AppendSegment(InlineCollection collection, string text, IBrush baseForeground)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (text.Length > MaxTokenizedLineLength)
        {
            collection.Add(new Run { Text = text, Foreground = baseForeground });
            return;
        }

        AppendTokenizedSegment(collection, text, baseForeground);
    }

    private static Run CreateRun(HighlightToken token, IBrush baseForeground)
    {
        return new Run
        {
            Text = token.Text,
            Foreground = token.Kind switch
            {
                HighlightKind.Keyword => KeywordBrush,
                HighlightKind.String => StringBrush,
                HighlightKind.Number => NumberBrush,
                HighlightKind.Comment => CommentBrush,
                HighlightKind.TypeName => TypeBrush,
                _ => baseForeground,
            }
        };
    }

    internal static IReadOnlyList<HighlightToken> Tokenize(string text)
    {
        var tokens = new List<HighlightToken>();
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            if (char.IsWhiteSpace(current))
            {
                var start = index;
                index = ConsumeWhile(text, index, char.IsWhiteSpace);
                tokens.Add(new HighlightToken(text[start..index], HighlightKind.Plain));
                continue;
            }

            if (TryConsumeLineComment(text, index, out var commentLength))
            {
                tokens.Add(new HighlightToken(text[index..(index + commentLength)], HighlightKind.Comment));
                index += commentLength;
                continue;
            }

            if (TryConsumeBlockComment(text, index, out commentLength))
            {
                tokens.Add(new HighlightToken(text[index..(index + commentLength)], HighlightKind.Comment));
                index += commentLength;
                continue;
            }

            if (TryConsumeString(text, index, out var stringLength))
            {
                tokens.Add(new HighlightToken(text[index..(index + stringLength)], HighlightKind.String));
                index += stringLength;
                continue;
            }

            if (char.IsDigit(current))
            {
                var start = index;
                index = ConsumeNumber(text, index);
                tokens.Add(new HighlightToken(text[start..index], HighlightKind.Number));
                continue;
            }

            if (IsIdentifierStart(current))
            {
                var start = index;
                index = ConsumeIdentifier(text, index);
                var word = text[start..index];
                tokens.Add(new HighlightToken(word, ClassifyIdentifier(word)));
                continue;
            }

            tokens.Add(new HighlightToken(current.ToString(), HighlightKind.Plain));
            index++;
        }

        return tokens;
    }

    private static HighlightKind ClassifyIdentifier(string word)
    {
        if (Keywords.Contains(word))
        {
            return HighlightKind.Keyword;
        }

        if (word.Length > 1 && char.IsUpper(word[0]) && word.All(char.IsLetterOrDigit))
        {
            return HighlightKind.TypeName;
        }

        return HighlightKind.Plain;
    }

    private static bool TryConsumeLineComment(string text, int index, out int length)
    {
        if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '/' && IsCommentBoundary(text, index))
        {
            length = text.Length - index;
            return true;
        }

        if (text[index] == '#' && IsCommentBoundary(text, index))
        {
            length = text.Length - index;
            return true;
        }

        length = 0;
        return false;
    }

    private static bool TryConsumeBlockComment(string text, int index, out int length)
    {
        if (index + 1 >= text.Length || text[index] != '/' || text[index + 1] != '*')
        {
            length = 0;
            return false;
        }

        var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
        if (end < 0)
        {
            length = text.Length - index;
            return true;
        }

        length = end - index + 2;
        return true;
    }

    private static bool TryConsumeString(string text, int index, out int length)
    {
        var quote = text[index];
        if (quote is not ('"' or '\'' or '`'))
        {
            length = 0;
            return false;
        }

        var cursor = index + 1;
        while (cursor < text.Length)
        {
            if (text[cursor] == '\\')
            {
                cursor += 2;
                continue;
            }

            if (text[cursor] == quote)
            {
                length = cursor - index + 1;
                return true;
            }

            cursor++;
        }

        length = text.Length - index;
        return true;
    }

    private static int ConsumeNumber(string text, int index)
    {
        return ConsumeWhile(text, index, c => char.IsDigit(c) || c is '.' or '_' or 'x' or 'X' or 'b' or 'B' or 'e' or 'E' or '+' or '-');
    }

    private static int ConsumeIdentifier(string text, int index)
    {
        return ConsumeWhile(text, index, IsIdentifierPart);
    }

    private static int ConsumeWhile(string text, int index, Func<char, bool> predicate)
    {
        var cursor = index;
        while (cursor < text.Length && predicate(text[cursor]))
        {
            cursor++;
        }

        return cursor;
    }

    private static bool IsIdentifierStart(char value) =>
        char.IsLetter(value) || value == '_' || value == '@';

    private static bool IsIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_' || value == '@' || value == '-';

    private static bool IsCommentBoundary(string text, int index) =>
        index == 0 || char.IsWhiteSpace(text[index - 1]);
}

internal enum HighlightKind
{
    Plain,
    Keyword,
    String,
    Number,
    Comment,
    TypeName,
}

internal readonly record struct HighlightToken(string Text, HighlightKind Kind);
