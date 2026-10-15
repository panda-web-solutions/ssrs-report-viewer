using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using SsrsReportViewer.Api.Ssrs;

namespace SsrsReportViewer.Api.Tests;

public sealed record FakeReportParameter(
    string Name,
    string DataType,
    bool Nullable,
    bool MultiValue,
    string? Prompt,
    bool Required,
    IReadOnlyList<string>? DefaultValues = null,
    IReadOnlyList<(string Label, string Value)>? ValidValues = null,
    IReadOnlyList<string>? Dependencies = null,
    IReadOnlyDictionary<string, IReadOnlyList<(string Label, string Value)>>? CascadingByDependencyValue = null,
    bool PromptUser = true);

public sealed record FakeStream(string StreamId, string MimeType, byte[] Content);

public sealed record FakeDocumentMapNode(
    string Id,
    string Label,
    IReadOnlyList<FakeDocumentMapNode>? Children = null);

/// <summary>
/// In-memory SSRS adapter for API tests. Catalog + Report Parameter + Execution behaviors.
/// </summary>
public sealed partial class FakeSsrsAdapter : ISsrsAdapter
{
    private readonly Dictionary<string, CatalogChildren> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SeededReport> _reports = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FakeExecution> _executions = new(StringComparer.Ordinal);

    /// <summary>
    /// Counts any Viewer Session / Execution mutations. Catalog ListChildren must leave this at 0.
    /// </summary>
    public int ViewerSessionMutations { get; private set; }

    public int ActiveExecutionCount => _executions.Count;

    /// <summary>Execution id from the most recent LoadReportAsync call.</summary>
    public string? LastLoadedExecutionId { get; private set; }

    /// <summary>Execution id from the most recent RenderAsync call.</summary>
    public string? LastRenderedExecutionId { get; private set; }

    /// <summary>DeviceInfo from the most recent non-HTML RenderAsync (Export) call.</summary>
    public DeviceInfo? LastExportDeviceInfo { get; private set; }

    /// <summary>DeviceInfo from the most recent HTML RenderAsync call.</summary>
    public DeviceInfo? LastHtmlDeviceInfo { get; private set; }

    /// <summary>
    /// Fake Find hit chrome matching ReportViewer <c>searchHighlighting</c> (stable for assertions).
    /// </summary>
    public const string FindHighlightOpenTag =
        "<span id=\"oHit0\" class=\"searchHighlighting\">";

    public const string FindHighlightCloseTag = "</span>";

    /// <summary>forRendering flag from the most recent GetParametersAsync call.</summary>
    public bool? LastGetParametersForRendering { get; private set; }

    /// <summary>Parameter values from the most recent SetParametersAsync call.</summary>
    public IReadOnlyList<ParameterValue>? LastSetParameters { get; private set; }

    /// <summary>Cascading Values from the most recent GetParametersAsync call.</summary>
    public IReadOnlyList<ParameterValue>? LastGetParametersValues { get; private set; }

    /// <summary>Optional delay applied inside RenderStreamAsync (busy serialization tests).</summary>
    public TimeSpan StreamDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Optional delay applied inside ToggleItemAsync (busy serialization tests).</summary>
    public TimeSpan ToggleDelay { get; set; } = TimeSpan.Zero;

    public void Reset()
    {
        _catalog.Clear();
        _reports.Clear();
        _executions.Clear();
        ViewerSessionMutations = 0;
        StreamDelay = TimeSpan.Zero;
        ToggleDelay = TimeSpan.Zero;
        LastLoadedExecutionId = null;
        LastRenderedExecutionId = null;
        LastExportDeviceInfo = null;
        LastHtmlDeviceInfo = null;
        LastGetParametersForRendering = null;
        LastSetParameters = null;
        LastGetParametersValues = null;
    }

    public void ResetCatalog() => Reset();

    /// <summary>Simulates SSRS dropping an Execution (idle churn).</summary>
    public bool DropExecution(string executionId) => _executions.TryRemove(executionId, out _);

    public void SeedCatalog(
        string folder,
        IReadOnlyList<(string Path, string Name)> folders,
        IReadOnlyList<(string Path, string Name)> reports)
    {
        var key = NormalizePath(folder);
        _catalog[key] = new CatalogChildren(
            folders.Select(f => new CatalogItem(f.Name, NormalizePath(f.Path), CatalogItemType.Folder)).ToList(),
            reports.Select(r => new CatalogItem(r.Name, NormalizePath(r.Path), CatalogItemType.Report)).ToList());
    }

