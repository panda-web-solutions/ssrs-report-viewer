using System.Text.RegularExpressions;

namespace SsrsReportViewer.Api.Reports;

/// <summary>
/// Maps SSRS HTML Find hit chrome to ReportViewer <c>searchHighlighting</c> (orange #FFC20D).
/// SOAP HTML4.0 emits system colors (<c>BACKGROUND-COLOR:highlight</c>); the native control uses the class.
/// </summary>
internal static partial class FindHighlightNormalizer
{
    public static string Normalize(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html;
        }

        return HitSpanRegex().Replace(
            html,
            static match => $"<span id=\"{match.Groups["id"].Value}\" class=\"searchHighlighting\">");
    }

    [GeneratedRegex(
        """
        <span\b[^>]*\bid\s*=\s*["'](?<id>oHit\d+)["'][^>]*>
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HitSpanRegex();
}
