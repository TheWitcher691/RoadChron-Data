using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using RoadChron;

internal static class Collector
{
    public static async Task<int> Main(string[] args)
    {
        try {
            if(args is ["selftest"]){SelfTest();return 0;}
            if(args is ["keygen",var endpoint,var publicConfig,var privateKey]) {
                Console.WriteLine("RoadChron keygen Windows FIX 2");
                if(CentralPriceFeed.Endpoint(endpoint)==null)throw new ArgumentException("Use the exact https://OWNER.github.io/REPO/fuel-prices.json URL.");
                if(File.Exists(privateKey))throw new IOException("Private key already exists; refusing to overwrite.");
                using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
                using(var file=CreatePrivateKeyFile(privateKey))
                using(var writer=new StreamWriter(file))writer.Write(key.ExportPkcs8PrivateKeyPem());
                AdBlueRealPrices.Atomic(Path.GetFullPath(publicConfig),JsonSerializer.Serialize(new CentralPriceFeed.Config(endpoint,key.ExportSubjectPublicKeyInfoPem()),new JsonSerializerOptions{WriteIndented=true}));
                Console.WriteLine("Public config created. Store private key outside the repository; set GitHub secret ROADCHRON_PRICE_SIGNING_KEY. Never commit the private PEM.");return 0;
            }
            if(args is not ["collect",var configPath,var output])throw new ArgumentException("Usage: keygen URL CONFIG PRIVATE_PEM | collect CONFIG OUTPUT");
            var config=JsonSerializer.Deserialize<CentralPriceFeed.Config>(File.ReadAllText(configPath))??throw new InvalidDataException("Missing public config.");
            if(CentralPriceFeed.Endpoint(config.Url)==null)throw new InvalidDataException("Missing publication URL.");
            var privatePem=Environment.GetEnvironmentVariable("ROADCHRON_PRICE_SIGNING_KEY")??throw new InvalidOperationException("Signing secret not set.");
            using var signing=ECDsa.Create();signing.ImportFromPem(privatePem);
            using var pub=ECDsa.Create();pub.ImportFromPem(config.PublicKeyPem);
            if(!signing.ExportSubjectPublicKeyInfo().SequenceEqual(pub.ExportSubjectPublicKeyInfo()))throw new InvalidDataException("Signing key does not match pinned public key.");
            // The previous signed static publication is the only retained state. No repository write token.
            var now=DateTime.UtcNow;
            if(!File.Exists(output)) {
                string bootstrap=Path.Combine(Path.GetTempPath(),"roadchron-bootstrap-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(bootstrap);
                try {
                    PublicDataHttp.SetDownloadsEnabled(bootstrap,true);
                    using var client=PublicDataHttp.Create(bootstrap,20,CentralPriceFeed.MaxBytes,CentralPriceFeed.Endpoint(config.Url));
                    using var response=await client.GetAsync(config.Url);
                    if(response.StatusCode!=System.Net.HttpStatusCode.NotFound) {
                        response.EnsureSuccessStatusCode();
                        string priorText=await response.Content.ReadAsStringAsync();
                        CentralPriceFeed.Verify(priorText,config.PublicKeyPem,now,true);
                        AdBlueRealPrices.Atomic(Path.GetFullPath(output),priorText);
                    }
                } finally { try { Directory.Delete(bootstrap,true); } catch {} }
            }
            var prior=CentralPriceFeed.ReadVerified(output,config.PublicKeyPem,now,true);
            if(File.Exists(output)&&prior==null)throw new InvalidDataException("Existing feed invalid; refusing to overwrite.");
            // Never republish restricted legacy observations, even if signed by this owner.
            var oldAts=Approved(prior?.Ats,true,now);
            var oldEts=Approved(prior?.Ets2,false,now);
            var oldQuotes=prior?.AdBlue.Where(q=>q.Country=="E"&&q.Source==AdBlueRealPrices.SpainUrl).ToList()??[];
            bool previousApproved=prior!=null&&prior.Def.Count==0&&oldQuotes.Count==prior.AdBlue.Count&&
                (prior.Ats==null||oldAts?.Prices.Count==prior.Ats.Prices.Count)&&
                (prior.Ets2==null||oldEts?.Prices.Count==prior.Ets2.Prices.Count);
            if(previousApproved&&prior!.GeneratedUtc>=now.AddHours(-20)){Console.WriteLine("Already published recently; no requests sent.");return 0;}
            string root=Path.Combine(Path.GetTempPath(),"roadchron-collector-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            using var cts=new CancellationTokenSource(TimeSpan.FromMinutes(20));var ct=cts.Token;
            try {
                PublicDataHttp.SetDownloadsEnabled(root,true);
                using var http=PublicDataHttp.Create(root,30,24*1024*1024);
                var ats=await Try("EIA",()=>ReadUsAsync(http,ct),ct);
                var ets=await Try("EU EnergyPrices",()=>ReadEuropeAsync(http,root,ct),ct);
                var fx=await Try("ECB",async()=>AdBlueRealPrices.ParseFx(await http.GetStringAsync(AdBlueRealPrices.FxUrl,ct),DateTime.UtcNow),ct);
                var quote=await Try("MITECO",async()=>{
                    var text=await http.GetStringAsync(AdBlueRealPrices.SpainUrl,ct);var checkedAt=DateTime.UtcNow;
                    if(AdBlueRealPrices.ParseSpain(text,checkedAt,out var published) is not decimal eur||published==null)
                        throw new InvalidDataException("No current public Spanish AdBlue observations.");
                    return new AdBlueRealPrices.Quote("E","EUR",eur,checkedAt,AdBlueRealPrices.SpainUrl,published);
                },ct);
                now=DateTime.UtcNow;
                if(ats==null&&ets==null&&fx==null&&quote==null)throw new InvalidDataException("All approved sources failed; previous publication preserved.");
                var quotes=CentralPriceFeed.MergeAdBlue(oldQuotes,quote==null?[]:new[]{quote},now);
                var rates=fx??prior?.Fx;
                var payload=new CentralPriceFeed.Payload(1,now,Merge(ats,oldAts,true,now),Merge(ets,oldEts,false,now),
                    quotes,new(),AdBlueRealPrices.ValidFx(rates,now)?rates:null);
                string signed=CentralPriceFeed.Sign(payload,signing);CentralPriceFeed.Verify(signed,config.PublicKeyPem,now);
                AdBlueRealPrices.Atomic(Path.GetFullPath(output),signed);
                Console.WriteLine($"Published {payload.Ats?.Prices.Count??0} ATS (includes labelled regional estimates), {payload.Ets2?.Prices.Count??0} ETS2, {payload.AdBlue.Count} AdBlue observations, 0 DEF states; FX {payload.Fx?.Rates.Count??0}.");return 0;
            } finally { try{Directory.Delete(root,true);}catch{} }
        }catch(Exception ex){Console.Error.WriteLine(ex.Message);if(args.Length>0 && args[0]=="keygen")Console.Error.WriteLine(ex.StackTrace);return 1;}
    }
    private static FileStream CreatePrivateKeyFile(string path)
    {
        // Do not instantiate FileStreamOptions at all on Windows.
        // CreateNew ensures an existing private key is never overwritten.
        if (OperatingSystem.IsWindows())
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }
    private static async Task<T?> Try<T>(string label,Func<Task<T>> f,CancellationToken ct) where T:class {try{return await f();}catch(Exception ex)when(!ct.IsCancellationRequested){Console.Error.WriteLine(label+": "+ex.Message);return null;}}
    private static RealFuelPrices.Feed? Approved(RealFuelPrices.Feed? feed,bool ats,DateTime now)
    {
        if(!RealFuelPrices.ValidFeed(feed,ats,now))return null;
        var prices=feed!.Prices.Where(p=>feed.DateFor(p.Key).Date>=now.Date.AddDays(-30)&&
            feed.Sources?.GetValueOrDefault(p.Key)==(ats?RealFuelPrices.EiaSource:RealFuelPrices.EtsSource)).ToDictionary(p=>p.Key,p=>p.Value);
        if(prices.Count==0)return null;
        var dates=prices.Keys.ToDictionary(k=>k,feed.DateFor);
        var estimated=feed.Estimated?.Where(p=>prices.ContainsKey(p.Key)).ToDictionary(p=>p.Key,p=>p.Value);
        return new(dates.Values.Max(),prices,dates,prices.Keys.ToDictionary(k=>k,k=>feed.Sources![k]),null,estimated);
    }
    private static RealFuelPrices.Feed? Merge(RealFuelPrices.Feed? current,RealFuelPrices.Feed? previous,bool ats,DateTime now)
    {
        current=Approved(current,ats,now);previous=Approved(previous,ats,now);
        return current==null?previous:RealFuelPrices.MergeObservations(current,previous,ats,now);
    }
    private static void Log(string root,string message){Console.WriteLine(message);RoadChronBoundedFileAppender.AppendLine(Path.Combine(root,"fuel-prices.log"),message);}
    internal static async Task<RealFuelPrices.Feed> ReadEuropeAsync(HttpClient http, string root, CancellationToken stop)
    {
        var today = DateTime.UtcNow;
        var prices = new Dictionary<string, RealFuelPrices.Price>(StringComparer.Ordinal);
        var dates = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var sources = new Dictionary<string,string>(StringComparer.Ordinal);
        DateTime newest = DateTime.MinValue;
        try
        {

            var feed = RealFuelPrices.Parse(await http.GetStringAsync(RealFuelPrices.EtsSource, stop), false, today);
            if(feed.Date>newest)newest=feed.Date;
            foreach (var row in feed.Prices)
            {
                if (prices.ContainsKey(row.Key) && dates[row.Key]>=feed.Date) continue;
                prices[row.Key] = row.Value;
                sources[row.Key]=RealFuelPrices.EtsSource;
                dates[row.Key] = feed.Date;
            }
        }
        catch (Exception ex) when (!stop.IsCancellationRequested) { Log(root, "ETS2: main table unavailable; trying country pages - " + ex.Message); }
        var keys = RealFuelPrices.DiagnosticRegions(false).Where(k => RealFuelPrices.EuropeanCountryCodes.ContainsKey(k) && (!prices.TryGetValue(k,out var price)||!price.Petrol.HasValue)).ToArray();
        // Same public catalog for all users; no DLC- or location-specific requests.
        await Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = stop }, async (key, token) =>
        {
            try
            {
                string url = RealFuelPrices.EtsSource + "/" + RealFuelPrices.EuropeanCountryCodes[key];
                var observation = RealFuelPrices.ParseEuropeanCountry(await http.GetStringAsync(url, token), key, today);
                lock (prices)
                {
                    if(dates.TryGetValue(key,out var previous)&&previous>observation.Date)return;
                    prices[key] = observation.Price;
                    dates[key] = observation.Date;
                    sources[key]=RealFuelPrices.EtsSource;
                    if (observation.Date > newest) newest = observation.Date;
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested) { Log(root, "ETS2: " + key + " - game default: " + ex.Message); }
        });
        if (prices.Count == 0) throw new InvalidDataException("No verified European fuel prices.");
        return new(newest, prices, dates,sources);
    }

    private static readonly string[] PaddGroups = [
        "newenglandpadda|connecticut maine massachusetts newhampshire rhodeisland vermont",
        "centralatlanticpaddb|delaware districtofcolumbia maryland newjersey newyork pennsylvania",
        "loweratlanticpaddc|florida georgia northcarolina southcarolina virginia westvirginia",
        "midwestpadd|illinois indiana iowa kansas kentucky michigan minnesota missouri nebraska northdakota ohio oklahoma southdakota tennessee wisconsin",
        "gulfcoastpadd|alabama arkansas louisiana mississippi newmexico texas",
        "rockymountainpadd|colorado idaho montana utah wyoming",
        "westcoastlesscalifornia|arizona nevada oregon washington",
        "westcoastpadd|california",
        "us|alaska hawaii"
    ];
    private static readonly Dictionary<string,string> Regions = PaddGroups.SelectMany(g=>{
        var parts=g.Split('|');return parts[1].Split(' ').Select(s=>(state:s,region:parts[0]));
    }).ToDictionary(p=>p.state,p=>p.region,StringComparer.Ordinal);
    private static string Plain(string value)=>Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(value,"<[^>]+>"," ")), @"\s+", " ").Trim();
    private sealed record EiaTable(DateTime Date,Dictionary<string,decimal> Prices);
    private static EiaTable ParseEia(string html,DateTime now)
    {
        if(!Plain(html).Contains("Dollars per Gallon, Including Taxes",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("EIA units not verified.");
        var rows=Regex.Matches(html,@"<tr\b[^>]*>((?:(?!<tr\b).)*?)</tr\s*>",RegexOptions.IgnoreCase|RegexOptions.Singleline,TimeSpan.FromSeconds(8))
            .Cast<Match>().Select(r=>Regex.Matches(r.Groups[1].Value,@"<t[dh]\b[^>]*>(.*?)</t[dh]\s*>",RegexOptions.IgnoreCase|RegexOptions.Singleline,TimeSpan.FromSeconds(2))
            .Cast<Match>().Select(c=>Plain(c.Groups[1].Value)).ToArray()).ToArray();
        DateTime date=DateTime.MinValue;var prices=new Dictionary<string,decimal>(StringComparer.Ordinal);
        var labels=Regions.Keys.Concat(Regions.Values).Append("us").Append("westcoastpadd").ToHashSet(StringComparer.Ordinal);
        string? pending=null;
        foreach(var row in rows){
            var days=row.Select((v,i)=>(ok:DateTime.TryParseExact(v,new[]{"M/d/yy","MM/dd/yy","M/d/yyyy"},CultureInfo.InvariantCulture,DateTimeStyles.None,out var d),d,i)).Where(x=>x.ok).ToArray();
            if(days.Length==6&&days.Zip(days.Skip(1),(a,b)=>b.d>a.d&&b.i==a.i+1).All(x=>x)){
                if(date!=DateTime.MinValue&&date!=days[^1].d)throw new InvalidDataException("Ambiguous EIA table dates.");
                date=days[^1].d;continue;
            }
            if(date==DateTime.MinValue)continue;
            int labelIndex=Array.FindIndex(row,v=>labels.Contains(RealFuelPrices.Key(v)));
            string? key=labelIndex>=0?RealFuelPrices.Key(row[labelIndex]):pending;
            // EIA can place a label on its own row; never match a city or another table label.
            var values=(labelIndex>=0?row.Skip(labelIndex+1):row.SkipWhile(string.IsNullOrWhiteSpace)).ToArray();
            if(labelIndex>=0&&values.All(string.IsNullOrWhiteSpace)){pending=key;continue;}
            pending=null;
            if(key==null||values.Length is <6 or >7)continue;
            var six=values.Take(6).ToArray();
            bool PriceCell(string v)=>decimal.TryParse(v,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out _)||v is "" or "-" or "--" or "NA" or "W";
            if(!six.All(PriceCell)||!decimal.TryParse(six[5],NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var n)||n<.6m||n>30m)continue;
            if(!prices.TryAdd(key,n/RealFuelPrices.LitersPerUsGallon))throw new InvalidDataException("Duplicate EIA row: "+key);
        }
        if(date.Date>now.Date||date.Date<now.Date.AddDays(-21)||prices.Count==0)throw new InvalidDataException("No current verified EIA table.");
        return new(date,prices);
    }
    private static RealFuelPrices.Feed BuildUs(EiaTable?[] tables)
    {
        var date=tables.Where(t=>t!=null).Select(t=>t!.Date).DefaultIfEmpty(DateTime.MinValue).Max();
        var prices=new Dictionary<string,RealFuelPrices.Price>();var estimated=new Dictionary<string,string[]>();
        foreach(var state in RealFuelPrices.DiagnosticRegions(true)){
            var values=new decimal?[4];var flags=new List<string>();
            for(int i=0;i<4;i++){
                var t=tables[i];if(t==null||t.Date!=date)continue;
                if(t.Prices.TryGetValue(state,out var direct)){values[i]=direct;continue;}
                if(Regions.TryGetValue(state,out var region)&&t.Prices.TryGetValue(region,out var regional)||t.Prices.TryGetValue("us",out regional)){
                    values[i]=regional;flags.Add(RealFuelPrices.FuelFields[i]);
                }
            }
            if(values[0]==null&&values[1]==null)continue;
            prices[state]=new(values[0],values[1],values[2],values[3]);if(flags.Count>0)estimated[state]=flags.ToArray();
        }
        if(prices.Count==0)throw new InvalidDataException("No usable EIA state/region data.");
        return new(date,prices,prices.Keys.ToDictionary(k=>k,_=>date),prices.Keys.ToDictionary(k=>k,_=>RealFuelPrices.EiaSource),null,estimated);
    }
    private static async Task<RealFuelPrices.Feed> ReadUsAsync(HttpClient http,CancellationToken ct){
        var tables=new EiaTable?[4];
        for(int i=0;i<tables.Length;i++){
            var url=RealFuelPrices.EiaPages[i];
            tables[i]=await Try(RealFuelPrices.FuelFields[i],async()=>ParseEia(await http.GetStringAsync(url,ct),DateTime.UtcNow),ct);
        }
        return BuildUs(tables);
    }
    private static void SelfTest(){
        static void Check(bool test,string message){if(!test)throw new InvalidDataException(message);Console.WriteLine("PASS: "+message);}
        var now=new DateTime(2026,11,26,0,0,0,DateTimeKind.Utc);
        string header="<p>Dollars per Gallon, Including Taxes</p><table><tr><th></th><th>10/19/26</th><th>10/26/26</th><th>11/02/26</th><th>11/09/26</th><th>11/16/26</th><th>11/23/26</th><th>View History</th></tr>";
        string Row(string name,string value)=>"<tr><td>"+name+"</td>"+string.Concat(Enumerable.Repeat("<td>"+value+"</td>",6))+"<td>History</td></tr>";
        var table=ParseEia(header+Row("U.S.","4.00")+Row("Rocky Mountain (PADD 4)","4.50")+Row("California","5.00")+"</table>",now);
        var feed=BuildUs([table,table,table,table]);
        Check(Regions.Count==51,"all states mapped exactly once");
        Check(feed.Prices.Count==51&&feed.Date==new DateTime(2026,11,23),"EIA latest observation date and coverage");
        Check(!feed.IsEstimated("california","Diesel")&&feed.IsEstimated("idaho","Diesel"),"direct and estimated prices kept separate");
        Check(feed.Prices["idaho"].Diesel==4.5m/RealFuelPrices.LitersPerUsGallon,"regional fallback uses USD per litre exactly once");
        Check(RealFuelPrices.ValidFeed(feed,true,now),"estimated feed valid");
        Check(PublicDataHttp.CollectorAllowed(new(RealFuelPrices.EiaSource))&&!PublicDataHttp.CollectorAllowed(new(RealFuelPrices.AtsSource))&&!PublicDataHttp.CollectorAllowed(new(RealFuelPrices.AmzsSource))&&!PublicDataHttp.CollectorAllowed(new(DefRealPrices.PilotSource)),"collector denies legacy providers");
        Check(Approved(new(now,new(){["california"]=new(1m,1m)},null,new(){["california"]=RealFuelPrices.AtsSource}),true,now)==null,"restricted signed prior data excluded");
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload=new CentralPriceFeed.Payload(1,now,feed,null,[],new(),null);
        Check(CentralPriceFeed.Verify(CentralPriceFeed.Sign(payload,key),key.ExportSubjectPublicKeyInfoPem(),now).Ats!.IsEstimated("idaho","Diesel"),"signed estimates round trip");
    }
}
