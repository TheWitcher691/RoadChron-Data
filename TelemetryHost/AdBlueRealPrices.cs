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
    internal sealed record Fx(DateTime Fetched, DateTime Date, Dictionary<string, decimal> Rates);
    internal sealed record Quote(string Country, string Currency, decimal Local, DateTime Checked, string Source, DateTime? Published=null);
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
    internal static bool ValidFx(Fx? fx, DateTime now) => fx != null && fx.Fetched <= now.AddMinutes(5) && fx.Fetched >= now.AddDays(-30)
        && fx.Date.Date <= now.Date && fx.Date.Date >= now.Date.AddDays(-30) && RealFuelPrices.ValidRates(fx.Rates)
        && fx.Rates.ContainsKey("PLN") && fx.Rates.ContainsKey("CZK") && fx.Rates.ContainsKey("CAD") && fx.Rates.ContainsKey("MXN");
    internal static async Task<Fx?> GetFxAsync(string root, CancellationToken ct)
    {
        await FxGate.WaitAsync(ct);
        try {
            var path=Path.Combine(root,FxCacheName); var cached=Load<Fx>(path); var now=DateTime.UtcNow;
            bool valid=ValidFx(cached,now);
            if(valid && now-cached!.Fetched<TimeSpan.FromHours(24))return cached;
            if (PublicDataHttp.Offline(root)) return valid?cached:null;
#if ROADCHRON_COLLECTOR
            var attemptPath=Path.Combine(root,"ecb-attempt.json");
            if(DefRealPrices.NextRefresh(Load<DefRealPrices.RefreshAttempt>(attemptPath),now)>now)return valid?cached:null;
            Save(attemptPath,new DefRealPrices.RefreshAttempt(now));
            try { using var fxHttp=PublicDataHttp.Create(root,15,1024*1024); var fx=ParseFx(await fxHttp.GetStringAsync(FxUrl,ct),now); Save(path,fx);return fx; }
            catch(Exception ex) when(!ct.IsCancellationRequested){Log(root,"ECB offline: "+ex.Message);return valid?cached:null;}
#else
            var central=(await CentralPriceFeed.GetAsync(root,ct))?.Fx;
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
    internal static Dictionary<string,CountryPrice> Compute(IEnumerable<Quote> quotes, Fx? fx, DateTime now)
    {
        var observations=quotes.Where(q=>q is { Country: not null, Currency: not null }&&q.Local>0&&q.Local<=5000&&q.Checked<=now.AddMinutes(5)&&q.Checked>=now.AddDays(-7)&&(!q.Published.HasValue||q.Published.Value.Date>=now.Date.AddDays(-14))&&Neighbours.ContainsKey(q.Country)).GroupBy(q=>q.Country).Select(g=>new {Country=g.Key, Prices=g.Select(q=> {
            decimal rate=1; if(q.Currency!="EUR"&&(fx==null||!fx.Rates.TryGetValue(q.Currency,out rate)||rate<=0))return (Price:0m,Quote:q);
            var eur=q.Local/rate;return (Price:eur>=.05m&&eur<=5m?eur:0m,Quote:q);
        }).Where(p=>p.Price>0).ToArray()}).Where(g=>g.Prices.Length>0).ToDictionary(g=>g.Country,g=>{
            var fresh=g.Prices.Where(p=>p.Quote.Checked>=now.AddHours(-24)&&(!p.Quote.Published.HasValue||p.Quote.Published.Value.Date>=now.Date.AddDays(-1))).ToArray();
            var used=fresh.Length>0?fresh:g.Prices;
            return new CountryPrice(used.Average(p=>p.Price),fresh.Length>0?"public":"cached",string.Join("; ",used.Select(p=>p.Quote.Source)));
        },StringComparer.OrdinalIgnoreCase);
        var result=new Dictionary<string,CountryPrice>(observations,StringComparer.OrdinalIgnoreCase);
        var average=observations.Count>0?new CountryPrice(observations.Values.Average(p=>p.Eur),"europe","available countries: "+string.Join(",",observations.Keys)):new CountryPrice(2m/4.45m,"test","no public data; 0.9.1 fallback");
        result["EU"]=average;
        foreach(var (country,adjacent) in Neighbours) {
            if(result.ContainsKey(country))continue;
            var nearby=adjacent.Split(' ',StringSplitOptions.RemoveEmptyEntries).Where(observations.ContainsKey).Distinct().ToArray();
            result[country]=nearby.Length>0?new(nearby.Average(n=>observations[n].Eur),"neighbours",string.Join(",",nearby)):average;
        }
        return result;
    }
    internal static T? Load<T>(string path) where T:class {try{if(new FileInfo(path).Length>4*1024*1024)return null;return JsonSerializer.Deserialize<T>(File.ReadAllText(path));}catch{return null;}}
    internal static Dictionary<string,CountryPrice> PricesFromCache(IEnumerable<Quote> quotes,Fx? fx,DateTime now)
    {
        var rows=quotes.Where(q=>q!=null).ToArray();
        var prices=Compute(rows,fx,now);
        if(prices["EU"].Kind!="test")return prices;
        var older=rows.Where(q=>q.Checked>=now.AddDays(-30)&&q.Checked<=now).ToArray();
        if(older.Length==0)return prices;
        // Value conversion and regional weighting use the last observed day.
        // Keep estimated regional prices distinguishable during an offline month.
        var historical=Compute(older,fx,older.Max(q=>q.Checked));
        return historical.ToDictionary(p=>p.Key,p=>p.Value.Kind=="public"?p.Value with {Kind="cached"}:p.Value);
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
            if(p.Kind is "public" or "cached")prices[key]=p with {Kind="cached"};
            else if(p.Kind is "neighbours" or "europe" or "offline")prices[key]=p;
        }
        legacy=true; return prices;
    }
    internal static void Atomic(string path,string data) {Directory.CreateDirectory(Path.GetDirectoryName(path)!);string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllText(tmp,data,new UTF8Encoding(false));File.Move(tmp,path,true);}finally{if(File.Exists(tmp))File.Delete(tmp);}}
    private static void Save<T>(string path,T data)=>Atomic(path,JsonSerializer.Serialize(data));
    private static void Log(string root,string message)=>RoadChronBoundedFileAppender.AppendLine(Path.Combine(root,"adblue-prices.log"),$"[{DateTime.UtcNow:O}] {message}");
    private static string Bridge(CountryPrice p,string code)=>$"RCADBLUE1 {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} {p.Eur.ToString("0.000000",Inv)} {code} {p.Kind}\n";
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
        using var http=PublicDataHttp.Create(root,20,24*1024*1024);
        var quotesPath=Path.Combine(dir,"adblue-quotes.json"); var quotes=Load<List<Quote>>(quotesPath)??[]; var fx=Load<Fx>(Path.Combine(root,FxCacheName));
        if(!ValidFx(fx,DateTime.UtcNow))fx=null;
        var snapshot=Path.Combine(dir,"adblue-countries.json");
        var prices=LoadRegionalPrices(snapshot,quotes,fx,DateTime.UtcNow,out _);
        var nextEvaluation=DateTime.MinValue;
        var attemptPath=Path.Combine(dir,"adblue-attempt-central-v1.json");
        DateTime nextUpdate=DefRealPrices.NextRefresh(Load<DefRealPrices.RefreshAttempt>(attemptPath),DateTime.UtcNow),nextLookup=DateTime.MinValue; string? country=null; double lastX=double.NaN,lastZ=double.NaN;
        Task<(List<Quote>,Fx?)>? refresh=null;
        try {
            while(!ct.IsCancellationRequested){
                try {
                    if(!File.Exists(mod)||File.Exists(Path.Combine(root,"disabled.txt"))){country=null;if(File.Exists(livePath))File.Delete(livePath);await Task.Delay(5000,ct);continue;}
                    var now=DateTime.UtcNow;
                    if(refresh==null&&now>=nextUpdate&&!PublicDataHttp.Offline(root)){Save(attemptPath,new DefRealPrices.RefreshAttempt(now));nextUpdate=now.AddHours(24);refresh=RefreshAsync(root,dir,quotes,fx,http,ct);}
                    if(refresh?.IsCompleted==true){
                        try{(quotes,fx)=await refresh;nextEvaluation=DateTime.MinValue;}finally{refresh=null;}
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
        }catch(OperationCanceledException) when(ct.IsCancellationRequested){}finally{try{File.Delete(livePath);}catch{}if(refresh!=null)try{await refresh;}catch{}}
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
