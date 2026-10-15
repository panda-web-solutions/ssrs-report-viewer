using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SsrsReportViewer.Api.Sessions;
using SsrsReportViewer.Api.Ssrs;

namespace SsrsReportViewer.Api.Reports;

public sealed partial class ReportViewerService
{
    private readonly ISsrsAdapter _ssrs;
    private readonly IViewerSessionStore _sessions;
    private readonly SsrsOptions _options;

    public ReportViewerService(
        ISsrsAdapter ssrs,
        IViewerSessionStore sessions,
        IOptions<SsrsOptions> options)
    {
        _ssrs = ssrs;
        _sessions = sessions;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<ReportParameterDefinition>> GetParametersAsync(
        string reportPath,
        bool forRendering,
        IReadOnlyList<ParameterValue>? valuesForCascading,
        CancellationToken cancellationToken)
    {
        if (valuesForCascading is null)
        {
            return await _ssrs.GetParametersAsync(
                reportPath,
                forRendering,
                valuesForCascading: null,
                cancellationToken);
        }

        // Cascading Values from the client omit non-prompted params; supply their defaults
        // so GetItemParameters still resolves dependents (US8).
        var metadata = await _ssrs.GetParametersAsync(
            reportPath,
            forRendering: false,
            valuesForCascading: null,
            cancellationToken);
        var enriched = MergeNonPromptedDefaultsForCascading(metadata, valuesForCascading);
        enriched = FormatDateTimeParameterValues(metadata, enriched, _options.DefaultCulture);
        return await _ssrs.GetParametersAsync(
            reportPath,
            forRendering,
            enriched,
            cancellationToken);
    }

    public async Task<ViewReportResult> ViewReportAsync(
        string reportPath,
        IReadOnlyList<ParameterValue> parameters,
        CancellationToken cancellationToken)
    {
        // Full definitions (including non-prompted) for required checks and default resolution.
        // ForRendering=false avoids data-source parameter queries before LoadReport.
        var definitions = await GetParametersAsync(
            reportPath,
            forRendering: false,
            valuesForCascading: null,
            cancellationToken);
        var missing = definitions
            .Where(d => d.ShownOnParameterForm && d.Required)
            .Where(d =>
            {
                var submitted = parameters.FirstOrDefault(p =>
                    string.Equals(p.Name, d.Name, StringComparison.OrdinalIgnoreCase));
                return submitted is null || submitted.Values.Count == 0 ||
                       submitted.Values.All(string.IsNullOrWhiteSpace);
            })
            .Select(d => d.Name)
            .ToArray();

        if (missing.Length > 0)
        {
            throw new RequiredParameterMissingException(missing);
        }

        var resolved = PrepareParametersForSsrs(definitions, parameters);
        var execution = await _ssrs.LoadReportAsync(reportPath, cancellationToken);
        await _ssrs.SetParametersAsync(execution, resolved, _options.DefaultCulture, cancellationToken);

        // Session / client payload: form-visible only. SetParameters already got full resolved.
        var session = _sessions.Create(
            reportPath,
            execution,
            FilterPromptedParameters(definitions, resolved));
        return await RenderSessionAsync(session, cancellationToken);
    }

    public async Task<ViewReportResult> SetZoomAsync(
        string sessionId,
        int zoomPercent,
        ViewerSessionGuard guard,
        CancellationToken cancellationToken)
    {
        if (zoomPercent is < 10 or > 500)
        {
            throw new InvalidViewerInteractionException($"Zoom percent '{zoomPercent}' is out of range (10-500).");
        }

        return await guard.ExecuteAsync(
            sessionId,
            async (session, ct) =>
            {
                session.ZoomPercent = zoomPercent;
                return await RenderSessionAsync(session, ct);
            },
            cancellationToken);
    }

    public async Task<ViewReportResult> SetPageAsync(
        string sessionId,
        string action,
        int? page,
        ViewerSessionGuard guard,
        CancellationToken cancellationToken)
    {
        return await guard.ExecuteAsync(
            sessionId,
            async (session, ct) =>
            {
                var target = ResolveTargetPage(session, action, page);
                if (target < 1 || target > Math.Max(1, session.PageCount))
                {
                    throw new InvalidViewerInteractionException(
                        $"Report Page '{target}' is outside 1–{Math.Max(1, session.PageCount)}.");
                }

                session.CurrentPage = target;
                return await RenderSessionAsync(session, ct);
            },
            cancellationToken);
    }

    public async Task<ViewReportResult> RefreshAsync(
        string sessionId,
        ViewerSessionGuard guard,
        CancellationToken cancellationToken)
    {
        return await guard.ExecuteAsync(
            sessionId,
            async (session, ct) =>
            {
                var definitions = await GetParametersAsync(
                    session.ReportPath,
                    forRendering: false,
                    valuesForCascading: null,
                    ct);
                var resolved = PrepareParametersForSsrs(definitions, session.Parameters);
                var execution = await _ssrs.LoadReportAsync(session.ReportPath, ct);
                await _ssrs.SetParametersAsync(
                    execution,
                    resolved,
                    _options.DefaultCulture,
                    ct);
                session.ReplaceExecution(execution);
                session.CurrentPage = 1;
                return await RenderSessionAsync(session, ct);
            },
            cancellationToken);
    }

    public Task<ViewReportResult> ToggleAsync(
        string sessionId,
        string toggleId,
        ViewerSessionGuard guard,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(toggleId))
        {
            throw new ArgumentException("toggleId is required.", nameof(toggleId));
        }

        return guard.ExecuteAsync(
            sessionId,
            async (session, ct) =>
            {
                await _ssrs.ToggleItemAsync(session.Execution, toggleId, ct);
                return await RenderSessionAsync(session, ct);
            },
            cancellationToken);
    }

