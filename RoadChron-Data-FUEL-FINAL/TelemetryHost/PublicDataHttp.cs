using System.Net;
using System.Text.RegularExpressions;

namespace RoadChron;

// Only public, read-only feeds. No cookies, authentication, redirect, referrer or app identifier.
// The server still sees the network's public IP (or configured proxy/VPN exit IP).
internal static class PublicDataHttp
{
    internal const string OfflineFile = "offline.txt";
    internal const string ConsentFile = "allow-public-downloads.txt";
    internal static bool Offline(string root) =>
        File.Exists(Path.Combine(root, OfflineFile)) || !File.Exists(Path.Combine(root, ConsentFile));

    internal static void SetDownloadsEnabled(string root, bool enabled)
    {
        string consent = Path.Combine(root, ConsentFile);
        if (enabled)
        {
            AdBlueRealPrices.Atomic(consent, "1\n");
            File.Delete(Path.Combine(root, OfflineFile));
        }
        else File.Delete(consent);
    }

    internal static HttpClient Create(string root, int seconds, int maxBytes, Uri? central = null) => new(new Gate(root, new SocketsHttpHandler {
        AllowAutoRedirect=false, UseCookies=false, Credentials=null, ActivityHeadersPropagator=null,
        DefaultProxyCredentials=null, AutomaticDecompression=DecompressionMethods.None }, central)) {
        Timeout = TimeSpan.FromSeconds(seconds), MaxResponseContentBufferSize = maxBytes };
    private static readonly HashSet<string> Feeds = new(
        new[] { RealFuelPrices.UsGasGaugeSource, RealFuelPrices.OpenVanSource, AdBlueRealPrices.FxUrl, AdBlueRealPrices.SpainUrl, DefRealPrices.PilotSource, AdBlueRealPrices.SpainProductUrl }
            .Concat(RealFuelPrices.EiaPages)
            .Concat(AdBlueRealPrices.Sources.Select(source => source.Url))
            .Select(url => new Uri(url).AbsoluteUri), StringComparer.Ordinal);
    private static readonly Regex DefCatalogPath = new(
        @"^/location/(?<state>[a-z]{2})/(?:[a-z0-9-]{1,160}/)?\z", RegexOptions.CultureInvariant);

    internal static bool Allowed(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true } || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        if (Feeds.Contains(uri.AbsoluteUri)) return true;
        if (uri.Host != "www.ta-petro.com" || uri.AbsolutePath.Length > 180) return false;
        var match = DefCatalogPath.Match(uri.AbsolutePath);
        return match.Success && DefRealPrices.Neighbours.ContainsKey(match.Groups["state"].Value.ToUpperInvariant());
    }
    // Publisher policy is intentionally narrower than legacy provenance validation.
    internal static bool CollectorAllowed(Uri? uri) => uri is {IsAbsoluteUri:true} && (
        uri.AbsoluteUri == RealFuelPrices.UsGasGaugeRegular || uri.AbsoluteUri == RealFuelPrices.UsGasGaugeDiesel || uri.AbsoluteUri == RealFuelPrices.OpenVanPrices || uri.AbsoluteUri == RealFuelPrices.OpenVanFx || uri.AbsoluteUri == new Uri(AdBlueRealPrices.SupplementFxUrl).AbsoluteUri ||
#if ROADCHRON_COLLECTOR
        global::EiaWorkbooks.Urls.Contains(uri.AbsoluteUri, StringComparer.Ordinal) ||
#endif
        (AdBlueRealPrices.VatEnabled&&AdBlueRealPrices.VatSources.Any(s=>s.Url==uri.AbsoluteUri))||uri.AbsoluteUri==new Uri(AdBlueRealPrices.RomaniaUrl).AbsoluteUri||(Allowed(uri) &&
        (uri.AbsoluteUri == AdBlueRealPrices.FxUrl || uri.AbsoluteUri == AdBlueRealPrices.SpainProductUrl)));
    internal static bool AllowedSource(string? source) => Uri.TryCreate(source,UriKind.Absolute,out var uri)&&Allowed(uri);
    // Read-only historical provenance for the existing signed dataset; NEVER used by the HTTP gate.
    internal static bool LegacyFuelProvenance(string? source) => source is
        "https://gasprices.aaa.com/todays-state-averages/" or "https://www.euenergyprices.eu/en/fuel" or
        "https://www.amzs.si/na-poti/cene-goriv-po-evropi" or
        "https://www.gov.uk/government/statistics/weekly-road-fuel-prices" or
        "https://www.ssb.no/en/statbank/table/09654" ||
        RealFuelPrices.EuropeanCountryCodes.Values.Any(c=>source=="https://www.euenergyprices.eu/en/fuel/"+c);
    // Allowed() describes source provenance; only the collector build may contact those sources.
    internal sealed class Gate(string root, HttpMessageHandler transport, Uri? central = null) : DelegatingHandler(transport)
    {
        private void Validate(HttpRequestMessage request)
        {
            if (Offline(root)) throw new HttpRequestException("Public downloads disabled (offline mode).");
            bool allowed = central != null && CentralPriceFeed.Endpoint(central.AbsoluteUri) != null && request.RequestUri?.AbsoluteUri == central.AbsoluteUri;
#if ROADCHRON_COLLECTOR
            if (central == null) allowed = CollectorAllowed(request.RequestUri);
#endif
            if (request.Method != HttpMethod.Get || request.Content != null || !allowed)
                throw new HttpRequestException("Public data request blocked by privacy policy.");
            request.Headers.Clear();
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            Validate(request);
            return base.Send(request, ct);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Validate(request);
            return base.SendAsync(request, ct);
        }
    }
}
