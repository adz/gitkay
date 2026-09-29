using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace GitKay.UI;

/// <summary>A selectable text block that turns http(s) URLs in its text into links: underlined in the accent colour,
/// a hand cursor over them, and a plain click (not a drag-select) opens the system browser. Detection lives in
/// <c>GitKay.Core.Links</c>.</summary>
public class LinkTextBlock : SelectableTextBlock {
    private Point _pressedAt;
    private bool _pressedOnLink;

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    static LinkTextBlock() {
        TextProperty.Changed.AddClassHandler<LinkTextBlock>((block, _) => block.Rebuild());
    }

    private void Rebuild() {
        var text = Text ?? "";
        var spans = GitKay.Core.Links.find(text);
        if (spans.IsEmpty) {
            Inlines?.Clear();
            return;
        }

        // Setting Inlines replaces Text, so remember it: the runs below spell out the same characters.
        var inlines = new InlineCollection();
        var position = 0;
        foreach (var span in spans) {
            if (span.Start > position) inlines.Add(new Run(text.Substring(position, span.Start - position)));
            var link = new Run(text.Substring(span.Start, span.Length)) { TextDecorations = Avalonia.Media.TextDecorations.Underline };
            link.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("GitKayAccentBrush"));
            inlines.Add(link);
            position = span.Start + span.Length;
        }

        if (position < text.Length) inlines.Add(new Run(text.Substring(position)));
        _linkedFor = text;
        Inlines = inlines;
    }

    private string? _linkedFor;

    internal string? LinkAt(Point point) {
        var text = _linkedFor ?? Text;
        if (string.IsNullOrEmpty(text)) return null;
        var local = new Point(point.X - Padding.Left, point.Y - Padding.Top);
        var hit = TextLayout.HitTestPoint(local);
        var index = hit.TextPosition;
        // A point past the end of a short line still resolves to that line's last character; only a point on the
        // characters themselves counts, so blank space beside a link is not a click on it.
        var onCharacter = false;
        foreach (var rect in TextLayout.HitTestTextRange(index, 1)) onCharacter |= rect.Contains(local);
        if (!onCharacter) return null;
        var found = GitKay.Core.Links.linkAt(text, index);
        return found == null ? null : found.Value;
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        Cursor = LinkAt(e.GetPosition(this)) != null ? new Cursor(StandardCursorType.Hand) : null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        _pressedAt = e.GetPosition(this);
        _pressedOnLink = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && LinkAt(_pressedAt) != null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        if (!_pressedOnLink) return;
        _pressedOnLink = false;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _pressedAt.X) > 3 || Math.Abs(point.Y - _pressedAt.Y) > 3) return;
        if (LinkAt(point) is { } url) OpenUrl(url);
    }

    /// <summary>Opens the URL in the default browser; only http(s) is ever passed to the shell.</summary>
    public static void OpenUrl(string url) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { ArgumentList = { uri.AbsoluteUri }, UseShellExecute = false });
        }
        catch (Exception exception) {
            Trace.WriteLine($"[links] could not open {url}: {exception.Message}");
        }
    }
}
