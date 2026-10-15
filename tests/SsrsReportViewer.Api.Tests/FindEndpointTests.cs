using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SsrsReportViewer.Api.Tests;

public sealed class FindEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public FindEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.Fake.Reset();
        _factory.ResetSessions();
    }

    [Fact]
    public async Task Post_find_returns_matching_page_html_for_viewer_session()
    {
        _factory.Fake.SeedReport(
            "/Sales/Revenue",
            parameters: [],
            pageHtml:
            [
                "<div class=\"report\">Page one alpha</div>",
                "<div class=\"report\">Page two beta needle here</div>",
                "<div class=\"report\">Page three gamma</div>",
            ]);

        var sessionId = await RenderSessionAsync("/Sales/Revenue");

        var response = await _client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/find",
            new { findValue = "needle" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<FindResponse>();
        Assert.NotNull(body);
        Assert.True(body.Found);
        Assert.Equal(2, body.PageInfo.CurrentPage);
        Assert.Equal(3, body.PageInfo.PageCount);
        Assert.Contains("needle", body.Html, StringComparison.Ordinal);
        Assert.Contains("Page two", body.Html, StringComparison.Ordinal);
        Assert.Equal("needle", _factory.Fake.LastHtmlDeviceInfo?.FindString);
        Assert.Contains(
            FakeSsrsAdapter.FindHighlightOpenTag + "needle" + FakeSsrsAdapter.FindHighlightCloseTag,
            body.Html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_find_next_advances_to_subsequent_match()
    {
        _factory.Fake.SeedReport(
            "/Sales/Revenue",
            parameters: [],
            pageHtml:
            [
                "<div class=\"report\">first hit: needle</div>",
                "<div class=\"report\">no match</div>",
                "<div class=\"report\">second hit: needle again</div>",
            ]);

        var sessionId = await RenderSessionAsync("/Sales/Revenue");

        var first = await _client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/find",
            new { findValue = "needle" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<FindResponse>();
        Assert.NotNull(firstBody);
        Assert.True(firstBody.Found);
        Assert.Equal(1, firstBody.PageInfo.CurrentPage);

        var next = await _client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/find/next",
            new { findValue = "needle" });
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);

        var nextBody = await next.Content.ReadFromJsonAsync<FindResponse>();
        Assert.NotNull(nextBody);
        Assert.True(nextBody.Found);
        Assert.Equal(3, nextBody.PageInfo.CurrentPage);
        Assert.Contains("second hit", nextBody.Html, StringComparison.Ordinal);
        Assert.Equal("needle", _factory.Fake.LastHtmlDeviceInfo?.FindString);
        Assert.Contains(
            FakeSsrsAdapter.FindHighlightOpenTag + "needle" + FakeSsrsAdapter.FindHighlightCloseTag,
            nextBody.Html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_find_next_wraps_to_first_match_with_highlights()
    {
        _factory.Fake.SeedReport(
            "/Sales/Revenue",
            parameters: [],
            pageHtml:
            [
                "<div class=\"report\">first hit: needle</div>",
                "<div class=\"report\">no match</div>",
                "<div class=\"report\">last hit: needle</div>",
            ]);

        var sessionId = await RenderSessionAsync("/Sales/Revenue");

        var last = await _client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/find",
            new { findValue = "needle" });
        Assert.Equal(HttpStatusCode.OK, last.StatusCode);
        // Jump to last match page via find next once.
        var toLast = await _client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/find/next",
            new { findValue = "needle" });
        Assert.Equal(HttpStatusCode.OK, toLast.StatusCode);
        var onLast = await toLast.Content.ReadFromJsonAsync<FindResponse>();
        Assert.NotNull(onLast);
        Assert.Equal(3, onLast.PageInfo.CurrentPage);

        var wrap = await _client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/find/next",
            new { findValue = "needle" });
        Assert.Equal(HttpStatusCode.OK, wrap.StatusCode);
        var wrapBody = await wrap.Content.ReadFromJsonAsync<FindResponse>();
        Assert.NotNull(wrapBody);
        Assert.True(wrapBody.Found);
        Assert.Equal(1, wrapBody.PageInfo.CurrentPage);
        Assert.Equal("needle", _factory.Fake.LastHtmlDeviceInfo?.FindString);
        Assert.Contains(
            FakeSsrsAdapter.FindHighlightOpenTag + "needle" + FakeSsrsAdapter.FindHighlightCloseTag,
            wrapBody.Html,
            StringComparison.Ordinal);
        Assert.Contains("first hit", wrapBody.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_find_returns_found_false_when_string_missing()
    {
        _factory.Fake.SeedReport(
            "/Sales/Revenue",
            parameters: [],
            pageHtml:
            [
                "<div class=\"report\">alpha</div>",
                "<div class=\"report\">beta</div>",
            ]);

        var sessionId = await RenderSessionAsync("/Sales/Revenue");

        var response = await _client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/find",
            new { findValue = "missing-term" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<FindResponse>();
        Assert.NotNull(body);
        Assert.False(body.Found);
        Assert.Equal(1, body.PageInfo.CurrentPage);
        Assert.Contains("alpha", body.Html, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(_factory.Fake.LastHtmlDeviceInfo?.FindString));
        Assert.DoesNotContain(
            FakeSsrsAdapter.FindHighlightOpenTag,
            body.Html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_find_returns_problem_details_for_unknown_session()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/sessions/does-not-exist/find",
            new { findValue = "x" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(
            problem.TryGetProperty("title", out _) || problem.TryGetProperty("detail", out _),
            "Expected ProblemDetails-shaped body");
    }

    [Fact]
    public async Task Non_find_rerenders_omit_FindString_after_successful_find()
    {
        _factory.Fake.SeedReport(
            "/Sales/Revenue",
            parameters: [],
            pageHtml:
            [
                "<div class=\"report\">Page one needle here</div>",
                "<div class=\"report\">Page two also needle</div>",
            ]);

        var sessionId = await RenderSessionAsync("/Sales/Revenue");

        var find = await _client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/find",
            new { findValue = "needle" });
        Assert.Equal(HttpStatusCode.OK, find.StatusCode);
        var findBody = await find.Content.ReadFromJsonAsync<FindResponse>();
        Assert.NotNull(findBody);
        Assert.True(findBody.Found);
        Assert.Equal("needle", _factory.Fake.LastHtmlDeviceInfo?.FindString);
        Assert.Contains(
            FakeSsrsAdapter.FindHighlightOpenTag,
            findBody.Html,
            StringComparison.Ordinal);

        var page = await _client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/page",
            new { action = "goto", page = 2 });
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var pageBody = await page.Content.ReadFromJsonAsync<FindResponse>();
        Assert.NotNull(pageBody);
        Assert.True(string.IsNullOrEmpty(_factory.Fake.LastHtmlDeviceInfo?.FindString));
        Assert.DoesNotContain(
            FakeSsrsAdapter.FindHighlightOpenTag,
            pageBody.Html,
            StringComparison.Ordinal);

        var zoom = await _client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/zoom",
            new { zoomPercent = 150 });
        Assert.Equal(HttpStatusCode.OK, zoom.StatusCode);
        var zoomBody = await zoom.Content.ReadFromJsonAsync<FindResponse>();
        Assert.NotNull(zoomBody);
        Assert.True(string.IsNullOrEmpty(_factory.Fake.LastHtmlDeviceInfo?.FindString));
        Assert.DoesNotContain(
            FakeSsrsAdapter.FindHighlightOpenTag,
            zoomBody.Html,
            StringComparison.Ordinal);

        var refresh = await _client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/refresh",
            new { });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var refreshBody = await refresh.Content.ReadFromJsonAsync<FindResponse>();
        Assert.NotNull(refreshBody);
        Assert.True(string.IsNullOrEmpty(_factory.Fake.LastHtmlDeviceInfo?.FindString));
        Assert.DoesNotContain(
            FakeSsrsAdapter.FindHighlightOpenTag,
            refreshBody.Html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_sort_after_find_omits_FindString_device_info()
    {
        _factory.Fake.SeedReport(
            "/Sales/Revenue",
            parameters: [],
            html: """
                  <div class="report">
                    <a href="{ReplacementRoot}?rc:SortItem=Col1&amp;rc:SortDirection=Ascending">Sort Col1</a>
                    <div class="rows">needle row</div>
                  </div>
                  """);

        var sessionId = await RenderSessionAsync("/Sales/Revenue");

        var find = await _client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/find",
            new { findValue = "needle" });
        Assert.Equal(HttpStatusCode.OK, find.StatusCode);
        Assert.Equal("needle", _factory.Fake.LastHtmlDeviceInfo?.FindString);

        var sort = await _client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/sort",
            new { sortItem = "Col1", direction = "Ascending", clear = false });
        Assert.Equal(HttpStatusCode.OK, sort.StatusCode);
        var sorted = await sort.Content.ReadFromJsonAsync<FindResponse>();
        Assert.NotNull(sorted);
        Assert.True(string.IsNullOrEmpty(_factory.Fake.LastHtmlDeviceInfo?.FindString));
        Assert.DoesNotContain(
            FakeSsrsAdapter.FindHighlightOpenTag,
            sorted.Html,
            StringComparison.Ordinal);
    }

    private async Task<string> RenderSessionAsync(string reportPath)
    {
        var render = await _client.PostAsJsonAsync(
            "/api/reports/render",
            new { reportPath, parameters = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, render.StatusCode);
        var body = await render.Content.ReadFromJsonAsync<RenderResponse>();
        Assert.NotNull(body);
        return body.SessionId;
    }

    private sealed record RenderResponse(string SessionId, string Html);

    private sealed record FindResponse(string Html, bool Found, PageInfoResponse PageInfo);

    private sealed record PageInfoResponse(int CurrentPage, int PageCount);
}
