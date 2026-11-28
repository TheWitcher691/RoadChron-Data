using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RoadChron;

// ETS2 only. Public data is never executed; only bounded HTTPS responses are parsed.
internal static class AdBlueRealPrices
{
    internal const string FxUrl = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly SemaphoreSlim FxGate = new(1);
    internal const string SupplementFxUrl = "https://api.frankfurter.dev/v2/rates?base=EUR&quotes=ALL,BAM,MKD,RSD,RUB";
    internal static readonly string[] SupplementCurrencies = ["ALL","BAM","MKD","RSD","RUB"];
    internal sealed record Fx(DateTime Fetched, DateTime Date, Dictionary<string, decimal> Rates, Dictionary<string,DateTime>? SupplementDates=null);
    internal sealed record SupplementFx(Dictionary<string,decimal> Rates,Dictionary<string,DateTime> Dates);
    internal sealed record Quote(string Country, string Currency, decimal Local, DateTime Checked, string Source, DateTime? Published=null, int? Samples=null);
    internal sealed record CountryPrice(decimal Eur, string Kind, string Source);
    internal sealed record Source(string Country, string Currency, string Url, string Pattern, string? DatePattern=null);
    internal static readonly Source[] Sources = [
        new("L","EUR","https://www.aral.de/fr_lu/luxembourg/home/carburants-et-prix/prix-actuels-des-carburants.html",
            @"\bAdblue\s*\*\*\s+(\d{1,2}[,.]\d{2,3})\s*€\s+\d{1,2}[,.]\d{2,3}\s*€\s+\d+%",@"Prix valables à compter du (\d{2}\.\d{2}\.\d{4})"),
        new("DK","DKK","https://www.ok.dk/erhverv/produkter/braendstof/dieselpriser",
            @"\bAdBlue\s+(?:\d+[,.]\d+ kr\.\s+){0,3}Ekskl\. afgifter\s+Ekskl\. moms\s+\d+[,.]\d+ kr\.\s+Inkl\. moms\s+(\d+[,.]\d+) kr\.\s+AdBlue er", @"OK's truckstationer[^.]{0,100}\..{0,650}?Priserne er pr\. liter inkl\. moms gældende d\.\s*(\d{2}\.\d{2}\.\d{4})"),
        new("PL", "PLN", "https://www.dspaliwa.wielun.pl/ceny/", @"\bAdBlue\s+Roztwór mocznika \(SCR\)\s+(\d{1,3}[,.]\d{2,3})\s*zł\s*/\s*l\b"),
        new("CZ", "CZK", "https://www.has-cs.cz/cerpaci-stanice", @"\bAD\s*Blue\s+(\d{1,3}[,.]\d{2,3})\s*Kč\s+Cena za 1 l"),
        new("CZ", "CZK", "https://www.zdar.cz/cerpaci-stanice", @"\bAdBlue\s+(\d{1,3}[,.]\d{2,3})\s*Kč"),
        new("D", "EUR", "https://www.nates.de/tankstelle/", @"\bAdBlue\s+(\d{1,3}[,.]\d{2,3})\s*€\s*/\s*Liter"),
        new("A", "EUR", "https://www.hoffelner-linz.at/tankstellenfinder-49168.html", @"\bAdBlue\s*:\s*€\s*(\d{1,3}[,.]\d{2,3})"),
        new("A", "EUR", "https://www.avia-moser.at/autopflege/tankstellenfinder-avia-wash-49686.html", @"\bAdBlue\s*:\s*€\s*(\d{1,3}[,.]\d{2,3})")
    ];
    internal const string SpainUrl = "https://sedeaplicaciones.minetur.gob.es/ServiciosRESTCarburantes/PreciosCarburantes/EstacionesTerrestres/";
    internal const string SpainProductUrl = "https://energia.serviciosmin.gob.es/ServiciosRestCarburantes/PreciosCarburantes/EstacionesTerrestres/FiltroProducto/26";
    internal const string RomaniaUrl = "https://pretcarburant.ro/api/v1/statii?tip=adblue";
    // Optional catalogue prices: enable network collection only after written permission.
    internal static readonly (string Country,string Currency,string Url)[] VatSources = [
        ("LV","EUR","https://vatservice.eu/getprice/get_price_lv_AdB.php"),
        ("LT","EUR","https://vatservice.eu/getprice/get_price_lt_AdB.php"),
        ("DK","DKK","https://vatservice.eu/getprice/get_price_dk_AdB.php"),
        ("L","EUR","https://vatservice.eu/getprice/get_price_lux_AdB.php"),
        ("B","EUR","https://vatservice.eu/getprice/get_price_be_AdB.php"),
        ("S","SEK","https://vatservice.eu/getprice/get_price_se_AdB.php")
    ];
    internal static bool VatEnabled => Environment.GetEnvironmentVariable("ROADCHRON_VATSERVICE_APPROVED")=="true";
    internal static bool VatQuote(Quote q) => VatSources.Any(s=>s.Country==q.Country&&s.Currency==q.Currency&&s.Url==q.Source);
    internal static Quote? ParseVat(string text,(string Country,string Currency,string Url) source,Fx? fx,DateTime now) {
        if(text.Length>4096)return null;
        // A bare text response or simple presentation-only wrapper is accepted; never execute markup.
        var value=System.Net.WebUtility.HtmlDecode(Regex.Replace(text,@"</?(?:html|body|div|span|p|b|strong)\s*>"," ",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1))).Trim();
        var match=Regex.Match(value,@"\A([0-9]{1,3}[,.][0-9]{1,4})\s+([A-Z]{3})\z",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
        if(!match.Success||match.Groups[2].Value!=source.Currency||ParseLocal(match.Groups[1].Value) is not decimal local)return null;
        decimal rate=1m;
        if(source.Currency!="EUR"&&(fx==null||!fx.Rates.TryGetValue(source.Currency,out rate)||rate<=0))return null;
        if(local/rate is <.05m or >5m)return null;
        return new(source.Country,source.Currency,local,now,source.Url);
    }
    // Data provenance is separate from the network allowlist. Only these feeds are approved for publication.
    internal static bool ApprovedQuote(Quote q) =>
        (q.Country=="E" && q.Currency=="EUR" && (q.Source==SpainUrl||q.Source==SpainProductUrl)) ||
        (q.Country=="RO" && q.Currency=="RON" && q.Source==RomaniaUrl);
    // Tachograph country codes, land neighbours. No recursive estimates are used as observations.
    internal static readonly Dictionary<string,string> Neighbours = new(StringComparer.OrdinalIgnoreCase) {
        ["RKS"]="AL MNE SRB MK",
        ["A"]="D CZ SK H SLO I CH FL", ["AL"]="MNE SRB MK GR", ["AND"]="E F", ["ARM"]="GE AZ TR", ["AZ"]="ARM GE RUS TR", ["B"]="F NL D L", ["BG"]="RO SRB MK GR TR", ["BIH"]="HR SRB MNE", ["BY"]="PL LT LV RUS UA", ["CH"]="D F I A FL", ["CY"]="", ["CZ"]="D PL SK A", ["D"]="DK PL CZ A CH F L B NL", ["DK"]="D", ["E"]="P F AND", ["EST"]="LV RUS", ["F"]="B L D CH I MC AND E", ["FIN"]="S N RUS", ["FO"]="", ["GE"]="RUS TR ARM AZ", ["GR"]="AL MK BG TR", ["H"]="A SK UA RO SRB HR SLO", ["HR"]="SLO H SRB BIH MNE", ["I"]="F CH A SLO RSM V", ["IRL"]="UK", ["IS"]="", ["KZ"]="RUS", ["L"]="B F D", ["LT"]="LV BY PL RUS", ["LV"]="EST RUS BY LT", ["M"]="", ["MC"]="F", ["MD"]="RO UA", ["MK"]="SRB AL GR BG", ["MNE"]="HR BIH SRB AL", ["N"]="S FIN RUS", ["NL"]="D B", ["P"]="E", ["PL"]="D CZ SK UA BY LT RUS", ["RO"]="H SRB BG UA MD", ["RSM"]="I", ["RUS"]="N FIN EST LV LT PL BY UA GE AZ KZ", ["S"]="N FIN", ["SK"]="CZ A H UA PL", ["SLO"]="I A H HR", ["SRB"]="H RO BG MK AL MNE BIH HR", ["TR"]="GR BG GE ARM AZ", ["UA"]="PL SK H RO MD BY RUS", ["UK"]="IRL", ["V"]="I", ["FL"]="CH A"
    };
    internal const string FxCacheName = "ecb-eur-rates.json";
    internal static Fx ParseFx(string xml, DateTime now)
    {
        try {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            var doc = XDocument.Load(reader);
            XNamespace ns = "http://www.ecb.int/vocabulary/2002-08-01/eurofxref";
            var day = doc.Descendants(ns + "Cube").Where(e => e.Attribute("time") != null).Single();
            if (!DateTime.TryParseExact((string?)day.Attribute("time"), "yyyy-MM-dd", Inv, DateTimeStyles.None, out var date)
                || date.Date > now.Date || date.Date < now.Date.AddDays(-7)) throw new InvalidDataException("Invalid ECB date");
            var rates = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["EUR"] = 1m };
            foreach (var row in day.Elements(ns + "Cube")) {
                var code = (string?)row.Attribute("currency") ?? "";
                if (!Regex.IsMatch(code,"^[A-Z]{3}$") || !decimal.TryParse((string?)row.Attribute("rate"), NumberStyles.AllowDecimalPoint, Inv, out var rate)
                    || rate < .000001m || rate > 1000000m || !rates.TryAdd(code,rate)) throw new InvalidDataException("Invalid or duplicate ECB rate");
            }
            if (!ValidFx(new(now,date,rates),now)) throw new InvalidDataException("Incomplete ECB rates");
            return new(now,date,rates);
        } catch (Exception ex) when (ex is XmlException or InvalidOperationException) { throw new InvalidDataException("Invalid ECB XML",ex); }
    }
    // Frankfurter's blended EUR rates, not the underlying individual provider feeds.
    internal static SupplementFx ParseSupplementFx(string json,DateTime now)
    {
        if(json.Length>128*1024)throw new InvalidDataException("Supplementary FX response too large.");
        using var doc=JsonDocument.Parse(json);
        if(doc.RootElement.ValueKind!=JsonValueKind.Array||doc.RootElement.GetArrayLength()>SupplementCurrencies.Length)
            throw new InvalidDataException("Invalid supplementary FX response.");
        var rates=new Dictionary<string,decimal>(StringComparer.Ordinal);
        var dates=new Dictionary<string,DateTime>(StringComparer.Ordinal);
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var row in doc.RootElement.EnumerateArray()) {
            var code=row.GetProperty("quote").GetString()??"";
            if(row.GetProperty("base").GetString()!="EUR"||!SupplementCurrencies.Contains(code,StringComparer.Ordinal)||!seen.Add(code)||
                !row.GetProperty("rate").TryGetDecimal(out var rate)||rate is <.000001m or >1000000m||
                !DateTime.TryParseExact(row.GetProperty("date").GetString(),"yyyy-MM-dd",Inv,DateTimeStyles.None,out var date)||date.Date>now.Date)
                throw new InvalidDataException("Invalid or duplicate supplementary FX rate.");
            if(date.Date<now.Date.AddDays(-14))continue;
            rates.Add(code,rate);dates.Add(code,date.Date);
        }
        if(rates.Count==0)throw new InvalidDataException("No current supplementary FX rates.");
        return new(rates,dates);
    }
    internal static Fx MergeSupplementFx(Fx ecb,SupplementFx? incoming,Fx? prior,DateTime now)
    {
        var rates=new Dictionary<string,decimal>(ecb.Rates,StringComparer.Ordinal);
        var dates=new Dictionary<string,DateTime>(StringComparer.Ordinal);
        foreach(var code in SupplementCurrencies) {
            if(rates.ContainsKey(code))continue; // ECB always has priority.
            var date=DateTime.MinValue;decimal rate=0;
            if(prior?.SupplementDates?.TryGetValue(code,out var oldDate)==true&&prior.Rates.TryGetValue(code,out var oldRate))
                (date,rate)=(oldDate,oldRate);
            if(incoming?.Dates.TryGetValue(code,out var newDate)==true&&incoming.Rates.TryGetValue(code,out var newRate)&&newDate>=date)
                (date,rate)=(newDate,newRate);
            if(date.Date<now.Date.AddDays(-14)||date.Date>ecb.Fetched.Date||rate is <.000001m or >1000000m)continue;
            rates[code]=rate;dates[code]=date.Date;
        }
        return ecb with {Rates=rates,SupplementDates=dates.Count==0?null:dates};
    }
    internal static Fx? UsableFx(Fx? fx,DateTime now)
    {
        if(!ValidFx(fx,now))return null;
        if(fx!.SupplementDates==null)return fx;
        var rates=new Dictionary<string,decimal>(fx.Rates,StringComparer.Ordinal);
        var dates=new Dictionary<string,DateTime>(fx.SupplementDates,StringComparer.Ordinal);
        foreach(var code in dates.Where(p=>p.Value.Date<now.Date.AddDays(-14)).Select(p=>p.Key).ToArray()) {rates.Remove(code);dates.Remove(code);}
        return fx with {Rates=rates,SupplementDates=dates.Count==0?null:dates};
    }
    internal static bool ValidFx(Fx? fx, DateTime now) => fx != null && fx.Fetched <= now.AddMinutes(5) && fx.Fetched >= now.AddDays(-30)
        && fx.Date.Date <= now.Date && fx.Date.Date >= now.Date.AddDays(-30) && RealFuelPrices.ValidRates(fx.Rates)
        && fx.Rates.ContainsKey("PLN") && fx.Rates.ContainsKey("CZK") && fx.Rates.ContainsKey("CAD") && fx.Rates.ContainsKey("MXN")
        && (fx.SupplementDates==null||fx.SupplementDates.All(p=>SupplementCurrencies.Contains(p.Key,StringComparer.Ordinal)&&fx.Rates.ContainsKey(p.Key)&&
            p.Value.Date==p.Value&&p.Value.Date<=fx.Fetched.Date&&p.Value.Date>=fx.Fetched.Date.AddDays(-14)));
    internal static async Task<Fx?> GetFxAsync(string root, CancellationToken ct)
    {
        await FxGate.WaitAsync(ct);
        try {
            var path=Path.Combine(root,FxCacheName); var now=DateTime.UtcNow; var cached=UsableFx(Load<Fx>(path),now);
            bool valid=ValidFx(cached,now);
            #if ROADCHRON_COLLECTOR
            if(valid && now-cached!.Fetched<TimeSpan.FromHours(24))return cached;
#endif
            if (PublicDataHttp.Offline(root)) return valid?cached:null;
#if ROADCHRON_COLLECTOR
            var attemptPath=Path.Combine(root,"ecb-attempt.json");
            if(DefRealPrices.NextRefresh(Load<DefRealPrices.RefreshAttempt>(attemptPath),now)>now)return valid?cached:null;
            Save(attemptPath,new DefRealPrices.RefreshAttempt(now));
            try { using var fxHttp=PublicDataHttp.Create(root,15,1024*1024); var fx=ParseFx(await fxHttp.GetStringAsync(FxUrl,ct),now); Save(path,fx);return fx; }
            catch(Exception ex) when(!ct.IsCancellationRequested){Log(root,"ECB offline: "+ex.Message);return valid?cached:null;}
#else
            var central=UsableFx((await CentralPriceFeed.GetAsync(root,ct))?.Fx,now);
            if(ValidFx(central,now)&&(!valid||central!.Date>=cached!.Date&&central.Fetched>=cached.Fetched)){Save(path,central);return central;}
            return valid?cached:null;
#endif
        } finally { FxGate.Release(); }
    }
    internal static decimal? ParseHtml(string html, Source source) => ParseHtml(html,source,DateTime.UtcNow,out _);
    internal static decimal? ParseHtml(string html, Source source,DateTime now,out DateTime? published)
    {
        // Remove script/style contents first. Only match a source-specific price with units.
        var text=Regex.Replace(html,@"<(script|style)\b[^>]*>.*?</\1\s*>"," ",RegexOptions.IgnoreCase|RegexOptions.Singleline,TimeSpan.FromSeconds(2));
        text=WebUtility.HtmlDecode(Regex.Replace(text,"<[^>]+>"," ",RegexOptions.None,TimeSpan.FromSeconds(2)));
        text=Regex.Replace(text,@"[\s\u200b]+"," ");
        published=null;
        if(source.DatePattern!=null){
            var date=Regex.Match(text,source.DatePattern,RegexOptions.IgnoreCase,TimeSpan.FromSeconds(2));
            if(!DateTime.TryParseExact(date.Groups[1].Value,"dd.MM.yyyy",Inv,DateTimeStyles.None,out var day)||day.Date>now.Date.AddDays(1)||day.Date<now.Date.AddDays(-14))return null;
            published=day;
        }
        var values=Regex.Matches(text,source.Pattern,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromSeconds(2)).Select(m=>ParseLocal(m.Groups[1].Value)).Where(x=>x.HasValue).Select(x=>x!.Value).Distinct().ToArray();
        return values.Length==0?null:values.Average();
    }
    private static decimal? ParseLocal(string? s) => decimal.TryParse(s?.Replace(',','.'),NumberStyles.AllowDecimalPoint,Inv,out var n)&&n>0&&n<=5000?n:null;
    internal static decimal? ParseSpain(string json, DateTime now) => ParseSpain(json,now,out _);
    internal static decimal? ParseSpain(string json, DateTime now,out DateTime? published)
    {
        published=null;
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
        if(!DateTime.TryParseExact(root.GetProperty("Fecha").GetString(),["dd/MM/yyyy H:mm:ss","dd/MM/yyyy HH:mm:ss"],Inv,DateTimeStyles.None,out var date)||date.Date<now.Date.AddDays(-7)||date.Date>now.Date.AddDays(1))return null;
        published=date;
        var prices=new List<decimal>();
        foreach(var row in root.GetProperty("ListaEESSPrecio").EnumerateArray()) {
            if(!row.TryGetProperty("Tipo Venta",out var v)||v.ValueKind!=JsonValueKind.String||v.GetString()?.Trim()!="P")continue;
            if((row.TryGetProperty("Precio Adblue",out var p)||row.TryGetProperty("Precio AdBlue",out p))&&p.ValueKind==JsonValueKind.String&&ParseLocal(p.GetString()) is decimal n&&n>=.05m&&n<=5m)prices.Add(n);
        }
        return prices.Count>=10?prices.Average():null;
    }
    internal static Quote? ParseSpainProduct(string json, DateTime now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("ResultadoConsulta").GetString() != "OK" ||
            !DateTime.TryParseExact(root.GetProperty("Fecha").GetString(), ["dd/MM/yyyy H:mm:ss","dd/MM/yyyy HH:mm:ss"],
                Inv, DateTimeStyles.None, out var snapshot) ||
            snapshot.Date < now.Date.AddDays(-7) || snapshot.Date > now.Date.AddDays(1)) return null;

        var stations = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in root.GetProperty("ListaEESSPrecio").EnumerateArray())
        {
            if (Field(row, "Tipo Venta")?.Trim() != "P" ||
                Field(row, "IDEESS") is not { Length: > 0 } id ||
                ParseLocal(Field(row, "PrecioProducto")) is not decimal price ||
                price is < .05m or > 5m) continue;
            if (!stations.TryAdd(id, price) && stations[id] != price) conflicts.Add(id);
        }

        foreach (var id in conflicts) stations.Remove(id);
        if (stations.Count < 10) return null;
        var prices = stations.Values.Order().ToArray();
        return new("E", "EUR", (prices[(prices.Length-1)/2] + prices[prices.Length/2])/2,
            now, SpainProductUrl, snapshot.Date, prices.Length);

        static string? Field(JsonElement row, string name) =>
            row.ValueKind==JsonValueKind.Object && row.TryGetProperty(name,out var value) &&
            value.ValueKind==JsonValueKind.String ? value.GetString() : null;
    }

    // PretCarburant public station sample, CC BY 4.0. Reject ambiguous, undated or stale observations.
    internal static Quote? ParseRomania(string json, DateTime now)
    {
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
        if(root.ValueKind!=JsonValueKind.Object||Text(root,"status")!="ok"||
           !root.TryGetProperty("statii",out var rows)||rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>150)return null;
        var stations=new Dictionary<string,(decimal Price,DateTime Date)>(StringComparer.Ordinal);
        var conflicts=new HashSet<string>(StringComparer.Ordinal);
        foreach(var row in rows.EnumerateArray()){
            if(row.ValueKind!=JsonValueKind.Object||Text(row,"tip")!="adblue"||
               Flag(row,"pret_expirat")||Flag(row,"nesigur")||Flag(row,"pret_incoerent"))continue;
            var id=Text(row,"id");var observed=Text(row,"observat_la");
            if(string.IsNullOrWhiteSpace(id)||id.Length>160||string.IsNullOrEmpty(observed)||
               !DateTimeOffset.TryParse(observed,Inv,DateTimeStyles.AssumeUniversal,out var stamp)||
               stamp.UtcDateTime.Date<now.Date.AddDays(-7)||stamp.UtcDateTime>now.AddDays(1)||
               !row.TryGetProperty("pret",out var value)||value.ValueKind!=JsonValueKind.Number||
               !value.TryGetDecimal(out var price)||price<.25m||price>25m)continue;
            if(stations.TryGetValue(id,out var old)&&old.Price!=price){conflicts.Add(id);continue;}
            if(!stations.TryGetValue(id,out old)||stamp.UtcDateTime>old.Date)stations[id]=(price,stamp.UtcDateTime);
        }
        foreach(var id in conflicts)stations.Remove(id);
        if(stations.Count<3)return null;
        return new("RO","RON",stations.Values.Average(v=>v.Price),now,RomaniaUrl,stations.Values.Min(v=>v.Date),stations.Count);
        static string? Text(JsonElement e,string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
        static bool Flag(JsonElement e,string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind!=JsonValueKind.False&&v.ValueKind!=JsonValueKind.Null;
    }
    internal static Dictionary<string,CountryPrice> Compute(IEnumerable<Quote> quotes, Fx? fx, DateTime now)
    {
        var observations=quotes.Where(q=>q is { Country: not null, Currency: not null }&&q.Local>0&&q.Local<=5000&&q.Checked<=now.AddMinutes(5)&&q.Checked>=now.AddDays(-30)&&(!q.Published.HasValue||q.Published.Value.Date>=now.Date.AddDays(-30))&&Neighbours.ContainsKey(q.Country)).GroupBy(q=>q.Country).Select(g=>new {Country=g.Key, Prices=g.Select(q=> {
            decimal rate=1; if(q.Currency!="EUR"&&(fx==null||!fx.Rates.TryGetValue(q.Currency,out rate)||rate<=0))return (Price:0m,Quote:q);
            var eur=q.Local/rate;return (Price:eur>=.05m&&eur<=5m?eur:0m,Quote:q);
        }).Where(p=>p.Price>0).ToArray()}).Where(g=>g.Prices.Length>0).ToDictionary(g=>g.Country,g=>{
            var fresh=g.Prices.Where(p=>p.Quote.Checked>=now.AddDays(ApprovedQuote(p.Quote)||VatQuote(p.Quote)?-7:-1)&&(!p.Quote.Published.HasValue||p.Quote.Published.Value.Date>=now.Date.AddDays(ApprovedQuote(p.Quote)||VatQuote(p.Quote)?-7:-1))).ToArray();
            // A station sample takes precedence over a catalogue price.
            var measured=fresh.Where(p=>!VatQuote(p.Quote)).ToArray();
            var used=measured.Length>0?measured:fresh.Length>0?fresh:g.Prices;
            var kind=fresh.Length==0?"cached":used.All(p=>VatQuote(p.Quote))?"catalogue":"public";
            return new CountryPrice(used.Average(p=>p.Price),kind,string.Join("; ",used.Select(p=>p.Quote.Source)));
        },StringComparer.OrdinalIgnoreCase);
        var result=new Dictionary<string,CountryPrice>(observations,StringComparer.OrdinalIgnoreCase);
        // Gross-price scenario factors, NOT observed national price ratios or VAT rates.
        // Recalibrate when adequate legal measurements become available.
        var measuredAnchors=observations.Where(p=>p.Value.Kind=="public").ToArray();
        var catalogAnchors=observations.Where(p=>p.Value.Kind=="catalogue").ToArray();
        var basis=measuredAnchors.Length>0?measuredAnchors:catalogAnchors.Length>0?catalogAnchors:observations.ToArray();
        // One country alone cannot recalibrate the entire continent.
        var dataWeight=Math.Min(1m,basis.Length/3m);
        var normalized=basis.Select(p=>p.Value.Eur/IndexFactor(p.Key)).OrderBy(x=>x).ToArray();
        decimal reference=normalized.Length==0?.95m:normalized.Length%2==1?normalized[normalized.Length/2]:(normalized[normalized.Length/2-1]+normalized[normalized.Length/2])/2m;
        reference=.95m+(reference-.95m)*dataWeight;
        string detail=normalized.Length==0?"RoadChron scenario baseline (no measurements)":"RoadChron anchors: "+string.Join(",",basis.Select(p=>p.Key).Order());
        result["EU"]=new(Math.Clamp(reference,.05m,5m),"index",detail);
        foreach(var country in Neighbours.Keys)
            if(!result.ContainsKey(country))result[country]=new(Math.Round(Math.Clamp(reference*IndexFactor(country),.05m,5m),4),"index",detail);
        return result;
    }
    internal static decimal IndexFactor(string country)=>country switch {
        "PL" or "RO" or "BG" or "MD" or "UA" or "AL" or "MK" or "RKS" or "BIH" or "SRB" or "BY" => .85m,
        "CZ" or "SK" or "H" or "LT" or "LV" or "EST" or "TR" or "MNE" or "RUS" => .95m,
        "DK" or "N" or "S" or "FIN" or "IS" or "FO" or "CH" or "FL" => 1.30m,
        "D" or "F" or "A" or "B" or "NL" or "L" or "UK" or "IRL" or "I" or "MC" or "V" or "RSM" => 1.15m,
        _=>1m
    };
    internal static T? Load<T>(string path) where T:class {try{if(new FileInfo(path).Length>4*1024*1024)return null;return JsonSerializer.Deserialize<T>(File.ReadAllText(path));}catch{return null;}}
    internal static Dictionary<string,CountryPrice> PricesFromCache(IEnumerable<Quote> quotes,Fx? fx,DateTime now)
    {
        return Compute(quotes,fx,now);
    }
    internal static Dictionary<string,CountryPrice> LoadRegionalPrices(string snapshot,List<Quote> quotes,Fx? fx,DateTime now,out bool legacy)
    {
        legacy=false;
        var prices=PricesFromCache(quotes,fx,now);
        if(quotes.Count!=0||!File.Exists(snapshot))return prices;
        var stamp=File.GetLastWriteTimeUtc(snapshot);
        if(stamp<now.AddDays(-30)||stamp>now.AddMinutes(5))return prices;
        var old=Load<Dictionary<string,CountryPrice>>(snapshot);
        if(old?.TryGetValue("EU",out var fallback)!=true||fallback is not {Eur: >=.05m and <=5m,Kind:"europe" or "offline"})return prices;
        foreach(var (key,p) in old){
            if(!prices.ContainsKey(key)||p is not {Eur: >=.05m and <=5m})continue;
            if(p.Kind is "public" or "cached" or "catalogue")prices[key]=p with {Kind="cached"};
            else if(p.Kind is "neighbours" or "europe" or "offline")prices[key]=p;
        }
        legacy=true; return prices;
    }
    internal static void Atomic(string path,string data) {Directory.CreateDirectory(Path.GetDirectoryName(path)!);string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllText(tmp,data,new UTF8Encoding(false));File.Move(tmp,path,true);}finally{if(File.Exists(tmp))File.Delete(tmp);}}
    private static void Save<T>(string path,T data)=>Atomic(path,JsonSerializer.Serialize(data));
    private static void Log(string root,string message)=>RoadChronBoundedFileAppender.AppendLine(Path.Combine(root,"adblue-prices.log"),$"[{DateTime.UtcNow:O}] {message}");
    private static string Bridge(CountryPrice p,string code)=>$"RCADBLUE1 {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} {p.Eur.ToString("0.000000",Inv)} {code} {(p.Kind=="index"?"europe":p.Kind=="catalogue"?"public":p.Kind)}\n";
    // Match the tachograph: a recognised local SCS node is a country suggestion.
    // Ambiguous or absent data use the explicitly labelled EU fallback.
    internal static string? SelectCountry(bool detected, string? code)
    {
        var normalized = code?.Trim().ToUpperInvariant();
        return detected && normalized != null && Neighbours.ContainsKey(normalized) ? normalized : null;
    }
    internal static async Task RunAsync(string dataRoot, ScsSharedMemoryReader reader, LocalScsRegionResolver resolver, CancellationToken ct)
    {
        var root=Path.Combine(dataRoot,"fuel-prices");var dir=Path.Combine(root,"ETS2");var livePath=Path.Combine(dir,"adblue-live.txt");var fallback=Path.Combine(dir,"adblue-fallback.txt");
        var mod=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Euro Truck Simulator 2","mod",RealFuelPrices.ModName);
        var quotesPath=Path.Combine(dir,"adblue-quotes.json"); var quotes=Load<List<Quote>>(quotesPath)??[]; var fx=Load<Fx>(Path.Combine(root,FxCacheName));
        if(!ValidFx(fx,DateTime.UtcNow))fx=null;
        var snapshot=Path.Combine(dir,"adblue-countries.json");
        var prices=LoadRegionalPrices(snapshot,quotes,fx,DateTime.UtcNow,out _);
        var nextEvaluation=DateTime.MinValue;
        DateTime nextLookup=DateTime.MinValue, nextCacheRead=DateTime.MinValue; string? country=null; double lastX=double.NaN,lastZ=double.NaN;
        var fxPath=Path.Combine(root,FxCacheName);
        DateTime lastQuoteWrite=DateTime.MinValue,lastFxWrite=DateTime.MinValue;
        try {
            while(!ct.IsCancellationRequested){
                try {
                    if(!File.Exists(mod)||File.Exists(Path.Combine(root,"disabled.txt"))){country=null;if(File.Exists(livePath))File.Delete(livePath);await Task.Delay(5000,ct);continue;}
                    var now=DateTime.UtcNow;
                    if(now>=nextCacheRead){
                        nextCacheRead=now.AddSeconds(5);
                        var quoteWrite=File.GetLastWriteTimeUtc(quotesPath);var fxWrite=File.GetLastWriteTimeUtc(fxPath);
                        if(quoteWrite!=lastQuoteWrite||fxWrite!=lastFxWrite){
                            quotes=Load<List<Quote>>(quotesPath)??quotes;fx=Load<Fx>(fxPath);
                            lastQuoteWrite=quoteWrite;lastFxWrite=fxWrite;nextEvaluation=DateTime.MinValue;
                        }
                    }
                    if(now>=nextEvaluation){
                        prices=LoadRegionalPrices(snapshot,quotes,ValidFx(fx,now)?fx:null,now,out var legacy);
                        if(!legacy)Save(snapshot,prices);
                        Atomic(fallback,Bridge(prices["EU"],"EU"));
                        nextEvaluation=now.AddMinutes(15);
                    }
                    if(!File.Exists(fallback))Atomic(fallback,Bridge(prices["EU"],"EU"));
                    var sample=reader.Read("ETS2");
                    if(!sample.Connected||!sample.GameRunning||!string.Equals(sample.GameName,"ETS2",StringComparison.OrdinalIgnoreCase)||sample.SpeedKmh is not double speed||Math.Abs(speed)>1||sample.PositionX is not double x||sample.PositionZ is not double z||!double.IsFinite(x)||!double.IsFinite(z)){
                        country=null;lastX=lastZ=double.NaN;if(File.Exists(livePath))File.Delete(livePath);
                    }else{
                        var moved=!double.IsFinite(lastX)||Math.Abs(x-lastX)>20||Math.Abs(z-lastZ)>20;
                        if(moved||now>=nextLookup){
                            country=null;
                            if(File.Exists(livePath))File.Delete(livePath);
                            var region=await resolver.ResolveAsync("ETS2",x,z,ct);
                            var check=reader.Read("ETS2");
                            bool positionMatches=check.Connected&&check.GameRunning&&string.Equals(check.GameName,"ETS2",StringComparison.OrdinalIgnoreCase)&&check.SpeedKmh is double v&&Math.Abs(v)<=1&&check.PositionX is double xx&&check.PositionZ is double zz&&Math.Abs(xx-x)<=20&&Math.Abs(zz-z)<=20;
                            if(positionMatches)country=SelectCountry(region.Detected,region.CountryCode);
                            lastX=x;lastZ=z;
                            nextLookup=DateTime.UtcNow.AddSeconds(country==null?10:300);
                            Log(root,$"AdBlue country: detected={region.Detected}; code={region.CountryCode ?? "-"}; area={region.Area}; positionMatches={positionMatches}; selected={country ?? "EU"}");
                            if(!positionMatches){await Task.Delay(2000,ct);continue;}
                        }
                        var selected=country??"EU";var price=prices.TryGetValue(selected,out var p)?p:prices["EU"];Atomic(livePath,Bridge(price,selected));
                    }
                }catch(Exception ex) when(!ct.IsCancellationRequested){Log(root,"AdBlue bridge: "+ex.Message);try{File.Delete(livePath);}catch{}}
                await Task.Delay(2000,ct);
            }
        }catch(OperationCanceledException) when(ct.IsCancellationRequested){}finally{try{File.Delete(livePath);}catch{}}
    }
    private static int _refreshActive;
    private static int _refreshProgress;
    internal static string Progress=>$"{Volatile.Read(ref _refreshProgress)}/1";
    internal static bool RefreshActive => System.Threading.Volatile.Read(ref _refreshActive)!=0;
    internal static async Task<(List<Quote>,Fx?)> RefreshAsync(string root,string dir,List<Quote> previous,Fx? fx,HttpClient http,CancellationToken ct){
        Volatile.Write(ref _refreshProgress,0);
        System.Threading.Volatile.Write(ref _refreshActive,1);
        try {
#if ROADCHRON_COLLECTOR
        fx=await GetFxAsync(root,ct)??fx;
        var quotes=previous.Where(q=>q is { Source.Length: > 0 }).GroupBy(q=>q.Source,StringComparer.Ordinal)
            .ToDictionary(g=>g.Key,g=>g.MaxBy(q=>q.Checked)!,StringComparer.Ordinal);
        // At most one attempt per source in each daily refresh; keep the last good observation.
        var covered=new HashSet<string>();
        foreach(var s in Sources){
            if(covered.Contains(s.Country)){Interlocked.Increment(ref _refreshProgress);continue;}
            try{
                var text=await http.GetStringAsync(s.Url,ct);var now=DateTime.UtcNow;
                if(ParseHtml(text,s,now,out var published) is decimal value){
                    quotes[s.Url]=new(s.Country,s.Currency,value,now,s.Url,published);covered.Add(s.Country);
                    Log(root,$"{s.Country}: {value} {s.Currency}/l {s.Url}");
                }else Log(root,"No current pump price: "+s.Url);
            }catch(Exception ex)when(!ct.IsCancellationRequested){Log(root,s.Url+": "+ex.Message);}
            finally { Interlocked.Increment(ref _refreshProgress); }
        }
        try{if(ParseSpain(await http.GetStringAsync(SpainUrl,ct),DateTime.UtcNow,out var published) is decimal eur)quotes[SpainUrl]=new("E","EUR",eur,DateTime.UtcNow,SpainUrl,published);}catch(Exception ex)when(!ct.IsCancellationRequested){Log(root,"Spain: "+ex.Message);}
        var result=CentralPriceFeed.MergeAdBlue([],quotes.Values,DateTime.UtcNow);Save(Path.Combine(dir,"adblue-quotes.json"),result);return(result,fx);
#else
        var central=await CentralPriceFeed.GetAsync(root,ct);
        fx=await GetFxAsync(root,ct)??fx;
        if(central==null)return(previous,fx);
        var result=CentralPriceFeed.MergeAdBlue(previous,central.AdBlue,DateTime.UtcNow);
        Save(Path.Combine(dir,"adblue-quotes.json"),result);
        Volatile.Write(ref _refreshProgress,1);
        return(result,fx);
#endif
        } finally { System.Threading.Volatile.Write(ref _refreshActive,0); }
    }
}
