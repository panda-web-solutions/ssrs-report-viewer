using SsrsReportViewer.Api.Reports;

namespace SsrsReportViewer.Api.Tests;

public sealed class ActionLinkRewriterTests
{
    [Fact]
    public void Rewrite_maps_report_manager_itempath_even_when_tabindex_precedes_href()
    {
        var html =
            """
            <a tabindex="2" href="http://thor/Reports/Pages/Report.aspx?ItemPath=%2fDatabase%20Server%2fDatabase%20Dictionary" style="text-decoration:none;"><img border="0" alt="go to this report" title="go to this report" src="http://thor/ReportServer?rs:Command=Get&amp;rc:StreamId=GoToReport"></a>
            """;

        var resourceRoot = "/api/sessions/sess-1/resources/";
        var withResources = ResourceUrlRewriter.Rewrite(html, resourceRoot);
        var rewritten = ActionLinkRewriter.Rewrite(withResources, "/api/sessions/sess-1/actions/");

        Assert.Contains("data-ssrs-action=\"drillthrough\"", rewritten, StringComparison.Ordinal);
        Assert.Contains(
            "data-ssrs-report-path=\"/Database Server/Database Dictionary\"",
            rewritten,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Report.aspx", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"{resourceRoot}GoToReport", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"\"", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("src=''", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Strip_does_not_empty_img_src_when_stream_id_was_not_rewritable()
    {
        // No StreamId — ResourceUrlRewriter leaves the URL; host strip must not yield src="".
        var html =
            """<img alt="icon" src="http://thor/ReportServer?/Sales/Revenue&amp;rs:Command=Get" />""";

        var rewritten = ActionLinkRewriter.Rewrite(html);

        Assert.Contains("ReportServer", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"\"", rewritten, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "http://thor/Reports/Pages/Report.aspx?ItemPath=%2fDatabase%20Server%2fDatabase%20Dictionary",
        "/Database Server/Database Dictionary")]
    [InlineData(
        "http://host/Reports/Pages/Report.aspx?ItemPath=/Example%20Reports/Data%20Sources",
        "/Example Reports/Data Sources")]
    public void TryParseReportManagerItemPath_extracts_catalog_path(string href, string expectedPath)
    {
        Assert.True(ActionLinkRewriter.TryParseReportManagerItemPath(href, out var path));
        Assert.Equal(expectedPath, path);
    }

    [Theory]
    [InlineData("&amp;")]
    [InlineData("&amp;amp;")]
    public void Rewrite_decodes_amp_entities_in_drillthrough_parameter_names(string amp)
    {
        // Live Report List HTML uses &amp; separators; XML/SOAP can leave them double-encoded
        // as &amp;amp; so a single HtmlDecode still yields amp;ReportName keys.
        var html =
            $"""
            <a href="/api/sessions/s1/actions/?/Example%20Reports/Execution%20Log{amp}ReportName=Database%20Dictionary{amp}rc:Parameters=Collapsed">Execution Log</a>
            """;

        var rewritten = ActionLinkRewriter.Rewrite(html, "/api/sessions/s1/actions/");

        Assert.Contains("data-ssrs-action=\"drillthrough\"", rewritten, StringComparison.Ordinal);
        Assert.Contains(
            "data-ssrs-report-path=\"/Example Reports/Execution Log\"",
            rewritten,
            StringComparison.Ordinal);
        Assert.Contains("&quot;name&quot;:&quot;ReportName&quot;", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("amp;ReportName", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("rc:Parameters", rewritten, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("&amp;")]
    [InlineData("&amp;amp;")]
    public void TryParseDrillthroughQuery_accepts_html_encoded_separators(string amp)
    {
        Assert.True(ActionLinkRewriter.TryParseDrillthroughQuery(
            $"/Example%20Reports/Subscriptions{amp}ReportName=Sales%20Detail{amp}rc:Parameters=Collapsed",
            out var path,
            out var parameters));

        Assert.Equal("/Example Reports/Subscriptions", path);
        var reportName = Assert.Single(parameters, p => p.Name == "ReportName");
        Assert.Equal(["Sales Detail"], reportName.Values);
        Assert.DoesNotContain(parameters, p => p.Name.Contains("amp;", StringComparison.Ordinal));
        Assert.DoesNotContain(parameters, p => p.Name.StartsWith("rc:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryParseDrillthroughQuery_maps_isnull_suffix_to_empty_parameter_values()
    {
        // SSRS URL access: StartDate:isnull=True means null StartDate — not a parameter named StartDate:isnull.
        Assert.True(ActionLinkRewriter.TryParseDrillthroughQuery(
            "/Example%20Reports/Execution%20Log&ReportName=Database%20Dictionary&StartDate:isnull=True&EndDate:isnull=True&LogStatus=%3CALL%3E&rc:Parameters=Collapsed",
            out var path,
            out var parameters));

        Assert.Equal("/Example Reports/Execution Log", path);
        Assert.Equal(["Database Dictionary"], Assert.Single(parameters, p => p.Name == "ReportName").Values);
        Assert.Empty(Assert.Single(parameters, p => p.Name == "StartDate").Values);
        Assert.Empty(Assert.Single(parameters, p => p.Name == "EndDate").Values);
        Assert.Equal(["<ALL>"], Assert.Single(parameters, p => p.Name == "LogStatus").Values);
        Assert.DoesNotContain(parameters, p => p.Name.Contains(":isnull", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rewrite_maps_reportserver_render_url_label_to_report_leaf_name()
    {
        // Execution Log cells often use the raw ReportServer URL as both href and visible text.
        var html =
            """
            <a href="http://THOR:80/ReportServer/ReportServer?/Example+Reports%2fExecution+Log&amp;rs:Command=Render">http://THOR:80/ReportServer/ReportServer?/Example+Reports%2fExecution+Log&amp;rs:Command=Render</a>
            """;

        var rewritten = ActionLinkRewriter.Rewrite(html);

        Assert.Contains("data-ssrs-action=\"drillthrough\"", rewritten, StringComparison.Ordinal);
        Assert.Contains(
            "data-ssrs-report-path=\"/Example Reports/Execution Log\"",
            rewritten,
            StringComparison.Ordinal);
        Assert.Contains(">Execution Log</a>", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("data-ssrs-action=\"blocked-reportserver\"", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("THOR:80", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_maps_replacement_root_prefixed_absolute_reportserver_urls()
    {
        // SSRS Report List emits {ReplacementRoot}http://host/ReportServer?/path&ReportName=…
        // After placeholder replace that becomes /api/sessions/…/actions/http://host/ReportServer?…
        var html =
            """
            <a tabindex="8" href="/api/sessions/s1/actions/http://thor/ReportServer?%2FExample%20Reports%2FExecution%20Log&amp;ReportName=Scheduled%20Jobs&amp;StartDate%3Aisnull=True&amp;LogStatus=%3CALL%3E&amp;rc%3AParameters=Collapsed" style="text-decoration:none;"><img alt="view report of the log" src="/api/sessions/s1/resources/x"></a>
            """;

        var rewritten = ActionLinkRewriter.Rewrite(html, "/api/sessions/s1/actions/");

        Assert.Contains("data-ssrs-action=\"drillthrough\"", rewritten, StringComparison.Ordinal);
        Assert.Contains(
            "data-ssrs-report-path=\"/Example Reports/Execution Log\"",
            rewritten,
            StringComparison.Ordinal);
        Assert.Contains("&quot;name&quot;:&quot;ReportName&quot;", rewritten, StringComparison.Ordinal);
        Assert.Contains("Scheduled Jobs", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("data-ssrs-parameters=\"[]\"", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/http://", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_keeps_report_manager_query_parameters_with_itempath()
    {
        // Report List "go to Execution Log" icons use Report.aspx?ItemPath=…&ReportName=…
        // ItemPath alone must not drop ReportName (that yields data-ssrs-parameters="[]").
        var html =
            """
            <a tabindex="1" href="http://thor/Reports/Pages/Report.aspx?ItemPath=%2fExample%20Reports%2fExecution%20Log&amp;ReportName=Scheduled%20Jobs&amp;StartDate:isnull=True&amp;rc:Parameters=Collapsed" style="text-decoration:none;"><img border="0" alt="go to this report" title="go to this report" src="http://thor/ReportServer?rs:Command=Get&amp;rc:StreamId=GoToReport"></a>
            """;

        var resourceRoot = "/api/sessions/sess-1/resources/";
        var withResources = ResourceUrlRewriter.Rewrite(html, resourceRoot);
        var rewritten = ActionLinkRewriter.Rewrite(withResources, "/api/sessions/sess-1/actions/");

        Assert.Contains(
            "data-ssrs-report-path=\"/Example Reports/Execution Log\"",
            rewritten,
            StringComparison.Ordinal);
        Assert.Contains("&quot;name&quot;:&quot;ReportName&quot;", rewritten, StringComparison.Ordinal);
        Assert.Contains("Scheduled Jobs", rewritten, StringComparison.Ordinal);
        Assert.Contains("&quot;name&quot;:&quot;StartDate&quot;", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("data-ssrs-parameters=\"[]\"", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("rc:Parameters", rewritten, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("&amp;")]
    [InlineData("&amp;amp;")]
    public void Rewrite_maps_sortitem_action_link_with_encoded_separators(string amp)
    {
        // Column-header Sort Action Links use rc:SortItem + rc:SortDirection; live HTML
        // uses &amp; separators and SOAP can leave them double-encoded as &amp;amp;.
        var html =
            $"""
            <a tabindex="3" href="/api/sessions/s1/actions/?rc:SortItem=Region{amp}rc:SortDirection=Descending">Sort Region</a>
            """;

        var rewritten = ActionLinkRewriter.Rewrite(html, "/api/sessions/s1/actions/");

        Assert.Contains("data-ssrs-action=\"sort\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-ssrs-sort-item=\"Region\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-ssrs-sort-direction=\"Descending\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-ssrs-sort-clear=\"false\"", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("rc:SortItem", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("amp;rc:SortDirection", rewritten, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("&amp;")]
    [InlineData("&amp;amp;")]
    public void Rewrite_maps_sortid_and_clear_on_reportserver_urls(string amp)
    {
        var html =
            $"""
            <a href="http://reportserver.example/ReportServer?/Sales/Revenue{amp}rc:SortId=ColA{amp}rc:SortDirection=Ascending{amp}rc:Clear=True">Clear sort</a>
            """;

        var rewritten = ActionLinkRewriter.Rewrite(html);

        Assert.Contains("data-ssrs-action=\"sort\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-ssrs-sort-item=\"ColA\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-ssrs-sort-direction=\"Ascending\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-ssrs-sort-clear=\"true\"", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("ReportServer", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("amp;rc:SortId", rewritten, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("&amp;")]
    [InlineData("&amp;amp;")]
    public void Rewrite_maps_html_document_rs_sortid_command_sort_links(string amp)
    {
        // HTMLFragment=false emits rs:Command=Sort with percent-encoded rs:SortId and built-in GIF icons.
        var html =
            $"""
            <a tabindex="1" href="/api/sessions/s1/actions/http://thor/ReportServer?%2FReports%2FList{amp}rs%3ACommand=Sort{amp}rs%3ASortId=29iT0{amp}rs%3ASortDirection=Ascending{amp}rs%3AClearSort=TRUE"><IMG ALT="Unsorted" BORDER="0" SRC="/api/sessions/s1/resources/2022.1.0.0unsorted.gif"/></a>
            """;

        var rewritten = ActionLinkRewriter.Rewrite(html, "/api/sessions/s1/actions/");

        Assert.Contains("data-ssrs-action=\"sort\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-ssrs-sort-item=\"29iT0\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-ssrs-sort-direction=\"Ascending\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-ssrs-sort-clear=\"true\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("ssrs-sort-icon", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("unsorted.gif", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rs:Command=Sort", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_preserves_find_highlight_markup_alongside_action_links()
    {
        const string highlight =
            FakeSsrsAdapter.FindHighlightOpenTag + "Log" + FakeSsrsAdapter.FindHighlightCloseTag;
        var html =
            $"""
            <div class="report">
              <TD>{highlight} column</TD>
              <a href="/api/sessions/s1/actions/?rc:SortItem=Region&amp;rc:SortDirection=Ascending">
                <IMG ALT="Unsorted" BORDER="0" SRC="/api/sessions/s1/resources/unsorted.gif"/>
              </a>
              <a href="https://contoso.example/help">{highlight} help</a>
            </div>
            """;

        var rewritten = ActionLinkRewriter.Rewrite(html, "/api/sessions/s1/actions/");

        Assert.Contains(highlight + " column", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-ssrs-action=\"sort\"", rewritten, StringComparison.Ordinal);
        Assert.Contains(highlight + " help", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-ssrs-action=\"external\"", rewritten, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Sorted Ascending", true, "M4.5 10.5h7L8 5z")]
    [InlineData("Sorted Descending", true, "M4.5 5.5h7L8 11z")]
    [InlineData("Unsorted", false, "M3.5 6.5h9L8 2.5z")]
    public void SortIconLabel_matches_native_ssrs_states(string alt, bool circled, string pathSnippet)
    {
        var label = $"""<IMG ALT="{alt}" BORDER="0" SRC="/resources/icon.gif"/>""";

        var icon = ActionLinkRewriter.SortIconLabel(label, "Ascending", clear: false);

        Assert.StartsWith("<img ", icon, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data:image/svg+xml,", icon, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(pathSnippet), icon, StringComparison.Ordinal);
        Assert.Contains($"title=\"{alt}\"", icon, StringComparison.Ordinal);
        Assert.Contains($"alt=\"{alt}\"", icon, StringComparison.Ordinal);
        if (circled)
        {
            Assert.Contains("ssrs-sort-icon-active", icon, StringComparison.Ordinal);
            Assert.Contains(Uri.EscapeDataString("<circle"), icon, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain(Uri.EscapeDataString("<circle"), icon, StringComparison.Ordinal);
        }
    }
}