    public void SeedReport(
        string reportPath,
        IReadOnlyList<FakeReportParameter> parameters,
        string html = "<div class=\"report\">ok</div>",
        IReadOnlyList<FakeStream>? streams = null,
        IReadOnlyList<FakeDocumentMapNode>? documentMap = null,
        IReadOnlyDictionary<string, string>? documentMapHtml = null,
        IReadOnlyDictionary<string, string>? bookmarkHtml = null,
        IReadOnlyList<string>? pageHtml = null,
        int pageCount = 1)
    {
        var path = NormalizePath(reportPath);
        var pages = pageHtml is { Count: > 0 }
            ? pageHtml.ToList()
            : Enumerable.Repeat(html, Math.Max(1, pageCount)).ToList();
        _reports[path] = new SeededReport(
            path,
            parameters.ToList(),
            pages,
            (streams ?? []).ToDictionary(s => s.StreamId, s => s, StringComparer.Ordinal),
            documentMap?.ToList(),
            documentMapHtml is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(documentMapHtml, StringComparer.Ordinal),
            bookmarkHtml is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(bookmarkHtml, StringComparer.Ordinal));
    }

    public Task<IReadOnlyList<CatalogItem>> ListChildrenAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = NormalizePath(path);
        if (!_catalog.TryGetValue(key, out var children))
        {
            throw new CatalogPathNotFoundException(key);
        }

