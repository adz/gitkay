using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace GitKay.UI;

/// <summary>How a file's lines are coloured: as code, or as prose where code rules do more harm than good.</summary>
public enum SyntaxFlavour {
    Code,
    Markdown,
    PlainText,
}

internal static class SyntaxHighlighting {
    private const int MaxTokenizedLineLength = 240;

    /// <summary>
    /// Prose isn't code: in markdown a leading # is a heading, not a comment, and words like "type", "for" and "with"
    /// are just words. Colouring it with the code rules is what made READMEs look scrambled.
    /// </summary>
    public static SyntaxFlavour FlavourFor(string? path) {
        var extension = System.IO.Path.GetExtension(path ?? "").ToLowerInvariant();
        return extension switch {
            ".md" or ".markdown" or ".mdx" => SyntaxFlavour.Markdown,
            ".txt" or ".rst" or ".adoc" or ".asciidoc" or ".log" or "" => SyntaxFlavour.PlainText,
            _ => SyntaxFlavour.Code,
        };
    }


    private static readonly IBrush KeywordBrush = new SolidColorBrush(Color.FromRgb(86, 156, 214)).ToImmutable();
    private static readonly IBrush StringBrush = new SolidColorBrush(Color.FromRgb(206, 145, 120)).ToImmutable();
    private static readonly IBrush NumberBrush = new SolidColorBrush(Color.FromRgb(181, 206, 168)).ToImmutable();
    private static readonly IBrush CommentBrush = new SolidColorBrush(Color.FromRgb(87, 166, 74)).ToImmutable();
    private static readonly IBrush TypeBrush = new SolidColorBrush(Color.FromRgb(78, 201, 176)).ToImmutable();

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

    public static IBrush GetKeywordBrush() => KeywordBrush;
    public static IBrush GetStringBrush() => StringBrush;
    public static IBrush GetNumberBrush() => NumberBrush;
    public static IBrush GetCommentBrush() => CommentBrush;
    public static IBrush GetTypeBrush() => TypeBrush;

    private static readonly Dictionary<string, IReadOnlyList<HighlightToken>> TokenCache = new(StringComparer.Ordinal);
    private static readonly object TokenCacheLock = new();
    private const int MaxCacheSize = 2000;

    /// <summary>Benchmark hook: measures cold tokenization.</summary>
    internal static void ClearTokenCache() {
        lock (TokenCacheLock) {
            TokenCache.Clear();
        }
    }

    internal static IReadOnlyList<HighlightToken> Tokenize(string text) => Tokenize(text, SyntaxFlavour.Code);

    internal static IReadOnlyList<HighlightToken> Tokenize(string text, SyntaxFlavour flavour) {
        if (string.IsNullOrEmpty(text)) {
            return Array.Empty<HighlightToken>();
        }

        if (flavour == SyntaxFlavour.PlainText) {
            return new List<HighlightToken> { new(text, HighlightKind.Plain) }.AsReadOnly();
        }

        var key = flavour == SyntaxFlavour.Code ? text : (char)flavour + text;
        lock (TokenCacheLock) {
            if (TokenCache.TryGetValue(key, out var cached)) {
                return cached;
            }
        }

        if (flavour == SyntaxFlavour.Markdown) {
            var prose = TokenizeMarkdown(text);
            lock (TokenCacheLock) {
                if (TokenCache.Count >= MaxCacheSize) TokenCache.Clear();
                TokenCache[key] = prose;
            }
            return prose;
        }

        var tokens = new List<HighlightToken>();
        var index = 0;

        while (index < text.Length) {
            var current = text[index];

            if (char.IsWhiteSpace(current)) {
                var start = index;
                index = ConsumeWhile(text, index, char.IsWhiteSpace);
                tokens.Add(new HighlightToken(text[start..index], HighlightKind.Plain));
                continue;
            }

            if (TryConsumeLineComment(text, index, out var commentLength)) {
                tokens.Add(new HighlightToken(text[index..(index + commentLength)], HighlightKind.Comment));
                index += commentLength;
                continue;
            }

            if (TryConsumeBlockComment(text, index, out commentLength)) {
                tokens.Add(new HighlightToken(text[index..(index + commentLength)], HighlightKind.Comment));
                index += commentLength;
                continue;
            }

            if (TryConsumeString(text, index, out var stringLength)) {
                tokens.Add(new HighlightToken(text[index..(index + stringLength)], HighlightKind.String));
                index += stringLength;
                continue;
            }

            if (char.IsDigit(current)) {
                var start = index;
                index = ConsumeNumber(text, index);
                tokens.Add(new HighlightToken(text[start..index], HighlightKind.Number));
                continue;
            }

            if (IsIdentifierStart(current)) {
                var start = index;
                index = ConsumeIdentifier(text, index);
                var word = text[start..index];
                tokens.Add(new HighlightToken(word, ClassifyIdentifier(word)));
                continue;
            }

            // Group non-token characters together as Plain
            var plainStart = index;
            while (index < text.Length) {
                var next = text[index];
                if (char.IsWhiteSpace(next) ||
                    IsIdentifierStart(next) ||
                    char.IsDigit(next) ||
                    (next == '/' && index + 1 < text.Length && (text[index + 1] == '/' || text[index + 1] == '*')) ||
                    next == '#' ||
                    next == '"' || next == '\'' || next == '`') {
                    break;
                }
                index++;
            }

            // A comment marker that is not at a valid comment boundary still belongs to plain
            // text. Ensure this branch always consumes at least one character.
            if (index == plainStart) {
                index++;
            }

            tokens.Add(new HighlightToken(text[plainStart..index], HighlightKind.Plain));
        }

        // Merge adjacent tokens of the same kind
        if (tokens.Count > 1) {
            var merged = new List<HighlightToken>();
            var currentToken = tokens[0];
            for (int i = 1; i < tokens.Count; i++) {
                if (tokens[i].Kind == currentToken.Kind) {
                    currentToken = new HighlightToken(currentToken.Text + tokens[i].Text, currentToken.Kind);
                }
                else {
                    merged.Add(currentToken);
                    currentToken = tokens[i];
                }
            }
            merged.Add(currentToken);
            tokens = merged;
        }

        var result = tokens.AsReadOnly();
        lock (TokenCacheLock) {
            if (TokenCache.Count >= MaxCacheSize) {
                TokenCache.Clear();
            }
            TokenCache[key] = result;
        }

        return result;
    }

