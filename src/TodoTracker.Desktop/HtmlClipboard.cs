using System.Globalization;
using System.Text;

namespace TodoTracker.Desktop;

/// <summary>The Windows clipboard's HTML format (CF_HTML): a header of byte offsets, then the page.</summary>
public static class HtmlClipboard
{
    private const string Before = "<html><body><!--StartFragment-->";
    private const string After = "<!--EndFragment--></body></html>";

    /// <summary>A link named <paramref name="title"/> (or "Task"), both escaped.</summary>
    public static string Anchor(string url, string title)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "Task" : title.Trim();
        return $"<a href=\"{Escape(url)}\">{Escape(name)}</a>";
    }

    // Only markup characters: WebUtility.HtmlEncode would also turn letters like "é" into entities.
    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);

    public static string Format(string fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        var headerLength = Header(0, 0, 0, 0).Length;
        var startFragment = headerLength + Encoding.UTF8.GetByteCount(Before);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(After);
        return Header(headerLength, endHtml, startFragment, endFragment) + Before + fragment + After;
    }

    private static string Header(int startHtml, int endHtml, int startFragment, int endFragment) => string.Create(
        CultureInfo.InvariantCulture,
        $"Version:0.9\r\nStartHTML:{startHtml:D10}\r\nEndHTML:{endHtml:D10}\r\nStartFragment:{startFragment:D10}\r\nEndFragment:{endFragment:D10}\r\n");
}
