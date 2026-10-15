using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace SsrsReportViewer.Api.Ssrs;

/// <summary>
/// Production SSRS adapter: ReportingService2010 + ReportExecution2005 over NTLM SOAP (ADR-0001 / ADR-0002).
/// </summary>
public sealed class SoapSsrsAdapter : ISsrsAdapter, IDisposable
{
    private static readonly XNamespace Mgmt = SsrsSoapTransport.ManagementNamespace;
    private static readonly XNamespace Exec = SsrsSoapTransport.ExecutionNamespace;

    private readonly SsrsOptions _options;
    private readonly Lazy<SsrsSoapTransport> _soap;

    public SoapSsrsAdapter(IOptions<SsrsOptions> options)
        : this(options, transport: null)
    {
    }

    internal SoapSsrsAdapter(IOptions<SsrsOptions> options, SsrsSoapTransport? transport)
    {
        _options = options.Value;
        _soap = transport is null
            ? new Lazy<SsrsSoapTransport>(() => new SsrsSoapTransport(options))
            : new Lazy<SsrsSoapTransport>(() => transport);
    }

    /// <summary>Configured ReportServer base URL (server-side only; never sent to the client).</summary>
    internal string ReportServerBaseUrl => _options.ReportServerBaseUrl;

    public void Dispose()
    {
        if (_soap.IsValueCreated)
        {
            _soap.Value.Dispose();
        }
    }

    public async Task<IReadOnlyList<CatalogItem>> ListChildrenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var body = new XElement(
            Mgmt + "ListChildren",
            new XElement(Mgmt + "ItemPath", NormalizePath(path)),
            new XElement(Mgmt + "Recursive", false));

