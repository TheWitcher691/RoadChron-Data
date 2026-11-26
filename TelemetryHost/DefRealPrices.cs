using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoadChron;

// ATS only: public TA/Petro pump-price samples, in USD per US gallon.
internal static class DefRealPrices
{
    internal const string Source = "https://www.ta-petro.com/";
    internal const string PilotSource = "https://pilotcompany.com/fuel-prices";
    internal const decimal LitresPerGallon = 3.785411784m;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    internal sealed record Observation(decimal UsdPerGallon, DateTime Checked, string[] Sources, int Samples=0);
    internal sealed record Price(decimal UsdPerGallon, string Kind, int Samples);
    internal sealed record RefreshAttempt(DateTime StartedUtc);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);
    internal static readonly Dictionary<string,string> Neighbours = new(StringComparer.Ordinal) {
        ["AL"]="FL GA TN MS", ["AK"]="", ["AZ"]="CA NV UT CO NM", ["AR"]="MO TN MS LA TX OK", ["CA"]="OR NV AZ", ["CO"]="WY NE KS OK NM AZ UT", ["CT"]="NY MA RI", ["DE"]="MD PA NJ", ["DC"]="MD VA", ["FL"]="GA AL", ["GA"]="FL AL TN NC SC", ["HI"]="", ["ID"]="WA OR NV UT WY MT", ["IL"]="WI IA MO KY IN", ["IN"]="MI OH KY IL", ["IA"]="MN WI IL MO NE SD", ["KS"]="NE MO OK CO", ["KY"]="IL IN OH WV VA TN MO", ["LA"]="TX AR MS", ["ME"]="NH", ["MD"]="VA WV PA DE DC", ["MA"]="RI CT NY VT NH", ["MI"]="WI IN OH", ["MN"]="ND SD IA WI", ["MS"]="LA AR TN AL", ["MO"]="IA IL KY TN AR OK KS NE", ["MT"]="ID WY SD ND", ["NE"]="SD IA MO KS CO WY", ["NV"]="CA OR ID UT AZ", ["NH"]="ME MA VT", ["NJ"]="NY PA DE", ["NM"]="AZ CO OK TX UT", ["NY"]="PA NJ CT MA VT", ["NC"]="VA TN GA SC", ["ND"]="MT SD MN", ["OH"]="MI PA WV KY IN", ["OK"]="CO KS MO AR TX NM", ["OR"]="WA ID NV CA", ["PA"]="NY NJ DE MD WV OH", ["RI"]="CT MA", ["SC"]="NC GA", ["SD"]="ND MN IA NE WY MT", ["TN"]="KY VA NC GA AL MS AR MO", ["TX"]="NM OK AR LA", ["UT"]="ID WY CO NM AZ NV", ["VT"]="NY NH MA", ["VA"]="NC TN KY WV MD DC", ["WA"]="ID OR", ["WV"]="OH PA MD VA KY", ["WI"]="MI MN IA IL", ["WY"]="MT SD NE CO UT ID"
    };
    internal static string? SelectState(bool detected,string? code) {
        var s=code?.Trim().ToUpperInvariant();return detected&&s!=null&&Neighbours.ContainsKey(s)?s:null;
    }
    internal static string[] StationUrls(string html,string state) {
        if(!Neighbours.ContainsKey(state))return [];
        var prefix="/location/"+state.ToLowerInvariant()+"/";
        var result=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(Match m in Regex.Matches(html,@"\bhref\s*=\s*['""](?<url>[^'""\s<>]+)['""]",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(2))) {
            if(!Uri.TryCreate(new Uri(Source),WebUtility.HtmlDecode(m.Groups["url"].Value),out var uri)||
               uri.Scheme!="https"||uri.Host!="www.ta-petro.com"||!uri.IsDefaultPort||
               uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0)continue;
            var p=uri.AbsolutePath.ToLowerInvariant();
            if(p.StartsWith(prefix,StringComparison.Ordinal)&&Regex.IsMatch(p[prefix.Length..],@"^[a-z0-9-]{1,160}/?$"))result.Add(Source.TrimEnd('/')+p.TrimEnd('/')+"/");
        }
        return result.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
    }
    internal static decimal? ParseStation(string html) {
        var text=Regex.Replace(html,@"<(script|style)\b[^>]*>.*?</\1\s*>"," ",RegexOptions.IgnoreCase|RegexOptions.Singleline,TimeSpan.FromSeconds(2));
        text=WebUtility.HtmlDecode(Regex.Replace(text,"<[^>]+>"," ",RegexOptions.None,TimeSpan.FromSeconds(2)));
        text=Regex.Replace(text,@"[\s\u200b]+"," ");
        int pos=text.IndexOf("Current Fuel Prices",StringComparison.OrdinalIgnoreCase);if(pos<0)return null;
        var section=text[pos..Math.Min(text.Length,pos+2500)];
        var values=Regex.Matches(section,@"\$(\d{1,2}\.\d{2,3})\s+DEF\b",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromSeconds(2))
            .Select(m=>decimal.TryParse(m.Groups[1].Value,NumberStyles.AllowDecimalPoint,Inv,out var p)?p:0m).Where(Valid).Distinct().ToArray();
        return values.Length==1?values[0]:null;
    }
    private static bool Valid(decimal price)=>price>=.5m&&price<=15m;
    internal static Dictionary<string,Observation> ParsePilot(string html,DateTime checkedUtc)
    {
        var states = new Dictionary<string,List<decimal>>();
        var ids = new HashSet<string>();
        // The operator embeds the whole catalog in the page behind the paginated table.
        // Treat it as text, never execute scripts or follow station links.
        var stations=Regex.Matches(html,"\\{locationId:\"([0-9]{1,10})\",",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(2));
        for(int i=0;i<stations.Count;i++)
        {
            var start=stations[i].Index;
            var length=Math.Min(6000,(i+1<stations.Count?stations[i+1].Index:html.Length)-start);
            string block=html.Substring(start,length);
            var state=Regex.Match(block,"^\\{locationId:\"[0-9]+\",city:\"[^\"\\r\\n]{1,100}\",state:\"([A-Z]{2})\",country:\"United States\",interstate:\"[^\"\\r\\n]{0,300}\",currency:\"USD\",");
            if(!state.Success || !Neighbours.ContainsKey(state.Groups[1].Value))continue;
            var list=Regex.Match(block,@"\bfuelPrices:\[([^\]]{0,3000})\]");
            var prices=Regex.Matches(list.Groups[1].Value,"\\{description:\"DEF\",price:([0-9]{1,2}(?:\\.[0-9]{1,3})?)\\}");
            if(prices.Count!=1 || !decimal.TryParse(prices[0].Groups[1].Value,NumberStyles.AllowDecimalPoint,Inv,out var value)||!Valid(value)||!ids.Add(stations[i].Groups[1].Value)) continue;
            if(!states.TryGetValue(state.Groups[1].Value,out var samples)) states[state.Groups[1].Value]=samples=[];
            samples.Add(value);
        }
        return states.ToDictionary(p=>p.Key,p=>new Observation(p.Value.Average(),checkedUtc,[PilotSource],p.Value.Count));
    }
    private static readonly Dictionary<string,string> StateNames = ("AL:Alabama|AK:Alaska|AZ:Arizona|AR:Arkansas|CA:California|CO:Colorado|CT:Connecticut|DE:Delaware|DC:District of Columbia|FL:Florida|GA:Georgia|HI:Hawaii|ID:Idaho|IL:Illinois|IN:Indiana|IA:Iowa|KS:Kansas|KY:Kentucky|LA:Louisiana|ME:Maine|MD:Maryland|MA:Massachusetts|MI:Michigan|MN:Minnesota|MS:Mississippi|MO:Missouri|MT:Montana|NE:Nebraska|NV:Nevada|NH:New Hampshire|NJ:New Jersey|NM:New Mexico|NY:New York|NC:North Carolina|ND:North Dakota|OH:Ohio|OK:Oklahoma|OR:Oregon|PA:Pennsylvania|RI:Rhode Island|SC:South Carolina|SD:South Dakota|TN:Tennessee|TX:Texas|UT:Utah|VT:Vermont|VA:Virginia|WA:Washington|WV:West Virginia|WI:Wisconsin|WY:Wyoming").Split('|').Select(x=>x.Split(':')).ToDictionary(x=>x[0],x=>RealFuelPrices.Key(x[1]));
    internal static RealFuelPrices.Feed? VerifiedDiesel(string root,DateTime now) {
        var cfg=CentralPriceFeed.Configuration;
        return cfg==null?null:CentralPriceFeed.ReadVerified(Path.Combine(root,CentralPriceFeed.CacheName),cfg.PublicKeyPem,now)?.Ats;
    }
    internal static Dictionary<string,Price> Compute(Dictionary<string,Observation> observations,DateTime now,RealFuelPrices.Feed? diesel=null) {
        var direct=observations.Where(k=>Neighbours.ContainsKey(k.Key)&&k.Value!=null&&Valid(k.Value.UsdPerGallon)&&k.Value.Checked<=now.AddMinutes(5)&&k.Value.Checked>=now.AddDays(-30)&&k.Value.Sources is {Length:>0 and <=3}).ToDictionary(k=>k.Key,k=>new Price(k.Value.UsdPerGallon,k.Value.Checked>=now.AddDays(-7)?"public":"cached",k.Value.Samples is >0 and <=10000?k.Value.Samples:k.Value.Sources.Length));
        var fresh=direct.Where(k=>k.Value.Kind=="public").ToDictionary(k=>k.Key,k=>k.Value);var basis=fresh.Count>0?fresh:direct;
        // EIA prices are USD/l; they are a proxy for regional distribution costs, NEVER DEF measurements.
        var proxy=new Dictionary<string,decimal>();
        if(diesel!=null)foreach(var (state,key) in StateNames){
            if(diesel.Sources?.GetValueOrDefault(key)!=RealFuelPrices.EiaSource||diesel.DateFor(key)<now.Date.AddDays(-30)||diesel.DateFor(key)>now.Date.AddDays(1))continue;
            if(diesel.Prices.TryGetValue(key,out var fuel)&&fuel.Diesel is decimal p&&p*LitresPerGallon is >=1m and <=12m)proxy[state]=p*LitresPerGallon;
        }
        decimal center=proxy.Count>0?proxy.Values.Average():4m;
        // Explicit simulation assumption: $3.50/gal at $4 diesel, 20% absolute pass-through.
        decimal reference=basis.Count>0?basis.Values.Average(p=>p.UsdPerGallon):3.50m+.20m*(center-4m);
        var result=new Dictionary<string,Price>(direct){["USA"]=new(Math.Clamp(reference,.5m,15m),"index",0)};
        foreach(var state in Neighbours.Keys){
            if(result.ContainsKey(state))continue;
            decimal offset=proxy.TryGetValue(state,out var local)?Math.Clamp(.25m*(local-center),-.75m,.75m):0m;
            result[state]=new(Math.Round(Math.Clamp(reference+offset,.5m,15m),4),"index",0);
        }
        return result;
    }
    internal static string Bridge(Price p,string state,long timestamp)=>$"RCDEF1 {timestamp} {(p.UsdPerGallon/LitresPerGallon).ToString("0.000000",Inv)} {state} {(p.Kind=="index"?"national":p.Kind)}\n";
    internal static DateTime NextRefresh(RefreshAttempt? attempt, DateTime now) =>
        attempt != null && attempt.StartedUtc >= now - RefreshInterval && attempt.StartedUtc <= now.AddMinutes(5)
            ? attempt.StartedUtc + RefreshInterval : now;
    private static void Log(string root,string s)=>RoadChronBoundedFileAppender.AppendLine(Path.Combine(root,"def-prices.log"),$"[{DateTime.UtcNow:O}] {s}");
    private static void Delete(string p){try{File.Delete(p);}catch{}}
    private static int _refreshProgress = -1;
    internal static int RefreshProgress => System.Threading.Volatile.Read(ref _refreshProgress);
    internal static async Task Refresh(string root,string cache,CancellationToken ct) {
        System.Threading.Volatile.Write(ref _refreshProgress,0);
        try {
        var quotes=AdBlueRealPrices.Load<Dictionary<string,Observation>>(cache)??new();
#if ROADCHRON_COLLECTOR
        using var http=PublicDataHttp.Create(root,12,4*1024*1024);
        async Task<string?> Fetch(string url){await Task.Delay(500,ct);using var r=await http.GetAsync(url,ct);if((int)r.StatusCode is 429 or 403)throw new UnauthorizedAccessException("Provider refused or rate limited; daily sweep stopped");return r.IsSuccessStatusCode?await r.Content.ReadAsStringAsync(ct):null;}
        try {
            if(await Fetch(PilotSource) is string catalog) {
                var batch=ParsePilot(catalog,DateTime.UtcNow);
                foreach(var (state,quote) in batch) quotes[state]=quote;
                if(batch.Count>0) AdBlueRealPrices.Atomic(cache,JsonSerializer.Serialize(quotes));
            }
        } catch(Exception ex) when(!ct.IsCancellationRequested) { Log(root,"Pilot: "+ex.Message); }
        foreach(var state in Neighbours.Keys.OrderBy(s=>s,StringComparer.Ordinal)){
            if (PublicDataHttp.Offline(root) || File.Exists(Path.Combine(root, "disabled.txt"))) break;
            try {
            if(quotes.TryGetValue(state,out var old)&&old!=null&&Valid(old.UsdPerGallon)&&old.Sources is {Length:>0 and <=3}&&old.Checked>=DateTime.UtcNow.AddHours(-24)&&old.Checked<=DateTime.UtcNow.AddMinutes(5))continue;
            try{
                var directory=await Fetch(Source+"location/"+state.ToLowerInvariant()+"/");if(directory==null)continue;
                var urls=StationUrls(directory,state);var samples=new List<decimal>();var sources=new List<string>();
                // Spread up to three attempts over the directory, not a full site crawl.
                var candidates=urls.Length<=3?urls:new[]{urls[0],urls[urls.Length/2],urls[^1]};
                foreach(var url in candidates){if(PublicDataHttp.Offline(root)||File.Exists(Path.Combine(root,"disabled.txt")))break;try{var page=await Fetch(url);if(page!=null&&ParseStation(page) is decimal price){samples.Add(price);sources.Add(url);}}catch(Exception ex) when(!ct.IsCancellationRequested&&ex is not UnauthorizedAccessException){Log(root,url+": "+ex.Message);}}
                if(samples.Count>0){quotes[state]=new(samples.Average(),DateTime.UtcNow,sources.ToArray());AdBlueRealPrices.Atomic(cache,JsonSerializer.Serialize(quotes));Log(root,$"{state}: {samples.Average().ToString("0.000",Inv)} USD/gal ({samples.Count} station samples)");}
            }catch(UnauthorizedAccessException ex){Log(root,ex.Message);break;}catch(Exception ex)when(!ct.IsCancellationRequested){Log(root,state+": "+ex.Message);}
            } finally { System.Threading.Interlocked.Increment(ref _refreshProgress); }
        }
#else
        var central=await CentralPriceFeed.GetAsync(root,ct);
        if(central!=null)AdBlueRealPrices.Atomic(cache,JsonSerializer.Serialize(CentralPriceFeed.MergeDef(quotes,central.Def,DateTime.UtcNow)));
        System.Threading.Volatile.Write(ref _refreshProgress,1);
#endif
        } finally { System.Threading.Volatile.Write(ref _refreshProgress,-1); }
    }
    internal static async Task RunAsync(string dataRoot, ScsSharedMemoryReader reader, LocalScsRegionResolver resolver,
        Func<bool> isPluginInstalled, CancellationToken ct)
    {
        var root = Path.Combine(dataRoot, "fuel-prices");
        var dir = Path.Combine(root, "ATS");
        var live = Path.Combine(dir, "def-live.txt");
        var fallback = Path.Combine(dir, "def-fallback.txt");
        var cache = Path.Combine(dir, "def-quotes.json");
        var disabled = Path.Combine(root, "disabled.txt");
        var nextLookup = DateTime.MinValue;
        var nextInstallCheck = DateTime.MinValue;
        var nextPublish = DateTime.MinValue;
        bool installed = false;
        double lastX = double.NaN, lastZ = double.NaN;
        string? state = null;
        string? lastPublished = null;
        Dictionary<string, Price> prices = new();

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            do
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var sample = reader.Read("ATS");
                    bool running = IsAts(sample);
                    if (!running) nextInstallCheck = DateTime.MinValue;
                    else if (now >= nextInstallCheck)
                    {
                        nextInstallCheck = now.AddSeconds(15);
                        installed = false;
                        installed = isPluginInstalled();
                    }

                    bool disabledNow = File.Exists(disabled);
                    if (!running || !installed || disabledNow)
                    {
                        Delete(live);
                        if (disabledNow) Delete(fallback);
                        state = null;
                        lastX = lastZ = double.NaN;
                        nextPublish = DateTime.MinValue;
                        continue;
                    }

                    if (sample.SpeedKmh is not double speed || !double.IsFinite(speed) || Math.Abs(speed) > 1 ||
                        sample.PositionX is not double x || sample.PositionZ is not double z ||
                        !double.IsFinite(x) || !double.IsFinite(z))
                    {
                        Delete(live);
                        state = null;
                        lastX = lastZ = double.NaN;
                    }
                    else if (!double.IsFinite(lastX) || Math.Abs(x-lastX) > 20 || Math.Abs(z-lastZ) > 20 || now >= nextLookup)
                    {
                        Delete(live);
                        state = null;
                        var region = await resolver.ResolveAsync("ATS", x, z, ct);
                        var check = reader.Read("ATS");
                        if (IsAts(check) && check.SpeedKmh is double v && Math.Abs(v) <= 1 &&
                            check.PositionX is double xx && check.PositionZ is double zz &&
                            Math.Abs(xx-x) <= 20 && Math.Abs(zz-z) <= 20)
                            state = SelectState(region.Detected, region.CountryCode);
                        lastX = x;
                        lastZ = z;
                        nextLookup = DateTime.UtcNow.AddSeconds(state == null ? 10 : 300);
                        Log(root, "ATS state: " + (state ?? "USA") + "; local SCS: " + (region.Confidence ?? region.Message));
                    }

                    long stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (now >= nextPublish)
                    {
                        prices = Compute(AdBlueRealPrices.Load<Dictionary<string,Observation>>(cache) ?? new(), now, VerifiedDiesel(root,now));
                        AdBlueRealPrices.Atomic(fallback, Bridge(prices["USA"], "USA", stamp));
                        var snapshot = JsonSerializer.Serialize(prices);
                        if (snapshot != lastPublished)
                        {
                            AdBlueRealPrices.Atomic(Path.Combine(dir, "def-states.json"), snapshot);
                            lastPublished = snapshot;
                        }
                        nextPublish = now.AddSeconds(15);
                    }
                    if (state != null) AdBlueRealPrices.Atomic(live, Bridge(prices[state], state, stamp));
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Delete(live);
                    Log(root, "DEF bridge: " + ex.Message);
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            Delete(live);
        }
    }

    private static bool IsAts(TelemetryResponse sample) =>
        sample.Connected && sample.GameRunning && string.Equals(sample.GameName, "ATS", StringComparison.OrdinalIgnoreCase);
}