    /// <summary>
    /// Markdown's own marks, and nothing else: headings, list and quote markers, code spans and fences, link targets.
    /// The prose between them stays the text colour.
    /// </summary>
    private static IReadOnlyList<HighlightToken> TokenizeMarkdown(string text) {
        var tokens = new List<HighlightToken>();
        var indent = 0;
        while (indent < text.Length && (text[indent] == ' ' || text[indent] == '\t')) indent++;
        var body = text[indent..];

        void Add(string part, HighlightKind kind) {
            if (part.Length > 0) tokens.Add(new HighlightToken(part, kind));
        }

        Add(text[..indent], HighlightKind.Plain);

        // A whole heading line, or a fenced code block's fence, reads as one thing.
        if (body.StartsWith('#') || body.StartsWith("```") || body.StartsWith("~~~")) {
            Add(body, body.StartsWith('#') ? HighlightKind.Keyword : HighlightKind.Comment);
            return tokens.AsReadOnly();
        }

        // List bullets, numbered items, quotes and table rules: the marker only.
        var marker = MarkdownMarker(body);
        if (marker > 0) {
            Add(body[..marker], HighlightKind.Comment);
            body = body[marker..];
        }

        var index = 0;
        var plain = 0;
        while (index < body.Length) {
            var current = body[index];
            if (current == '`') {
                var close = body.IndexOf('`', index + 1);
                var end = close < 0 ? body.Length : close + 1;
                Add(body[plain..index], HighlightKind.Plain);
                Add(body[index..end], HighlightKind.String);
                index = plain = end;
                continue;
            }

            // A link or image: [text](target) — colour the target, leave the text alone.
            if (current == '(' && index > 0 && body[index - 1] == ']') {
                var close = body.IndexOf(')', index + 1);
                var end = close < 0 ? body.Length : close + 1;
                Add(body[plain..index], HighlightKind.Plain);
                Add(body[index..end], HighlightKind.TypeName);
                index = plain = end;
                continue;
            }

            index++;
        }

        Add(body[plain..], HighlightKind.Plain);
        return tokens.AsReadOnly();
    }

    /// <summary>How many characters of a list bullet, numbered item or quote marker start this line.</summary>
    private static int MarkdownMarker(string body) {
        if (body.StartsWith("> ") || body == ">") return body.Length >= 2 ? 2 : 1;
        if ((body.StartsWith("- ") || body.StartsWith("* ") || body.StartsWith("+ ")) && body.Length > 2) return 2;
        var digits = 0;
        while (digits < body.Length && char.IsDigit(body[digits])) digits++;
        if (digits > 0 && digits + 1 < body.Length && (body[digits] == '.' || body[digits] == ')') && body[digits + 1] == ' ') return digits + 2;
        return 0;
    }

    private static HighlightKind ClassifyIdentifier(string word) {
        if (Keywords.Contains(word)) {
            return HighlightKind.Keyword;
        }

        if (word.Length > 1 && char.IsUpper(word[0]) && word.All(char.IsLetterOrDigit)) {
            return HighlightKind.TypeName;
        }

        return HighlightKind.Plain;
    }

    private static bool TryConsumeLineComment(string text, int index, out int length) {
        if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '/' && IsCommentBoundary(text, index)) {
            length = text.Length - index;
            return true;
        }

        if (text[index] == '#' && IsCommentBoundary(text, index)) {
            length = text.Length - index;
            return true;
        }

        length = 0;
        return false;
    }

    private static bool TryConsumeBlockComment(string text, int index, out int length) {
        if (index + 1 >= text.Length || text[index] != '/' || text[index + 1] != '*') {
            length = 0;
            return false;
        }

        var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
        if (end < 0) {
            length = text.Length - index;
            return true;
        }

        length = end - index + 2;
        return true;
    }

    private static bool TryConsumeString(string text, int index, out int length) {
        var quote = text[index];
        if (quote is not ('"' or '\'' or '`')) {
            length = 0;
            return false;
        }

        var cursor = index + 1;
        while (cursor < text.Length) {
            if (text[cursor] == '\\') {
                cursor += 2;
                continue;
            }

            if (text[cursor] == quote) {
                length = cursor - index + 1;
                return true;
            }

            cursor++;
        }

        length = text.Length - index;
        return true;
    }

    private static int ConsumeNumber(string text, int index) {
        return ConsumeWhile(text, index, c => char.IsDigit(c) || c is '.' or '_' or 'x' or 'X' or 'b' or 'B' or 'e' or 'E' or '+' or '-');
    }

    private static int ConsumeIdentifier(string text, int index) {
        return ConsumeWhile(text, index, IsIdentifierPart);
    }

    private static int ConsumeWhile(string text, int index, Func<char, bool> predicate) {
        var cursor = index;
        while (cursor < text.Length && predicate(text[cursor])) {
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

internal enum HighlightKind {
    Plain,
    Keyword,
    String,
    Number,
    Comment,
    TypeName,
}

internal readonly record struct HighlightToken(string Text, HighlightKind Kind);
