using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace MusicAura.Core.Dlna;

public sealed class UpnpException : Exception
{
    public UpnpException(string action, int errorCode, string? description, int httpStatus)
        : base($"{action} failed: UPnP error {errorCode} {description} (HTTP {httpStatus})")
    {
        Action = action;
        ErrorCode = errorCode;
        ErrorDescription = description;
        HttpStatus = httpStatus;
    }

    public string Action { get; }

    public int ErrorCode { get; }

    public string? ErrorDescription { get; }

    public int HttpStatus { get; }
}

/// <summary>Minimal UPnP SOAP 1.1 action invoker.</summary>
public sealed class SoapClient
{
    public const string UserAgent = "Android/1 UPnP/1.0 DLNADOC/1.50 MusicAura/0.1";

    private readonly HttpClient _http;

    public SoapClient(HttpClient http)
    {
        _http = http;
    }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(6);

    public static string BuildEnvelope(string serviceType, string action, IEnumerable<KeyValuePair<string, string>> arguments)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
        sb.Append("<s:Body>");
        sb.Append("<u:").Append(action).Append(" xmlns:u=\"").Append(serviceType).Append("\">");
        foreach (var (name, value) in arguments)
        {
            sb.Append('<').Append(name).Append('>');
            sb.Append(SecurityElement.Escape(value));
            sb.Append("</").Append(name).Append('>');
        }

        sb.Append("</u:").Append(action).Append('>');
        sb.Append("</s:Body></s:Envelope>");
        return sb.ToString();
    }

    /// <summary>Parses the out-arguments of a successful response.</summary>
    public static IReadOnlyDictionary<string, string> ParseResponse(string xml, string action)
    {
        var doc = XDocument.Parse(xml);
        var responseName = action + "Response";
        var response = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == responseName)
            ?? throw new FormatException($"No {responseName} element in SOAP response.");
        return response.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value, StringComparer.OrdinalIgnoreCase);
    }

    public static (int Code, string? Description)? ParseFault(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var error = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "UPnPError");
            if (error is null)
            {
                return null;
            }

            var codeText = error.Elements().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value;
            var description = error.Elements().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value;
            return (int.TryParse(codeText, out var code) ? code : -1, description);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> InvokeAsync(
        Uri controlUrl,
        string serviceType,
        string action,
        IEnumerable<KeyValuePair<string, string>> arguments,
        CancellationToken cancellationToken = default)
    {
        var body = BuildEnvelope(serviceType, action, arguments);
        using var request = new HttpRequestMessage(HttpMethod.Post, controlUrl);
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        // Several TV firmwares only accept the quoted charset form.
        content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=\"utf-8\"");
        request.Content = content;
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{serviceType}#{action}\"");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.ConnectionClose = false;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{action} timed out after {Timeout.TotalSeconds:0.#} s ({controlUrl}).");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var fault = ParseFault(text);
                throw new UpnpException(action, fault?.Code ?? -1, fault?.Description ?? response.ReasonPhrase, (int)response.StatusCode);
            }

            return ParseResponse(text, action);
        }
    }
}