        try
        {
            var document = await _soap.Value.InvokeManagementAsync("ListChildren", body, cancellationToken);
            return document
                .Descendants(Mgmt + "CatalogItem")
                .Select(ParseCatalogItem)
                .Where(i => i is not null)
                .Select(i => i!)
                .ToArray();
        }
        catch (CatalogPathNotFoundException)
        {
            throw;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            throw new CatalogPathNotFoundException(NormalizePath(path));
        }
    }

    public async Task<IReadOnlyList<ReportParameterDefinition>> GetParametersAsync(
        string reportPath,
        bool forRendering,
        IReadOnlyList<ParameterValue>? valuesForCascading = null,
        CancellationToken cancellationToken = default)
    {
        XElement valuesElement;
        if (valuesForCascading is { Count: > 0 })
        {
            valuesElement = new XElement(Mgmt + "Values");
            foreach (var parameter in valuesForCascading)
            {
                if (parameter.Values.Count == 0)
                {
                    // SSRS NULL for cascading dependency resolution.
                    valuesElement.Add(
                        new XElement(
                            Mgmt + "ParameterValue",
                            new XElement(Mgmt + "Name", parameter.Name)));
                    continue;
                }

                foreach (var value in parameter.Values)
                {
                    valuesElement.Add(
                        new XElement(
                            Mgmt + "ParameterValue",
                            new XElement(Mgmt + "Name", parameter.Name),
                            new XElement(Mgmt + "Value", value)));
                }
            }
        }
        else
        {
            valuesElement = SsrsSoapTransport.Nil(Mgmt + "Values");
        }

        // Omit HistoryID entirely. SSRS maps HistoryID to internal snapshotID (DateTime);
        // <HistoryID xsi:nil="true"/> and empty HistoryID both fault as ParameterTypeMismatch.
        //
        // ForRendering comes from the caller: true runs parameter queries for Valid Values
        // (parameters form); false is metadata-only (View Report required-parameter check).
        // Do not infer solely from cascading Values — initial load needs true with empty Values.
        var body = new XElement(
            Mgmt + "GetItemParameters",
            new XElement(Mgmt + "ItemPath", NormalizePath(reportPath)),
            new XElement(Mgmt + "ForRendering", forRendering),
            valuesElement,
            SsrsSoapTransport.Nil(Mgmt + "Credentials"));

        try
        {
            var document = await _soap.Value.InvokeManagementAsync("GetItemParameters", body, cancellationToken);
            return document
                .Descendants(Mgmt + "ItemParameter")
                .Select(ParseParameter)
                .ToArray();
        }
        catch (CatalogPathNotFoundException)
        {
            throw new ReportNotFoundException(NormalizePath(reportPath));
        }
    }

    public async Task<ExecutionHandle> LoadReportAsync(
        string reportPath,
        CancellationToken cancellationToken = default)
    {
        // Omit HistoryID — same snapshotID DateTime fault as GetItemParameters when nil/empty.
        var body = new XElement(
            Exec + "LoadReport",
            new XElement(Exec + "Report", NormalizePath(reportPath)));

        try
        {
            var (_, executionId) = await _soap.Value.InvokeExecutionAsync(
                "LoadReport",
                body,
                executionId: null,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(executionId))
            {
                throw new InvalidOperationException("LoadReport did not return an ExecutionID.");
            }

            return new ExecutionHandle(executionId);
        }
        catch (CatalogPathNotFoundException)
        {
            throw new ReportNotFoundException(NormalizePath(reportPath));
        }
    }

    public async Task SetParametersAsync(
        ExecutionHandle execution,
        IReadOnlyList<ParameterValue> values,
        string culture,
        CancellationToken cancellationToken = default)
    {
        var parameters = new XElement(Exec + "Parameters");
        foreach (var parameter in values)
        {
            if (parameter.Values.Count == 0)
            {
                // SSRS NULL: ParameterValue with Name and no Value child.
                parameters.Add(
                    new XElement(
                        Exec + "ParameterValue",
                        new XElement(Exec + "Name", parameter.Name)));
                continue;
            }

            foreach (var value in parameter.Values)
            {
                parameters.Add(
                    new XElement(
                        Exec + "ParameterValue",
                        new XElement(Exec + "Name", parameter.Name),
                        new XElement(Exec + "Value", value)));
            }
        }

        var body = new XElement(
            Exec + "SetExecutionParameters",
            parameters,
            new XElement(
                Exec + "ParameterLanguage",
                string.IsNullOrWhiteSpace(culture) ? _options.DefaultCulture : culture));

        await InvokeExecutionAsync("SetExecutionParameters", body, execution, cancellationToken);
    }

    public async Task<RenderResult> RenderAsync(
        ExecutionHandle execution,
        string format,
        DeviceInfo deviceInfo,
        CancellationToken cancellationToken = default)
    {
        var body = new XElement(
            Exec + "Render",
            new XElement(Exec + "Format", format),
            new XElement(Exec + "DeviceInfo", ToDeviceInfoXml(deviceInfo)));

        var (document, _) = await InvokeExecutionAsync("Render", body, execution, cancellationToken);
        var resultNode = document.Descendants(Exec + "Result").FirstOrDefault()?.Value;
        var content = string.IsNullOrEmpty(resultNode) ? [] : Convert.FromBase64String(resultNode);
        var mimeType = document.Descendants(Exec + "MimeType").FirstOrDefault()?.Value ?? "application/octet-stream";
        var streamIds = document
            .Descendants(Exec + "StreamIds")
            .Elements()
            .Where(e => e.Name.LocalName is "string" or "StreamIds")
            .Select(e => e.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray();
        if (streamIds.Length == 0)
        {
            streamIds = document
                .Descendants()
                .Where(e => e.Name.LocalName == "string" && e.Parent?.Name.LocalName == "StreamIds")
                .Select(e => e.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToArray();
        }

        var info = await GetExecutionInfoAsync(execution, cancellationToken);
        var currentPage = deviceInfo.Section is > 0 ? deviceInfo.Section.Value : info.CurrentPage;
        return new RenderResult(
            content,
            mimeType,
            streamIds,
            info.PageCount,
            currentPage,
            info.DocumentMap);
    }

    public async Task<StreamResult> RenderStreamAsync(
        ExecutionHandle execution,
        string format,
        string streamId,
        CancellationToken cancellationToken = default)
    {
        var body = new XElement(
            Exec + "RenderStream",
            new XElement(Exec + "Format", format),
            new XElement(Exec + "StreamID", streamId),
            new XElement(Exec + "DeviceInfo", string.Empty));

        var (document, _) = await InvokeExecutionAsync("RenderStream", body, execution, cancellationToken);
        var resultNode = document.Descendants(Exec + "Result").FirstOrDefault()?.Value;
        var content = string.IsNullOrEmpty(resultNode) ? [] : Convert.FromBase64String(resultNode);
        var mimeType = document.Descendants(Exec + "MimeType").FirstOrDefault()?.Value ?? "application/octet-stream";
        return new StreamResult(content, mimeType);
    }

    public async Task ToggleItemAsync(
        ExecutionHandle execution,
        string toggleId,
        CancellationToken cancellationToken = default)
    {
        // Drilldown show/hide (glossary: Drilldown ↔ ToggleItem).
        var body = new XElement(
            Exec + "ToggleItem",
            new XElement(Exec + "ToggleID", toggleId));
        await InvokeExecutionAsync("ToggleItem", body, execution, cancellationToken);
    }

    public async Task<SortResult> SortAsync(
        ExecutionHandle execution,
        string sortItem,
        SortDirection direction,
        bool clear,
        CancellationToken cancellationToken = default)
    {
        // Clear means "clear other sorts", not Direction=None. Unsorted column links send
        // ClearSort=TRUE with Ascending/Descending; forcing None prevented the icon from changing.
        var directionName = direction == SortDirection.None ? "None" : direction.ToString();

        var body = new XElement(
            Exec + "Sort",
            new XElement(Exec + "SortItem", sortItem),
            new XElement(Exec + "Direction", directionName),
            new XElement(Exec + "Clear", clear));

        var (document, _) = await InvokeExecutionAsync("Sort", body, execution, cancellationToken);
        var pageNumber = ParseInt(document.Descendants(Exec + "PageNumber").FirstOrDefault()?.Value, fallback: 1);
        var reportItem = document.Descendants(Exec + "ReportItem").FirstOrDefault()?.Value;
        var info = await GetExecutionInfoAsync(execution, cancellationToken);
        return new SortResult(pageNumber > 0 ? pageNumber : info.CurrentPage, info.PageCount, reportItem);
    }

    public async Task NavigateDocumentMapAsync(
        ExecutionHandle execution,
        string documentMapId,
        CancellationToken cancellationToken = default)
    {
        var body = new XElement(
            Exec + "NavigateDocumentMap",
            new XElement(Exec + "DocMapID", documentMapId));
        await InvokeExecutionAsync("NavigateDocumentMap", body, execution, cancellationToken);
    }

    public async Task NavigateBookmarkAsync(
        ExecutionHandle execution,
        string bookmarkId,
        CancellationToken cancellationToken = default)
    {
        var body = new XElement(
            Exec + "NavigateBookmark",
            new XElement(Exec + "BookmarkID", bookmarkId));
        await InvokeExecutionAsync("NavigateBookmark", body, execution, cancellationToken);
    }

    public async Task<int> FindStringAsync(
        ExecutionHandle execution,
        int startPage,
        int endPage,
        string findValue,
        CancellationToken cancellationToken = default)
    {
        var body = new XElement(
            Exec + "FindString",
            new XElement(Exec + "StartPage", startPage),
            new XElement(Exec + "EndPage", endPage),
            new XElement(Exec + "FindValue", findValue));

        var (document, _) = await InvokeExecutionAsync("FindString", body, execution, cancellationToken);
        // ReportExecution2005 WSDL returns the page as PageNumber (same as Sort), not FindStringResult.
        return ParseFindStringPage(document);
    }

    /// <summary>
    /// Finds the page number from a FindString SOAP response. Prefer <c>PageNumber</c> (WSDL /
    /// same shape as Sort); accept <c>FindStringResult</c> as a defensive fallback. Match by
    /// local name so minor Execution namespace date variants still parse.
    /// </summary>
    internal static int ParseFindStringPage(XDocument document)
    {
        var pageNode = document.Descendants()
                           .FirstOrDefault(e => e.Name.LocalName == "PageNumber")
                       ?? document.Descendants()
                           .FirstOrDefault(e => e.Name.LocalName == "FindStringResult");
        return ParseInt(pageNode?.Value, fallback: 0);
    }

    private async Task<(XDocument Document, string? ExecutionId)> InvokeExecutionAsync(
        string operation,
        XElement body,
        ExecutionHandle execution,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _soap.Value.InvokeExecutionAsync(operation, body, execution.Id, cancellationToken);
        }
        catch (ExecutionNotFoundException)
        {
            throw new ExecutionNotFoundException(execution.Id);
        }
    }

    private async Task<(int PageCount, int CurrentPage, IReadOnlyList<DocumentMapNode>? DocumentMap)> GetExecutionInfoAsync(
        ExecutionHandle execution,
        CancellationToken cancellationToken)
    {
        var body = new XElement(Exec + "GetExecutionInfo");
        var (document, _) = await InvokeExecutionAsync("GetExecutionInfo", body, execution, cancellationToken);
        var info = document.Descendants(Exec + "ExecutionInfo").FirstOrDefault()
                   ?? document.Descendants(Exec + "GetExecutionInfoResult").FirstOrDefault();
        var pageCount = ParseInt(info?.Element(Exec + "NumPages")?.Value, fallback: 1);
        var mapRoot = info?.Element(Exec + "DocumentMap");
        IReadOnlyList<DocumentMapNode>? documentMap = mapRoot is null
            ? null
            : ParseDocumentMap(mapRoot);

        return (Math.Max(1, pageCount), 1, documentMap);
    }

    private static IReadOnlyList<DocumentMapNode>? ParseDocumentMap(XElement mapRoot)
    {
        // SSRS often wraps entries under a synthetic root DocumentMap / DocumentMapNode.
        var directChildren = mapRoot.Elements(Exec + "DocumentMapNode").ToArray();
        if (directChildren.Length > 0)
        {
            return ParseDocumentMapNodes(directChildren).ToArray();
        }

        var nested = mapRoot.Elements(Exec + "Children").Elements(Exec + "DocumentMapNode").ToArray();
        if (nested.Length > 0)
        {
            return ParseDocumentMapNodes(nested).ToArray();
        }

        var uniqueName = mapRoot.Element(Exec + "UniqueName")?.Value;
        if (!string.IsNullOrWhiteSpace(uniqueName))
        {
            return
            [
                new DocumentMapNode(
                    uniqueName,
                    mapRoot.Element(Exec + "Label")?.Value ?? uniqueName,
                    ParseDocumentMapNodes(
                        mapRoot.Elements(Exec + "Children").Elements(Exec + "DocumentMapNode")).ToArray()),
            ];
        }

        return null;
    }

    private static IEnumerable<DocumentMapNode> ParseDocumentMapNodes(IEnumerable<XElement> nodes) =>
        nodes.Select(node => new DocumentMapNode(
            node.Element(Exec + "UniqueName")?.Value
            ?? node.Element(Exec + "Id")?.Value
            ?? string.Empty,
            node.Element(Exec + "Label")?.Value ?? string.Empty,
            ParseDocumentMapNodes(node.Elements(Exec + "Children").Elements(Exec + "DocumentMapNode")).ToArray()));

    private static CatalogItem? ParseCatalogItem(XElement item)
    {
        var type = item.Element(Mgmt + "TypeName")?.Value
                   ?? item.Element(Mgmt + "Type")?.Value
                   ?? string.Empty;
        CatalogItemType? mapped = type.ToUpperInvariant() switch
        {
            "FOLDER" => CatalogItemType.Folder,
            "REPORT" => CatalogItemType.Report,
            _ => null,
        };
        if (mapped is null)
        {
            return null;
        }

        var name = item.Element(Mgmt + "Name")?.Value ?? string.Empty;
        var path = item.Element(Mgmt + "Path")?.Value ?? string.Empty;
        return new CatalogItem(name, path, mapped.Value);
    }

    private static ReportParameterDefinition ParseParameter(XElement parameter)
    {
        var name = parameter.Element(Mgmt + "Name")?.Value ?? string.Empty;
        var dataType = parameter.Element(Mgmt + "ParameterTypeName")?.Value
                       ?? parameter.Element(Mgmt + "Type")?.Value
                       ?? "Text";
        var nullable = ParseBool(parameter.Element(Mgmt + "Nullable")?.Value);
        var multiValue = ParseBool(parameter.Element(Mgmt + "MultiValue")?.Value);
        var promptUser = ParseBool(parameter.Element(Mgmt + "PromptUser")?.Value, defaultValue: true);
        var allowBlank = ParseBool(parameter.Element(Mgmt + "AllowBlank")?.Value);
        var prompt = parameter.Element(Mgmt + "Prompt")?.Value;
        // ReportService2010 ItemParameter.DefaultValues / Dependencies are string[] with
        // [XmlArrayItem("Value")]. Some envelopes also use Serialization Arrays <string>.
        var defaults = ParseStringArray(parameter.Element(Mgmt + "DefaultValues"));
        var validValues = parameter
            .Element(Mgmt + "ValidValues")?
            .Elements(Mgmt + "ValidValue")
            .Select(v => new ReportParameterValidValue(
                v.Element(Mgmt + "Label")?.Value ?? v.Element(Mgmt + "Value")?.Value ?? string.Empty,
                v.Element(Mgmt + "Value")?.Value ?? string.Empty))
            .ToArray() ?? [];
        var dependencies = ParseStringArray(parameter.Element(Mgmt + "Dependencies"));

        // Match native parameter bar: empty Prompt is not shown, so not user-required.
        var shownOnForm = promptUser && !string.IsNullOrWhiteSpace(prompt);
        var required = shownOnForm && !nullable && !allowBlank && defaults.Length == 0;
        return new ReportParameterDefinition(
            name,
            dataType,
            nullable,
            multiValue,
            required,
            prompt,
            defaults,
            validValues,
            dependencies,
            promptUser);
    }

    /// <summary>
    /// Parses ReportService2010 ArrayOfString children (<c>Value</c> or <c>string</c>).
    /// Blank entries are dropped — SSRS often returns <c>[""]</c> for nullable DateTime with no default.
    /// </summary>
    private static string[] ParseStringArray(XElement? parent) =>
        parent?
            .Elements()
            .Where(e =>
            {
                var local = e.Name.LocalName;
                return local is "Value" or "string";
            })
            .Select(e => e.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray()
        ?? [];

    internal static string ToDeviceInfoXml(DeviceInfo deviceInfo)
    {
        var sb = new StringBuilder();
        sb.Append("<DeviceInfo>");
        sb.Append("<HTMLFragment>").Append(deviceInfo.HtmlFragment ? "true" : "false").Append("</HTMLFragment>");
        sb.Append("<Toolbar>").Append(deviceInfo.Toolbar ? "true" : "false").Append("</Toolbar>");
        // Interactive Sort / toggle images and scripts need JS enabled (HTML device info default is true).
        sb.Append("<JavaScript>true</JavaScript>");
        sb.Append("<Zoom>").Append(deviceInfo.ZoomPercent.ToString(CultureInfo.InvariantCulture)).Append("</Zoom>");
        if (deviceInfo.Section is int section and > 0)
        {
            sb.Append("<Section>").Append(section.ToString(CultureInfo.InvariantCulture)).Append("</Section>");
        }

        if (!string.IsNullOrWhiteSpace(deviceInfo.ReplacementRoot))
        {
            sb.Append("<ReplacementRoot>")
                .Append(System.Security.SecurityElement.Escape(deviceInfo.ReplacementRoot))
                .Append("</ReplacementRoot>");
        }

        if (!string.IsNullOrWhiteSpace(deviceInfo.ResourceStreamRoot))
        {
            sb.Append("<ResourceStreamRoot>")
                .Append(System.Security.SecurityElement.Escape(deviceInfo.ResourceStreamRoot))
                .Append("</ResourceStreamRoot>");
        }

        if (!string.IsNullOrWhiteSpace(deviceInfo.PageWidth))
        {
            sb.Append("<PageWidth>")
                .Append(System.Security.SecurityElement.Escape(deviceInfo.PageWidth))
                .Append("</PageWidth>");
        }

        if (!string.IsNullOrWhiteSpace(deviceInfo.PageHeight))
        {
            sb.Append("<PageHeight>")
                .Append(System.Security.SecurityElement.Escape(deviceInfo.PageHeight))
                .Append("</PageHeight>");
        }

        if (!string.IsNullOrWhiteSpace(deviceInfo.OutputFormat))
        {
            sb.Append("<OutputFormat>")
                .Append(System.Security.SecurityElement.Escape(deviceInfo.OutputFormat))
                .Append("</OutputFormat>");
        }

        if (deviceInfo.AccessiblePdf)
        {
            sb.Append("<AccessiblePDF>true</AccessiblePDF>");
        }

        if (!string.IsNullOrWhiteSpace(deviceInfo.FindString))
        {
            sb.Append("<FindString>")
                .Append(System.Security.SecurityElement.Escape(deviceInfo.FindString))
                .Append("</FindString>");
        }

        sb.Append("</DeviceInfo>");
        return sb.ToString();
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Trim() == "/")
        {
            return "/";
        }

        return "/" + path.Trim().Trim('/');
    }

    private static bool ParseBool(string? value, bool defaultValue = false) =>
        bool.TryParse(value, out var parsed) ? parsed : defaultValue;

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}
