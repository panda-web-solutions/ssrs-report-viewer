namespace SsrsReportViewer.Api.Tests;

public sealed class FindHighlightNormalizerTests
{
    [Fact]
    public void Normalize_maps_ssrs_system_highlight_span_to_searchHighlighting()
    {
        const string html =
            """<TD><span ID="oHit0" style="COLOR:highlighttext;BACKGROUND-COLOR:highlight;">Log</span></TD>""";

        var normalized = SsrsReportViewer.Api.Reports.FindHighlightNormalizer.Normalize(html);

        Assert.Contains(
            """<span id="oHit0" class="searchHighlighting">Log</span>""",
            normalized,
            StringComparison.Ordinal);
        Assert.DoesNotContain("BACKGROUND-COLOR:highlight", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("highlighttext", normalized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_leaves_non_hit_spans_alone()
    {
        const string html = """<span class="a34" style="COLOR:Black">Report Name</span>""";

        var normalized = SsrsReportViewer.Api.Reports.FindHighlightNormalizer.Normalize(html);

        Assert.Equal(html, normalized);
    }
}
