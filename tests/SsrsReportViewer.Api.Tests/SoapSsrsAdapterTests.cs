using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using SsrsReportViewer.Api.Ssrs;

namespace SsrsReportViewer.Api.Tests;

/// <summary>
/// Characterizes SoapSsrsAdapter against canned SSRS SOAP envelopes (no live ReportServer).
/// </summary>
public sealed class SoapSsrsAdapterTests
{
    [Fact]
    public async Task GetParameters_omits_HistoryID_so_SSRS_does_not_treat_nil_as_empty_snapshotID()
    {
        // Live SSRS (ReportService2010) parses HistoryID via ParseSnapshotDateParameter.
        // <HistoryID xsi:nil="true"/> and <HistoryID/> both fault as snapshotID DateTime mismatch;
        // omitting the element is the only accepted "no snapshot" form.
        const string soap = """
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <GetItemParametersResponse xmlns="http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer">
                  <Parameters />
                </GetItemParametersResponse>
              </soap:Body>
            </soap:Envelope>
            """;

        var handler = new CapturingSoapHandler(soap);
        using var adapter = CreateAdapter(soap, handler);

        await adapter.GetParametersAsync("/Example Reports/Execution Log", forRendering: false);

        Assert.NotNull(handler.LastRequestXml);
        var document = XDocument.Parse(handler.LastRequestXml);
        XNamespace mgmt = SsrsSoapTransport.ManagementNamespace;
        Assert.Empty(document.Descendants(mgmt + "HistoryID"));
        Assert.Equal("false", document.Descendants(mgmt + "ForRendering").Single().Value);
        Assert.Equal(
            "GetItemParameters",
            document.Descendants().First(e => e.Name.LocalName == "GetItemParameters").Name.LocalName);
    }

    [Fact]
    public async Task GetParameters_sets_ForRendering_true_from_explicit_flag_without_cascading_values()
    {
        // Query-driven Valid Values (e.g. Report Folder) only appear when ForRendering=true,
        // including the initial parameters load with no cascade Values.
        const string soap = """
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <GetItemParametersResponse xmlns="http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer">
                  <Parameters />
                </GetItemParametersResponse>
              </soap:Body>
            </soap:Envelope>
            """;

        var handler = new CapturingSoapHandler(soap);
        using var adapter = CreateAdapter(soap, handler);

        await adapter.GetParametersAsync("/Example Reports/Report List", forRendering: true);

        Assert.NotNull(handler.LastRequestXml);
        var document = XDocument.Parse(handler.LastRequestXml);
        XNamespace mgmt = SsrsSoapTransport.ManagementNamespace;
        Assert.Equal("true", document.Descendants(mgmt + "ForRendering").Single().Value);
    }

    [Fact]
    public async Task GetParameters_sets_ForRendering_false_from_explicit_flag_even_with_cascading_values()
    {
        // Metadata-only callers must keep ForRendering=false; cascading Values alone must not force it.
        const string soap = """
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <GetItemParametersResponse xmlns="http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer">
                  <Parameters />
                </GetItemParametersResponse>
              </soap:Body>
            </soap:Envelope>
            """;

        var handler = new CapturingSoapHandler(soap);
        using var adapter = CreateAdapter(soap, handler);

        await adapter.GetParametersAsync(
            "/Example Reports/Execution Log",
            forRendering: false,
            [new ParameterValue("Server", ["db1"])]);

        Assert.NotNull(handler.LastRequestXml);
        var document = XDocument.Parse(handler.LastRequestXml);
        XNamespace mgmt = SsrsSoapTransport.ManagementNamespace;
        Assert.Equal("false", document.Descendants(mgmt + "ForRendering").Single().Value);
        Assert.Contains(
            document.Descendants(mgmt + "ParameterValue"),
            e => e.Element(mgmt + "Name")?.Value == "Server");
    }