    public async Task<StreamResult> GetResourceAsync(
        string sessionId,
        string streamId,
        ViewerSessionGuard guard,
        CancellationToken cancellationToken)
    {
        // Wait for the per-session gate: browsers fetch many <img> resources in parallel, and
        // failing those with 409 leaves permanently broken icons (SSRS onerror=this.errored).
        return await guard.ExecuteAsync(
            sessionId,
            (session, ct) => _ssrs.RenderStreamAsync(session.Execution, "HTML4.0", streamId, ct),
            cancellationToken,
            waitForGate: true);
    }


    public async Task<ViewReportResult> DrillthroughAsync(
        string sessionId,
        string reportPath,
        IReadOnlyList<ParameterValue> parameters,
        ViewerSessionGuard guard,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            throw new ArgumentException("reportPath is required.", nameof(reportPath));
        }

        return await guard.ExecuteAsync(
            sessionId,
            async (session, ct) =>
            {
                // Full definitions (including non-prompted) — same as View Report.
                // Catalog "go to this report" ItemPath links often omit required Report Parameters;
                // still open the child Execution so the parameter form can collect them (native SSRS).
                var definitions = await GetParametersAsync(
                    reportPath,
                    forRendering: false,
                    valuesForCascading: null,
                    ct);
                var resolved = PrepareParametersForSsrs(definitions, parameters);
                var execution = await _ssrs.LoadReportAsync(reportPath, ct);
                await _ssrs.SetParametersAsync(execution, resolved, _options.DefaultCulture, ct);
                session.DrillthroughTo(
                    reportPath,
                    execution,
                    FilterPromptedParameters(definitions, resolved));

                return await RenderSessionAsync(session, ct);
            },
            cancellationToken);
    }

    public async Task<ViewReportResult> BackAsync(
        string sessionId,
        ViewerSessionGuard guard,
        CancellationToken cancellationToken)
    {
        return await guard.ExecuteAsync(
            sessionId,
            async (session, ct) =>
            {
                var entry = session.PopBack();

                try
                {
                    // Prefer restoring the prior Execution when SSRS still holds it.
                    session.Restore(entry, entry.Execution);
                    return await RenderSessionAsync(session, ct);
                }
                catch (ExecutionNotFoundException)
                {
                    // Prior Execution dropped on SSRS — reload path + parameters (Drilldown may reset).
                    var definitions = await GetParametersAsync(
                        entry.ReportPath,
                        forRendering: false,
                        valuesForCascading: null,
                        ct);
                    var resolved = PrepareParametersForSsrs(definitions, entry.Parameters);
                    var execution = await _ssrs.LoadReportAsync(entry.ReportPath, ct);
                    await _ssrs.SetParametersAsync(
                        execution,
                        resolved,
                        _options.DefaultCulture,
                        ct);
                    session.Restore(entry, execution);
                    return await RenderSessionAsync(session, ct);
                }
            },
            cancellationToken);
    }


    public Task<ViewReportResult> SortAsync(
        string sessionId,
        string sortItem,
        SortDirection direction,
        bool clear,
        ViewerSessionGuard guard,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sortItem))
        {
            throw new ArgumentException("sortItem is required.", nameof(sortItem));
        }

        return guard.ExecuteAsync(
            sessionId,
            async (session, ct) =>
            {
                var sort = await _ssrs.SortAsync(session.Execution, sortItem, direction, clear, ct);
                session.CurrentPage = sort.CurrentPage;
                if (sort.PageCount > 0)
                {
                    session.PageCount = sort.PageCount;
                }

                return await RenderSessionAsync(session, ct);
            },
            cancellationToken);
    }
    public Task<ViewReportResult> NavigateDocumentMapAsync(
        string sessionId,
        string documentMapId,
        ViewerSessionGuard guard,
        CancellationToken cancellationToken)
    {
        return guard.ExecuteAsync(
            sessionId,
            async (session, ct) =>
            {
                await _ssrs.NavigateDocumentMapAsync(session.Execution, documentMapId, ct);
                return await RenderSessionAsync(session, ct);
            },
            cancellationToken);
    }

    public Task<ViewReportResult> NavigateBookmarkAsync(
        string sessionId,
        string bookmarkId,
        ViewerSessionGuard guard,
        CancellationToken cancellationToken)
    {
        return guard.ExecuteAsync(
            sessionId,
            async (session, ct) =>
            {
                await _ssrs.NavigateBookmarkAsync(session.Execution, bookmarkId, ct);
                return await RenderSessionAsync(session, ct);
            },
            cancellationToken);
    }

    public async Task<ExportResult> ExportAsync(
        string sessionId,
        string format,
        ViewerSessionGuard guard,
        CancellationToken cancellationToken,
        string? pageSize = null,
        string? orientation = null)
    {
        var resolved = ResolveExportFormat(format);
        PdfPageLayout? pdfLayout = null;
        if (resolved.SsrsFormat == "PDF" &&
            !resolved.AccessiblePdf &&
            (!string.IsNullOrWhiteSpace(pageSize) || !string.IsNullOrWhiteSpace(orientation)))
        {
            pdfLayout = ResolvePdfPageLayout(pageSize, orientation);
        }

        return await guard.ExecuteAsync(
            sessionId,
            async (session, ct) =>
            {
                var deviceInfo = CreateExportDeviceInfo(
                    session,
                    pdfLayout,
                    resolved.OutputFormat,
                    resolved.AccessiblePdf);
                var render = await _ssrs.RenderAsync(session.Execution, resolved.SsrsFormat, deviceInfo, ct);
                var fileName = BuildExportFileName(session.ReportPath, resolved.FileExtension);
                return new ExportResult(render.Content, render.MimeType, fileName);
            },
            cancellationToken);
    }

    public Task<FindResult> FindAsync(
        string sessionId,
        string findValue,
        ViewerSessionGuard guard,
        CancellationToken cancellationToken) =>
        guard.ExecuteAsync(
            sessionId,
            (session, ct) => FindOnSessionAsync(session, findValue, nextMatch: false, ct),
            cancellationToken);

    public Task<FindResult> FindNextAsync(
        string sessionId,
        string findValue,
        ViewerSessionGuard guard,
        CancellationToken cancellationToken) =>
        guard.ExecuteAsync(
            sessionId,
            (session, ct) => FindOnSessionAsync(session, findValue, nextMatch: true, ct),
            cancellationToken);

    private async Task<FindResult> FindOnSessionAsync(
        ViewerSession session,
        string findValue,
        bool nextMatch,
        CancellationToken cancellationToken)
    {
        var pageCount = Math.Max(1, session.PageCount);
        var currentPage = Math.Clamp(session.CurrentPage, 1, pageCount);

        int startPage;
        int endPage;
        if (nextMatch)
        {
            startPage = currentPage >= pageCount ? 1 : currentPage + 1;
            endPage = currentPage;
        }
        else
        {
            startPage = 1;
            endPage = pageCount;
        }

        var foundPage = await _ssrs.FindStringAsync(
            session.Execution,
            startPage,
            endPage,
            findValue,
            cancellationToken);

        var found = foundPage > 0;
        if (found)
        {
            session.CurrentPage = foundPage;
        }

        // Only found Find / Find next applies HTML DeviceInfo FindString for highlight chrome (#52).
        // FindString is not persisted on the Viewer Session — non-Find RenderSession paths omit it (#53).
        var render = await RenderSessionAsync(
            session,
            cancellationToken,
            findString: found ? findValue : null);
        return new FindResult(
            render.Html,
            found,
            new PageInfo(session.CurrentPage, session.PageCount));
    }

    /// <param name="findString">
    /// HTML DeviceInfo FindString for native highlight chrome. Pass only from found Find / Find next;
    /// default null so View Report, Refresh, page, Zoom, Sort, and other non-Find renders stay clear (#53).
    /// </param>
    private async Task<ViewReportResult> RenderSessionAsync(
        ViewerSession session,
        CancellationToken cancellationToken,
        string? findString = null)
    {
        var resourceRoot = $"/api/sessions/{session.Id}/resources/";
        var replacementRoot = $"/api/sessions/{session.Id}/actions/";
        var deviceInfo = CreateDeviceInfo(session, includeActionRoots: true, findString);

        var render = await _ssrs.RenderAsync(session.Execution, "HTML4.0", deviceInfo, cancellationToken);
        session.CurrentPage = render.CurrentPage;
        session.PageCount = render.PageCount;

        var html = PrepareHtml(render, resourceRoot, replacementRoot);
        return new ViewReportResult(
            session.Id,
            html,
            new PageInfo(render.CurrentPage, render.PageCount),
            session.ZoomPercent,
            session.CanGoBack,
            render.DocumentMap,
            session.ReportPath,
            session.Parameters);
    }

    private static DeviceInfo CreateDeviceInfo(
        ViewerSession session,
        bool includeActionRoots,
        string? findString = null)
    {
        string? resourceRoot = null;
        string? replacementRoot = null;
        if (includeActionRoots)
        {
            resourceRoot = $"/api/sessions/{session.Id}/resources/";
            replacementRoot = $"/api/sessions/{session.Id}/actions/";
        }

        // HTMLFragment must be false: Interactive Sort icons/links are omitted from fragments
        // (SSRS emits rs:Command=Sort + rs:SortId only on a full HTML document).
        return new DeviceInfo(
            HtmlFragment: false,
            Toolbar: false,
            ZoomPercent: session.ZoomPercent,
            Section: session.CurrentPage,
            ReplacementRoot: replacementRoot,
            ResourceStreamRoot: resourceRoot,
            FindString: findString);
    }

    private static DeviceInfo CreateExportDeviceInfo(
        ViewerSession session,
        PdfPageLayout? pdfLayout,
        string? outputFormat = null,
        bool accessiblePdf = false)
    {
        // Printer-friendly PDF uses full Execution pages; omit HTML Section / Zoom DeviceInfo.
        if (pdfLayout is not null)
        {
            return new DeviceInfo(
                HtmlFragment: false,
                Toolbar: false,
                ZoomPercent: 100,
                Section: null,
                PageWidth: pdfLayout.PageWidth,
                PageHeight: pdfLayout.PageHeight,
                OutputFormat: outputFormat,
                AccessiblePdf: accessiblePdf);
        }

        var baseInfo = CreateDeviceInfo(session, includeActionRoots: false);
        if (outputFormat is null && !accessiblePdf)
        {
            return baseInfo;
        }

        return baseInfo with { OutputFormat = outputFormat, AccessiblePdf = accessiblePdf };
    }

    private static PdfPageLayout ResolvePdfPageLayout(string? pageSize, string? orientation)
    {
        var sizeKey = string.IsNullOrWhiteSpace(pageSize)
            ? PdfPrintSettings.DefaultPageSize
            : pageSize.Trim();
        if (!PdfPrintSettings.PageSizes.TryGetValue(sizeKey, out var size))
        {
            throw new InvalidViewerInteractionException($"Unknown page size '{pageSize}'.");
        }

        var isLandscape = string.Equals(
            orientation?.Trim(),
            PdfPrintSettings.Landscape,
            StringComparison.OrdinalIgnoreCase);
        return isLandscape
            ? new PdfPageLayout(size.Height, size.Width)
            : new PdfPageLayout(size.Width, size.Height);
    }

    private sealed record PdfPageLayout(string PageWidth, string PageHeight);

    private static int ResolveTargetPage(ViewerSession session, string action, int? page)
    {
        var normalized = action.Trim().ToLowerInvariant();
        return normalized switch
        {
            "next" => session.CurrentPage + 1,
            "previous" or "prev" => session.CurrentPage - 1,
            "goto" => page ?? throw new InvalidViewerInteractionException("page is required for goto."),
            _ => throw new InvalidViewerInteractionException($"Unknown page action '{action}'."),
        };
    }

    /// <summary>
    /// Maps client Export format tokens to SSRS Render format + DeviceInfo extras + file extension.
    /// Accessible PDF → PDF + AccessiblePDF; TIFF → IMAGE + OutputFormat=TIFF.
    /// </summary>
    private static ResolvedExportFormat ResolveExportFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            throw new UnsupportedExportFormatException(format ?? string.Empty);
        }

        var normalized = format.Trim().ToUpperInvariant();
        return normalized switch
        {
            "WORDOPENXML" => new ResolvedExportFormat("WORDOPENXML", "docx"),
            "EXCELOPENXML" => new ResolvedExportFormat("EXCELOPENXML", "xlsx"),
            "PPTX" => new ResolvedExportFormat("PPTX", "pptx"),
            "PDF" => new ResolvedExportFormat("PDF", "pdf"),
            "ACCESSIBLEPDF" => new ResolvedExportFormat("PDF", "pdf", AccessiblePdf: true),
            "TIFF" => new ResolvedExportFormat("IMAGE", "tiff", OutputFormat: "TIFF"),
            "MHTML" => new ResolvedExportFormat("MHTML", "mhtml"),
            "CSV" => new ResolvedExportFormat("CSV", "csv"),
            "XML" => new ResolvedExportFormat("XML", "xml"),
            _ => throw new UnsupportedExportFormatException(format),
        };
    }

    private sealed record ResolvedExportFormat(
        string SsrsFormat,
        string FileExtension,
        string? OutputFormat = null,
        bool AccessiblePdf = false);

    private static string BuildExportFileName(string reportPath, string fileExtension)
    {
        var leaf = reportPath.Trim('/').Split('/').LastOrDefault();
        if (string.IsNullOrWhiteSpace(leaf))
        {
            leaf = "report";
        }

        return $"{leaf}.{fileExtension}";
    }

    private static string PrepareHtml(RenderResult render, string resourceRoot, string? replacementRoot)
    {
        var html = Encoding.UTF8.GetString(render.Content);
        // Interactive Sort icons require a full HTML document (HTMLFragment=false); embed body only.
        html = ExtractHtmlBody(html);
        html = RewriteResourcePlaceholders(html, resourceRoot, render.StreamIds);
        // Rewrite ReportServer-hosted resource URLs before ActionLinkRewriter strips reportserver hosts.
        html = ResourceUrlRewriter.Rewrite(html, resourceRoot);
        if (!string.IsNullOrEmpty(replacementRoot))
        {
            html = html.Replace("{ReplacementRoot}", replacementRoot, StringComparison.Ordinal);
        }

        html = FindHighlightNormalizer.Normalize(html);
        return ActionLinkRewriter.Rewrite(html, replacementRoot);
    }

    /// <summary>
    /// When DeviceInfo HTMLFragment is false, SSRS returns a full document. Embed body contents
    /// plus head <c>style</c> blocks (grid borders, zebra rows, header chrome live there).
    /// </summary>
    internal static string ExtractHtmlBody(string html)
    {
        var bodyMatch = HtmlBodyRegex().Match(html);
        if (!bodyMatch.Success)
        {
            return html;
        }

        var styles = new StringBuilder();
        foreach (Match styleMatch in HtmlStyleRegex().Matches(html))
        {
            // Only keep head styles that precede the body (report CSS), not accidental body styles.
            if (styleMatch.Index < bodyMatch.Index)
            {
                styles.Append(styleMatch.Value);
            }
        }

        return styles + bodyMatch.Groups["body"].Value;
    }

    [GeneratedRegex(
        @"<body\b[^>]*>(?<body>.*)</body>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlBodyRegex();

    [GeneratedRegex(
        @"<style\b[^>]*>.*?</style>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlStyleRegex();
    private static string RewriteResourcePlaceholders(
        string html,
        string resourceRoot,
        IReadOnlyList<string> streamIds)
    {
        var rewritten = html.Replace("{ResourceStreamRoot}", resourceRoot, StringComparison.Ordinal);
        foreach (var streamId in streamIds)
        {
            rewritten = rewritten.Replace(
                $"{{resource:{streamId}}}",
                resourceRoot + Uri.EscapeDataString(streamId),
                StringComparison.Ordinal);
        }

        return rewritten;
    }

    private IReadOnlyList<ParameterValue> PrepareParametersForSsrs(
        IReadOnlyList<ReportParameterDefinition> definitions,
        IReadOnlyList<ParameterValue> submitted) =>
        FormatDateTimeParameterValues(
            definitions,
            ResolveParametersForExecution(definitions, submitted),
            _options.DefaultCulture);

    /// <summary>
    /// Empty Values means SSRS NULL only for nullable parameters. Blank non-nullables must not
    /// clear LoadReport defaults (SetExecutionParameters with Name and no Value).
    /// Non-prompted definitions omitted by the client are included from their DefaultValues so
    /// SetParameters does not rely only on LoadReport leftovers.
    /// </summary>
    internal static IReadOnlyList<ParameterValue> ResolveParametersForExecution(
        IReadOnlyList<ReportParameterDefinition> definitions,
        IReadOnlyList<ParameterValue> submitted)
    {
        var byName = definitions.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var resolved = new List<ParameterValue>(submitted.Count);
        var submittedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in submitted)
        {
            submittedNames.Add(parameter.Name);
            byName.TryGetValue(parameter.Name, out var definition);
            var values = parameter.Values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToArray();

            if (values.Length > 0)
            {
                resolved.Add(new ParameterValue(parameter.Name, values));
                continue;
            }

            if (definition?.Nullable == true)
            {
                resolved.Add(new ParameterValue(parameter.Name, []));
                continue;
            }

            if (definition is { DefaultValues.Count: > 0 })
            {
                resolved.Add(new ParameterValue(parameter.Name, definition.DefaultValues));
            }

            // Else omit — keep whatever LoadReport already applied.
        }

        foreach (var definition in definitions)
        {
            // Not on the parameter form (hidden or empty Prompt): apply DefaultValues when present.
            if (definition.ShownOnParameterForm || submittedNames.Contains(definition.Name))
            {
                continue;
            }

            if (definition.DefaultValues.Count > 0)
            {
                resolved.Add(new ParameterValue(definition.Name, definition.DefaultValues));
            }
        }

        return resolved;
    }

    /// <summary>
    /// HTML date inputs submit <c>yyyy-MM-dd</c>; SSRS SetExecutionParameters parses DateTime
    /// values with ParameterLanguage (DefaultCulture). Convert to that culture's short date.
    /// </summary>
    internal static IReadOnlyList<ParameterValue> FormatDateTimeParameterValues(
        IReadOnlyList<ReportParameterDefinition> definitions,
        IReadOnlyList<ParameterValue> values,
        string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(
            string.IsNullOrWhiteSpace(cultureName) ? "en-GB" : cultureName);
        var byName = definitions.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

        return values.Select(parameter =>
        {
            if (!byName.TryGetValue(parameter.Name, out var definition) ||
                !definition.DataType.Equals("DateTime", StringComparison.OrdinalIgnoreCase) ||
                parameter.Values.Count == 0)
            {
                return parameter;
            }

            var formatted = parameter.Values
                .Select(value => FormatDateTimeValue(value, culture))
                .ToArray();
            return new ParameterValue(parameter.Name, formatted);
        }).ToArray();
    }

    internal static string FormatDateTimeValue(string value, CultureInfo culture)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var trimmed = value.Trim();

        // Date-only picker (HTML <input type="date">).
        if (DateTime.TryParseExact(
                trimmed,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var isoDate))
        {
            return isoDate.ToString("d", culture);
        }

        string[] isoDateTimes =
        [
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm",
            "yyyy-MM-dd HH:mm:ss",
        ];
        if (DateTime.TryParseExact(
                trimmed,
                isoDateTimes,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var isoDateTime))
        {
            return isoDateTime.Date.ToString("d", culture);
        }

        // SSRS defaults often arrive en-US even when ParameterLanguage is en-GB.
        if (DateTime.TryParse(trimmed, culture, DateTimeStyles.AllowWhiteSpaces, out var parsed) ||
            DateTime.TryParse(
                trimmed,
                CultureInfo.GetCultureInfo("en-US"),
                DateTimeStyles.AllowWhiteSpaces,
                out parsed) ||
            DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
        {
            return parsed.Date.ToString("d", culture);
        }

        return value;
    }

    /// <summary>
    /// Client / Viewer Session parameter bar: only Report Parameters shown on the form.
    /// </summary>
    internal static IReadOnlyList<ParameterValue> FilterPromptedParameters(
        IReadOnlyList<ReportParameterDefinition> definitions,
        IReadOnlyList<ParameterValue> resolved)
    {
        var promptedNames = definitions
            .Where(d => d.ShownOnParameterForm)
            .Select(d => d.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return resolved.Where(p => promptedNames.Contains(p.Name)).ToArray();
    }

    /// <summary>
    /// Adds off-form DefaultValues when the cascading client payload omitted them.
    /// </summary>
    internal static IReadOnlyList<ParameterValue> MergeNonPromptedDefaultsForCascading(
        IReadOnlyList<ReportParameterDefinition> definitions,
        IReadOnlyList<ParameterValue> submitted)
    {
        var byName = new Dictionary<string, ParameterValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in submitted)
        {
            byName[parameter.Name] = parameter;
        }

        foreach (var definition in definitions)
        {
            if (definition.ShownOnParameterForm ||
                definition.DefaultValues.Count == 0 ||
                byName.ContainsKey(definition.Name))
            {
                continue;
            }

            byName[definition.Name] = new ParameterValue(definition.Name, definition.DefaultValues);
        }

        return byName.Values.ToArray();
    }
}

public sealed record ViewReportResult(
    string SessionId,
    string Html,
    PageInfo PageInfo,
    int ZoomPercent,
    bool CanGoBack,
    IReadOnlyList<DocumentMapNode>? DocumentMap,
    string ReportPath,
    IReadOnlyList<ParameterValue> Parameters);

public sealed record FindResult(string Html, bool Found, PageInfo PageInfo);

public sealed record PageInfo(int CurrentPage, int PageCount);

public sealed record ExportResult(byte[] Content, string MimeType, string FileName);

public sealed class UnsupportedExportFormatException : Exception
{
    public UnsupportedExportFormatException(string format)
        : base(
            $"Unsupported Export format '{format}'. Use WORDOPENXML, EXCELOPENXML, PPTX, PDF, ACCESSIBLEPDF, TIFF, MHTML, CSV, or XML.")
    {
        Format = format;
    }

    public string Format { get; }
}

public sealed class InvalidViewerInteractionException : Exception
{
    public InvalidViewerInteractionException(string message)
        : base(message)
    {
    }
}
