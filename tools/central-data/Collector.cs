using System.Security.Cryptography;
using System.Text.Json;
using RoadChron;

internal static class Collector
{
    public static async Task<int> Main(string[] args)
    {
        try {
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
                        string prior=await response.Content.ReadAsStringAsync();
                        CentralPriceFeed.Verify(prior,config.PublicKeyPem,now,true);
                        AdBlueRealPrices.Atomic(Path.GetFullPath(output),prior);
                    }
                } finally { try { Directory.Delete(bootstrap,true); } catch {} }
            }
            var previous=CentralPriceFeed.ReadVerified(output,config.PublicKeyPem,now,true);
            if(File.Exists(output)&&previous==null)throw new InvalidDataException("Existing feed invalid; refusing to overwrite.");
            if(previous!=null&&previous.GeneratedUtc>=now.AddHours(-20)){Console.WriteLine("Already published recently; no requests sent.");return 0;}
            string root=Path.Combine(Path.GetTempPath(),"roadchron-collector-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            using var cts=new CancellationTokenSource(TimeSpan.FromMinutes(20));var ct=cts.Token;
            try {
                PublicDataHttp.SetDownloadsEnabled(root,true);
                var dir=Path.Combine(root,"ETS2");Directory.CreateDirectory(dir);
                string defPath=Path.Combine(root,"ATS","def-quotes.json");
                if(previous?.Fx is {} fx0)AdBlueRealPrices.Atomic(Path.Combine(root,AdBlueRealPrices.FxCacheName),JsonSerializer.Serialize(fx0));
                if(previous!=null)AdBlueRealPrices.Atomic(defPath,JsonSerializer.Serialize(previous.Def));
                using var http=PublicDataHttp.Create(root,20,24*1024*1024);
                var ats=await Try("AAA",async()=>RealFuelPrices.Parse(await http.GetStringAsync(RealFuelPrices.AtsSource,ct),true,DateTime.UtcNow),ct);
                var ets=await Try("ETS2",()=>ReadEuropeAsync(http,root,ct),ct);
                var fx=await AdBlueRealPrices.GetFxAsync(root,ct);
                var (quotes,updatedFx)=await AdBlueRealPrices.RefreshAsync(root,dir,previous?.AdBlue??[],fx,http,ct);
                await DefRealPrices.Refresh(root,defPath,ct);
                var def=AdBlueRealPrices.Load<Dictionary<string,DefRealPrices.Observation>>(defPath)??new();
                now=DateTime.UtcNow;
                bool newData=ats!=null||ets!=null||quotes.Any(q=>q.Checked>(previous?.GeneratedUtc??DateTime.MinValue))||def.Values.Any(q=>q.Checked>(previous?.GeneratedUtc??DateTime.MinValue))||updatedFx?.Fetched>(previous?.GeneratedUtc??DateTime.MinValue);
                if(!newData)throw new InvalidDataException("All sources failed; previous publication preserved.");
                var payload=new CentralPriceFeed.Payload(1,now,Merge(ats,previous?.Ats,true,now),Merge(ets,previous?.Ets2,false,now),
                    CentralPriceFeed.MergeAdBlue([],quotes,now),CentralPriceFeed.MergeDef(new(),def,now),AdBlueRealPrices.ValidFx(updatedFx,now)?updatedFx:null);
                string signed=CentralPriceFeed.Sign(payload,signing);CentralPriceFeed.Verify(signed,config.PublicKeyPem,now);
                AdBlueRealPrices.Atomic(Path.GetFullPath(output),signed);
                Console.WriteLine($"Published {payload.Ats?.Prices.Count??0} ATS, {payload.Ets2?.Prices.Count??0} ETS2, {payload.AdBlue.Select(q=>q.Country).Distinct().Count()} AdBlue countries, {payload.Def.Count} DEF states; FX {payload.Fx?.Rates.Count??0}.");return 0;
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
    private static RealFuelPrices.Feed? Merge(RealFuelPrices.Feed? current,RealFuelPrices.Feed? previous,bool ats,DateTime now)
    {
        var feed=RealFuelPrices.ValidFeed(current,ats,now)?RealFuelPrices.MergeObservations(current!,previous,ats,now):previous;
        if(!RealFuelPrices.ValidFeed(feed,ats,now))return null;
        var prices=feed!.Prices.Where(p=>feed.DateFor(p.Key).Date>=now.Date.AddDays(-30)).ToDictionary(p=>p.Key,p=>p.Value);
        if(prices.Count==0)return null;
        var dates=prices.Keys.ToDictionary(k=>k,feed.DateFor);
        return new(dates.Values.Max(),prices,dates,prices.Keys.ToDictionary(k=>k,k=>feed.Sources?.GetValueOrDefault(k)??(ats?RealFuelPrices.AtsSource:RealFuelPrices.EtsSource)),feed.Expired);
    }
    private static void Log(string root,string message){Console.WriteLine(message);RoadChronBoundedFileAppender.AppendLine(Path.Combine(root,"fuel-prices.log"),message);}
    internal static async Task<RealFuelPrices.Feed> ReadEuropeAsync(HttpClient http, string root, CancellationToken stop)
    {
        var today = DateTime.UtcNow;
        var prices = new Dictionary<string, RealFuelPrices.Price>(StringComparer.Ordinal);
        var dates = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var sources = new Dictionary<string,string>(StringComparer.Ordinal);
        Dictionary<string,DateTime>? expired=null;
        DateTime newest = DateTime.MinValue;
        try
        {

            var main=RealFuelPrices.ParseAmzs(await http.GetStringAsync(RealFuelPrices.AmzsSource,stop),today);
            foreach(var (key,value) in main.Prices)
            {
                prices[key]=value;dates[key]=main.DateFor(key);sources[key]=RealFuelPrices.AmzsSource;
            }
            expired=main.Expired;
            if(RealFuelPrices.DiagnosticRegions(false).Where(RealFuelPrices.EuropeanCountryCodes.ContainsKey).All(k=>prices.TryGetValue(k,out var p)&&p.Petrol.HasValue))return main;
            newest=main.Date;
        }
        catch(Exception ex) when(!stop.IsCancellationRequested){Log(root,"AMZS: "+ex.Message);}
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
        return new(newest, prices, dates,sources,expired);
    }
}