    [Fact]
    public async Task GetParameters_propagates_SOAP_fault_when_forRendering_true()
    {
        // Query-driven Valid Values must not swallow ProcessingAborted as an empty list.
        const string soap = """
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <soap:Fault>
                  <faultcode>soap:Server</faultcode>
                  <faultstring>ProcessingAbortedException: An error has occurred during report processing.</faultstring>
                </soap:Fault>
              </soap:Body>
            </soap:Envelope>
            """;

        using var adapter = CreateAdapter(soap);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.GetParametersAsync("/Example Reports/Report List", forRendering: true));
    }

    [Fact]
    public async Task GetParameters_includes_cascading_Values_when_supplied_with_ForRendering_true()
    {
        const string soap = """
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <GetItemParametersResponse xmlns="http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer">
                  <Parameters />
                </GetItemParametersResponse>
              </soap:Body>
            </soap:Envelope>
            """;

        var handler = new CapturingSoapHandler(soap);
        using var adapter = CreateAdapter(soap, handler);

        await adapter.GetParametersAsync(
            "/Example Reports/Execution Log",
            forRendering: true,
            [new ParameterValue("Server", ["db1"])]);

        Assert.NotNull(handler.LastRequestXml);
        var document = XDocument.Parse(handler.LastRequestXml);
        XNamespace mgmt = SsrsSoapTransport.ManagementNamespace;
        Assert.Equal("true", document.Descendants(mgmt + "ForRendering").Single().Value);
        Assert.Contains(
            document.Descendants(mgmt + "ParameterValue"),
            e => e.Element(mgmt + "Name")?.Value == "Server");
    }

    [Fact]
    public async Task LoadReport_omits_HistoryID_so_SSRS_does_not_treat_nil_as_empty_snapshotID()
    {
        const string soap = """
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Header>
                <ExecutionHeader xmlns="http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices">
                  <ExecutionID>exec-1</ExecutionID>
                </ExecutionHeader>
              </soap:Header>
              <soap:Body>
                <LoadReportResponse xmlns="http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices">
                  <executionInfo />
                </LoadReportResponse>
              </soap:Body>
            </soap:Envelope>
            """;

        var handler = new CapturingSoapHandler(soap);
        using var adapter = CreateAdapter(soap, handler);

        await adapter.LoadReportAsync("/Example Reports/Execution Log");

        Assert.NotNull(handler.LastRequestXml);
        var document = XDocument.Parse(handler.LastRequestXml);
        XNamespace exec = SsrsSoapTransport.ExecutionNamespace;
        Assert.Empty(document.Descendants(exec + "HistoryID"));
        Assert.Equal(
            $"\"{SsrsSoapTransport.ExecutionNamespace}/LoadReport\"",
            handler.LastSoapAction);
        Assert.Contains(
            "2005/06/30/reporting/reportingservices",
            SsrsSoapTransport.ExecutionNamespace,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListChildren_parses_folders_and_reports_from_ReportingService2010_response()
    {
        const string soap = """
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <ListChildrenResponse xmlns="http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer">
                  <ListChildrenResult>
                    <CatalogItem>
                      <Name>Sales</Name>
                      <Path>/Sales</Path>
                      <TypeName>Folder</TypeName>
                    </CatalogItem>
                    <CatalogItem>
                      <Name>Revenue</Name>
                      <Path>/Sales/Revenue</Path>
                      <TypeName>Report</TypeName>
                    </CatalogItem>
                    <CatalogItem>
                      <Name>DataSource1</Name>
                      <Path>/Sales/DataSource1</Path>
                      <TypeName>DataSource</TypeName>
                    </CatalogItem>
                  </ListChildrenResult>
                </ListChildrenResponse>
              </soap:Body>
            </soap:Envelope>
            """;

        using var adapter = CreateAdapter(soap);
        var items = await adapter.ListChildrenAsync("/");

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i is { Name: "Sales", Path: "/Sales", Type: CatalogItemType.Folder });
        Assert.Contains(items, i => i is { Name: "Revenue", Path: "/Sales/Revenue", Type: CatalogItemType.Report });
    }

    [Fact]
    public async Task GetParameters_parses_DefaultValues_Value_elements_from_ReportService2010()
    {
        // ItemParameter.DefaultValues is [XmlArrayItem("Value")], not Serialization Arrays <string>.
        // Mis-parsing left defaults empty so the viewer forced NULL on nullable DateTime params.
        const string soap = """
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <GetItemParametersResponse xmlns="http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer">
                  <Parameters>
                    <ItemParameter>
                      <Name>StartDate</Name>
                      <ParameterTypeName>DateTime</ParameterTypeName>
                      <Nullable>true</Nullable>
                      <AllowBlank>false</AllowBlank>
                      <MultiValue>false</MultiValue>
                      <PromptUser>true</PromptUser>
                      <Prompt>Start Date</Prompt>
                      <DefaultValues>
                        <Value>2026-10-14T22:25:13</Value>
                      </DefaultValues>
                      <Dependencies />
                    </ItemParameter>
                    <ItemParameter>
                      <Name>EndDate</Name>
                      <ParameterTypeName>DateTime</ParameterTypeName>
                      <Nullable>true</Nullable>
                      <AllowBlank>false</AllowBlank>
                      <MultiValue>false</MultiValue>
                      <PromptUser>true</PromptUser>
                      <Prompt>End Date</Prompt>
                      <DefaultValues>
                        <Value></Value>
                      </DefaultValues>
                      <Dependencies>
                        <Value>StartDate</Value>
                      </Dependencies>
                    </ItemParameter>
                  </Parameters>
                </GetItemParametersResponse>
              </soap:Body>
            </soap:Envelope>
            """;

        using var adapter = CreateAdapter(soap);
        var parameters = await adapter.GetParametersAsync(
            "/Example Reports/Execution Log",
            forRendering: true);

        var start = Assert.Single(parameters, p => p.Name == "StartDate");
        Assert.Equal(["2026-10-14T22:25:13"], start.DefaultValues);
        Assert.True(start.Nullable);

        var end = Assert.Single(parameters, p => p.Name == "EndDate");
        Assert.Empty(end.DefaultValues);
        Assert.Equal(["StartDate"], end.Dependencies);
    }

    [Fact]
    public void ToDeviceInfoXml_emits_escaped_FindString_when_set()
    {
        var xml = SoapSsrsAdapter.ToDeviceInfoXml(new DeviceInfo(
            HtmlFragment: false,
            Toolbar: false,
            FindString: "log & <hit>"));

        Assert.Contains("<FindString>log &amp; &lt;hit&gt;</FindString>", xml, StringComparison.Ordinal);
        Assert.Contains("<Toolbar>false</Toolbar>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void ToDeviceInfoXml_omits_FindString_when_null_or_whitespace()
    {
        var nullXml = SoapSsrsAdapter.ToDeviceInfoXml(new DeviceInfo(FindString: null));
        var blankXml = SoapSsrsAdapter.ToDeviceInfoXml(new DeviceInfo(FindString: "   "));

        Assert.DoesNotContain("<FindString>", nullXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<FindString>", blankXml, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseFindStringPage_prefers_PageNumber_like_ReportExecution2005_wsdl()
    {
        const string soap = """
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <FindStringResponse xmlns="http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices">
                  <PageNumber>2</PageNumber>
                </FindStringResponse>
              </soap:Body>
            </soap:Envelope>
            """;

        var page = SoapSsrsAdapter.ParseFindStringPage(System.Xml.Linq.XDocument.Parse(soap));

        Assert.Equal(2, page);
    }

    [Fact]
    public void ParseFindStringPage_falls_back_to_FindStringResult()
    {
        const string soap = """
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <FindStringResponse xmlns="http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices">
                  <FindStringResult>3</FindStringResult>
                </FindStringResponse>
              </soap:Body>
            </soap:Envelope>
            """;

        var page = SoapSsrsAdapter.ParseFindStringPage(System.Xml.Linq.XDocument.Parse(soap));

        Assert.Equal(3, page);
    }

    [Fact]
    public async Task ListChildren_maps_item_not_found_fault_to_CatalogPathNotFoundException()
    {
        const string soap = """
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <soap:Fault>
                  <faultcode>soap:Client</faultcode>
                  <faultstring>ItemNotFoundException</faultstring>
                  <detail>
                    <ErrorCode>rsItemNotFound</ErrorCode>
                  </detail>
                </soap:Fault>
              </soap:Body>
            </soap:Envelope>
            """;

        using var adapter = CreateAdapter(soap);
        await Assert.ThrowsAsync<CatalogPathNotFoundException>(() => adapter.ListChildrenAsync("/Missing"));
    }

    private static SoapSsrsAdapter CreateAdapter(string responseXml, CapturingSoapHandler? handler = null)
    {
        var options = Options.Create(new SsrsOptions
        {
            ReportServerBaseUrl = "http://ssrs.example/ReportServer",
            Username = "svc",
            Password = "secret",
            Domain = "CONTOSO",
            TimeoutSeconds = 30,
            MaxMessageSizeBytes = 1024 * 1024,
        });

        handler ??= new CapturingSoapHandler(responseXml);
        var transport = new SsrsSoapTransport(options, handler);
        return new SoapSsrsAdapter(options, transport);
    }

    private sealed class CapturingSoapHandler(string responseXml) : HttpMessageHandler
    {
        public string? LastRequestXml { get; private set; }
        public string? LastSoapAction { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastSoapAction = request.Headers.TryGetValues("SOAPAction", out var values)
                ? values.FirstOrDefault()
                : null;

            if (request.Content is not null)
            {
                LastRequestXml = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseXml, Encoding.UTF8, "text/xml"),
            };
        }
    }
}