        IReadOnlyList<CatalogItem> items =
            [.. children.Folders, .. children.Reports];
        return Task.FromResult(items);
    }

    public Task<IReadOnlyList<ReportParameterDefinition>> GetParametersAsync(
        string reportPath,
        bool forRendering,
        IReadOnlyList<ParameterValue>? valuesForCascading = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastGetParametersForRendering = forRendering;
        LastGetParametersValues = valuesForCascading;
        var report = RequireReport(reportPath);

        IReadOnlyList<ReportParameterDefinition> defs = report.Parameters
            .Select(p =>
            {
                var valid = ResolveValidValues(p, valuesForCascading);
                return new ReportParameterDefinition(
                    p.Name,
                    p.DataType,
                    p.Nullable,
                    p.MultiValue,
                    p.Required,
                    p.Prompt,
                    p.DefaultValues ?? [],
                    valid.Select(v => new ReportParameterValidValue(v.Label, v.Value)).ToArray(),
                    p.Dependencies ?? [],
                    p.PromptUser);
            })
            .ToArray();

        return Task.FromResult(defs);
    }

    private static IReadOnlyList<(string Label, string Value)> ResolveValidValues(
        FakeReportParameter parameter,
        IReadOnlyList<ParameterValue>? valuesForCascading)
    {
        if (parameter.CascadingByDependencyValue is null ||
            valuesForCascading is null ||
            valuesForCascading.Count == 0 ||
            parameter.Dependencies is null ||
            parameter.Dependencies.Count == 0)
        {
            return parameter.ValidValues ?? [];
        }

        var dependencyName = parameter.Dependencies[0];
        var dependency = valuesForCascading.FirstOrDefault(v =>
            string.Equals(v.Name, dependencyName, StringComparison.OrdinalIgnoreCase));
        var key = dependency?.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (key is null)
        {
            return parameter.ValidValues ?? [];
        }

        return parameter.CascadingByDependencyValue.TryGetValue(key, out var cascaded)
            ? cascaded
            : [];
    }

    public Task<ExecutionHandle> LoadReportAsync(string reportPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var report = RequireReport(reportPath);
        ViewerSessionMutations++;

        var id = Guid.NewGuid().ToString("N");
        LastLoadedExecutionId = id;
        var parameters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in report.Parameters)
        {
            if (parameter.DefaultValues is { Count: > 0 })
            {
                parameters[parameter.Name] = parameter.DefaultValues;
            }
        }

        _executions[id] = new FakeExecution(
            id,
            report,
            parameters,
            new HashSet<string>(StringComparer.Ordinal),
            ActiveSort: null);
        return Task.FromResult(new ExecutionHandle(id));
    }

    public Task SetParametersAsync(
        ExecutionHandle execution,
        IReadOnlyList<ParameterValue> values,
        string culture,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exec = RequireExecution(execution);
        ViewerSessionMutations++;
        _ = culture;
        LastSetParameters = values;

        foreach (var value in values)
        {
            // Empty Values means SSRS NULL: still apply so report defaults are cleared.
            // Mirrors SetExecutionParameters ParameterValue with Name and no Value child.
            exec.Parameters[value.Name] = value.Values;
        }

        return Task.CompletedTask;
    }

    public bool TryGetExecutionParameter(
        string executionId,
        string name,
        out IReadOnlyList<string>? values)
    {
        values = null;
        if (!_executions.TryGetValue(executionId, out var exec))
        {
            return false;
        }

        if (!exec.Parameters.TryGetValue(name, out var stored))
        {
            return false;
        }

        values = stored;
        return true;
    }

    public Task<RenderResult> RenderAsync(
        ExecutionHandle execution,
        string format,
        DeviceInfo deviceInfo,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exec = RequireExecution(execution);
        ViewerSessionMutations++;
        LastRenderedExecutionId = exec.Id;

        if (!string.Equals(format, "HTML4.0", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(format, "HTML5", StringComparison.OrdinalIgnoreCase))
        {
            LastExportDeviceInfo = deviceInfo;
            return Task.FromResult(RenderExport(exec, format, deviceInfo));
        }

        // Mirror SSRS ReportParameterValueNotSetException when a non-nullable parameter has no value.
        foreach (var parameter in exec.Report.Parameters)
        {
            if (parameter.Nullable)
            {
                continue;
            }

            if (!exec.Parameters.TryGetValue(parameter.Name, out var values) ||
                values.Count == 0 ||
                values.All(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException(
                    $"SSRS SOAP Render fault: This report requires a default or user-defined value for the report parameter '{parameter.Name}'.");
            }
        }

        var pageCount = exec.Report.PageHtml.Count;
        var page = deviceInfo.Section ?? 1;
        if (page < 1)
        {
            page = 1;
        }

        if (page > pageCount)
        {
            page = pageCount;
        }

        var html = exec.ActiveHtml ?? exec.Report.PageHtml[page - 1];
        foreach (var (name, values) in exec.Parameters)
        {
            html = html.Replace($"{{{{{name}}}}}", string.Join(",", values), StringComparison.OrdinalIgnoreCase);
        }

        var resourceRoot = deviceInfo.ResourceStreamRoot ?? "{ResourceStreamRoot}";
        var replacementRoot = deviceInfo.ReplacementRoot ?? "{ReplacementRoot}";
        html = html.Replace("{ResourceStreamRoot}", resourceRoot, StringComparison.Ordinal);
        html = html.Replace("{ReplacementRoot}", replacementRoot, StringComparison.Ordinal);

        foreach (var streamId in exec.Report.Streams.Keys)
        {
            html = html.Replace(
                $"{{resource:{streamId}}}",
                resourceRoot + streamId,
                StringComparison.Ordinal);
        }

        foreach (Match match in ToggleStatePlaceholderRegex().Matches(html))
        {
            var toggleId = match.Groups[1].Value;
            var state = exec.ToggledItems.Contains(toggleId) ? "expanded" : "collapsed";
            html = html.Replace(match.Value, state, StringComparison.Ordinal);
        }

        foreach (Match match in SortStatePlaceholderRegex().Matches(html))
        {
            var sortItem = match.Groups[1].Value;
            var state = exec.ActiveSort is not null
                && string.Equals(exec.ActiveSort.SortItem, sortItem, StringComparison.Ordinal)
                ? exec.ActiveSort.Direction.ToString()
                : "unsorted";
            html = html.Replace(match.Value, state, StringComparison.Ordinal);
        }

        // Ensure View Report responses expose the API resource root when streams exist.
        if (exec.Report.Streams.Count > 0 &&
            !html.Contains(resourceRoot, StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(deviceInfo.ResourceStreamRoot))
        {
            html += $"<!-- resources:{deviceInfo.ResourceStreamRoot} -->";
        }

        LastHtmlDeviceInfo = deviceInfo;
        if (!string.IsNullOrWhiteSpace(deviceInfo.FindString))
        {
            html = WrapFirstFindMatch(html, deviceInfo.FindString);
        }

        // Expose DeviceInfo Zoom / Section and Execution id so HTTP tests can assert SSRS re-render.
        html =
            $"<div data-exec=\"{exec.Id}\" data-zoom=\"{deviceInfo.ZoomPercent}\" data-page=\"{page}\">{html}</div>";

        var bytes = Encoding.UTF8.GetBytes(html);
        var streamIds = exec.Report.Streams.Keys.ToArray();
        var documentMap = ToDocumentMap(exec.Report.DocumentMap);
        return Task.FromResult(new RenderResult(
            bytes,
            "text/html",
            streamIds,
            PageCount: pageCount,
            CurrentPage: page,
            DocumentMap: documentMap));
    }

    public Task<int> FindStringAsync(
        ExecutionHandle execution,
        int startPage,
        int endPage,
        string findValue,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exec = RequireExecution(execution);
        ViewerSessionMutations++;
        // Find repositions by page; drop Document Map / Bookmark HTML overrides.
        exec.ActiveHtml = null;

        if (string.IsNullOrEmpty(findValue))
        {
            return Task.FromResult(0);
        }

        var pageCount = exec.Report.PageHtml.Count;
        if (pageCount == 0)
        {
            return Task.FromResult(0);
        }

        // Mirror ReportExecution2005 FindString wrap semantics.
        var start = startPage <= 0 ? 1 : startPage;
        var stop = endPage <= 0 ? start : endPage;
        if (start > pageCount)
        {
            start = 1;
        }

        if (stop > pageCount)
        {
            stop = pageCount;
        }

        var page = start;
        while (true)
        {
            if (exec.Report.PageHtml[page - 1].Contains(findValue, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(page);
            }

            if (page == stop)
            {
                break;
            }

            page++;
            if (page > pageCount)
            {
                page = 1;
            }
        }

        return Task.FromResult(0);
    }

    private static RenderResult RenderExport(FakeExecution exec, string format, DeviceInfo deviceInfo)
    {
        var normalized = format.Trim().ToUpperInvariant();
        var (prefix, mime) = normalized switch
        {
            "WORDOPENXML" => (
                "FAKE-DOCX:",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            "EXCELOPENXML" => (
                "FAKE-XLSX:",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            "PPTX" => (
                "FAKE-PPTX:",
                "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
            "PDF" when deviceInfo.AccessiblePdf => ("FAKE-ACCESSIBLE-PDF:", "application/pdf"),
            "PDF" => ("FAKE-PDF:", "application/pdf"),
            "IMAGE" when string.Equals(deviceInfo.OutputFormat, "TIFF", StringComparison.OrdinalIgnoreCase) =>
                ("FAKE-TIFF:", "image/tiff"),
            "MHTML" => ("FAKE-MHTML:", "multipart/related"),
            "CSV" => ("FAKE-CSV:", "text/csv"),
            "XML" => ("FAKE-XML:", "application/xml"),
            _ => throw new InvalidOperationException($"Unsupported Export format '{format}'."),
        };

        var bytes = Encoding.UTF8.GetBytes(prefix + exec.Id);
        return new RenderResult(bytes, mime, StreamIds: [], PageCount: 1, CurrentPage: 1);
    }

    public async Task ToggleItemAsync(
        ExecutionHandle execution,
        string toggleId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exec = RequireExecution(execution);
        ViewerSessionMutations++;

        if (string.IsNullOrWhiteSpace(toggleId))
        {
            throw new ArgumentException("toggleId is required.", nameof(toggleId));
        }

        if (ToggleDelay > TimeSpan.Zero)
        {
            await Task.Delay(ToggleDelay, cancellationToken);
        }

        if (!exec.ToggledItems.Add(toggleId))
        {
            exec.ToggledItems.Remove(toggleId);
        }
    }

    public Task<SortResult> SortAsync(
        ExecutionHandle execution,
        string sortItem,
        SortDirection direction,
        bool clear,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exec = RequireExecution(execution);
        ViewerSessionMutations++;

        if (string.IsNullOrWhiteSpace(sortItem))
        {
            throw new ArgumentException("sortItem is required.", nameof(sortItem));
        }

        // Clear = clear other sorts (implicit here with one ActiveSort); Direction=None removes sort.
        if (direction == SortDirection.None)
        {
            exec.ActiveSort = null;
        }
        else
        {
            exec.ActiveSort = new ActiveSort(sortItem.Trim(), direction);
        }

        return Task.FromResult(new SortResult(CurrentPage: 1, PageCount: 1, ReportItem: sortItem.Trim()));
    }

    public async Task<StreamResult> RenderStreamAsync(
        ExecutionHandle execution,
        string format,
        string streamId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exec = RequireExecution(execution);
        ViewerSessionMutations++;

        if (StreamDelay > TimeSpan.Zero)
        {
            await Task.Delay(StreamDelay, cancellationToken);
        }

        if (!exec.Report.Streams.TryGetValue(streamId, out var stream))
        {
            throw new InvalidOperationException($"Unknown stream '{streamId}'.");
        }

        return new StreamResult(stream.Content, stream.MimeType);
    }

    public Task NavigateDocumentMapAsync(
        ExecutionHandle execution,
        string documentMapId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exec = RequireExecution(execution);
        ViewerSessionMutations++;

        if (!exec.Report.DocumentMapHtml.TryGetValue(documentMapId, out var html))
        {
            html = $"<div class=\"report\" data-section=\"{documentMapId}\">Document Map {documentMapId}</div>";
        }

        exec.ActiveHtml = html;
        return Task.CompletedTask;
    }

    public Task NavigateBookmarkAsync(
        ExecutionHandle execution,
        string bookmarkId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exec = RequireExecution(execution);
        ViewerSessionMutations++;

        if (!exec.Report.BookmarkHtml.TryGetValue(bookmarkId, out var html))
        {
            throw new InvalidOperationException($"Unknown Bookmark '{bookmarkId}'.");
        }

        exec.ActiveHtml = html;
        return Task.CompletedTask;
    }

    private static IReadOnlyList<DocumentMapNode>? ToDocumentMap(IReadOnlyList<FakeDocumentMapNode>? nodes)
    {
        if (nodes is null || nodes.Count == 0)
        {
            return null;
        }

        return nodes.Select(MapNode).ToArray();
    }

    private static DocumentMapNode MapNode(FakeDocumentMapNode node) =>
        new(
            node.Id,
            node.Label,
            (node.Children ?? []).Select(MapNode).ToArray());

    private SeededReport RequireReport(string reportPath)
    {
        var path = NormalizePath(reportPath);
        if (!_reports.TryGetValue(path, out var report))
        {
            throw new ReportNotFoundException(path);
        }

        return report;
    }

    private FakeExecution RequireExecution(ExecutionHandle execution)
    {
        if (!_executions.TryGetValue(execution.Id, out var exec))
        {
            throw new ExecutionNotFoundException(execution.Id);
        }

        return exec;
    }

    private static string WrapFirstFindMatch(string html, string findString)
    {
        var index = html.IndexOf(findString, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return html;
        }

        var matched = html.Substring(index, findString.Length);
        return html[..index]
            + FindHighlightOpenTag
            + matched
            + FindHighlightCloseTag
            + html[(index + findString.Length)..];
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "/";
        }

        return "/" + path.Trim().Trim('/');
    }

    private sealed record CatalogChildren(
        IReadOnlyList<CatalogItem> Folders,
        IReadOnlyList<CatalogItem> Reports);

    private sealed record SeededReport(
        string Path,
        IReadOnlyList<FakeReportParameter> Parameters,
        IReadOnlyList<string> PageHtml,
        Dictionary<string, FakeStream> Streams,
        IReadOnlyList<FakeDocumentMapNode>? DocumentMap,
        Dictionary<string, string> DocumentMapHtml,
        Dictionary<string, string> BookmarkHtml);

    private sealed record ActiveSort(string SortItem, SortDirection Direction);

    private sealed class FakeExecution(
        string Id,
        SeededReport Report,
        Dictionary<string, IReadOnlyList<string>> Parameters,
        HashSet<string> ToggledItems,
        ActiveSort? ActiveSort)
    {
        public string Id { get; } = Id;
        public SeededReport Report { get; } = Report;
        public Dictionary<string, IReadOnlyList<string>> Parameters { get; } = Parameters;
        public HashSet<string> ToggledItems { get; } = ToggledItems;
        public ActiveSort? ActiveSort { get; set; } = ActiveSort;
        public string? ActiveHtml { get; set; }
    }

    [GeneratedRegex(@"\{toggle:([^}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex ToggleStatePlaceholderRegex();

    [GeneratedRegex(@"\{sort:([^}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex SortStatePlaceholderRegex();
}
