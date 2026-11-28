using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;
using RoadChron;

internal static class Collector
{
    public static async Task<int> Main(string[] args)
    {
        try {
            if(args is ["selftest"]){SelfTest();return 0;}
            if(args is ["check-usgasgauge"]){return await CheckUsGasGaugeAsync();}
            if(args is ["check-openvan"]){return await CheckOpenVanAsync();}
            if(args is ["check-openvan-files",var pricesFile,var fxFile,var asOf]){
                var date=DateTime.ParseExact(asOf,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal).AddDays(1).AddTicks(-1);
                var f=ParseOpenVan(File.ReadAllText(pricesFile),ParseOpenVanFx(File.ReadAllText(fxFile),date),date);
                return ReportOpenVan(f);
            }
            if(args is ["check-inputs",var samples,var timestamp])
            {
                CheckInputs(samples,DateTime.ParseExact(timestamp,"yyyy-MM-dd",CultureInfo.InvariantCulture));
                return 0;
            }
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
            bool force=args.Length==4&&args[3]=="--force";
            if((args.Length!=3&&!force)||args[0]!="collect")
                throw new ArgumentException("Usage: collect CONFIG OUTPUT [--force] | selftest | check-inputs DIRECTORY YYYY-MM-DD");
            string configPath=args[1],output=args[2];
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
            var oldQuotes=prior?.AdBlue.Where(q=>AdBlueRealPrices.ApprovedQuote(q)||(AdBlueRealPrices.VatEnabled&&AdBlueRealPrices.VatQuote(q))).ToList()??[];
            bool previousApproved=prior!=null&&prior.Def.Count==0&&oldQuotes.Count==prior.AdBlue.Count&&
                (prior.Ats==null||(oldAts?.Prices.Count==prior.Ats.Prices.Count&&oldAts?.Sources?.Values.Contains(RealFuelPrices.UsGasGaugeSource)==true))&&
                (prior.Ets2==null||oldEts?.Prices.Count==prior.Ets2.Prices.Count);
            if(!force&&Environment.GetEnvironmentVariable("ROADCHRON_ROMANIA_SCHEDULED")!="true"&&previousApproved&&prior!.GeneratedUtc>=now.AddDays(-6)){Console.WriteLine("Already published recently; no requests sent.");return 0;}
            string root=Path.Combine(Path.GetTempPath(),"roadchron-collector-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            using var cts=new CancellationTokenSource(TimeSpan.FromMinutes(20));var ct=cts.Token;
            try {
                PublicDataHttp.SetDownloadsEnabled(root,true);
                using var http=PublicDataHttp.Create(root,30,24*1024*1024);
                var providers=new Dictionary<string,CentralPriceFeed.ProviderStatus>(StringComparer.Ordinal);
                var ats=await ReadUsAsync(http,providers,ct);
                var ets=await Try("OpenVan",()=>ReadOpenVanAsync(http,ct),providers,ct);
                if(ets!=null&&ets.Prices.Count<RealFuelPrices.DiagnosticRegions(false).Length)
                    providers["OpenVan"]=new("partial",$"Complete country pairs: {ets.Prices.Count}/{RealFuelPrices.DiagnosticRegions(false).Length}; missing: {string.Join(",",RealFuelPrices.DiagnosticRegions(false).Where(k=>!ets.Prices.ContainsKey(k)).Select(RealFuelPrices.RegionCode))}.");
                // Initial migration must not replace a working feed with an empty EU dataset.
                if(oldEts==null&&(ets==null||ets.Prices.Count!=RealFuelPrices.DiagnosticRegions(false).Length))throw new InvalidDataException("Initial OpenVan migration requires full coverage; previous signed deployment preserved. Run check-openvan before publishing.");
                var fx=await Try("ECB",async()=>AdBlueRealPrices.ParseFx(await http.GetStringAsync(AdBlueRealPrices.FxUrl,ct),DateTime.UtcNow),providers,ct);
                if(fx!=null){
                    var extra=await Try("Frankfurter FX",async()=>AdBlueRealPrices.ParseSupplementFx(
                        await http.GetStringAsync(AdBlueRealPrices.SupplementFxUrl,ct),DateTime.UtcNow),providers,ct);
                    if(extra!=null&&extra.Rates.Count<AdBlueRealPrices.SupplementCurrencies.Length)
                        providers["Frankfurter FX"]=new("partial",$"Current blended rates: {extra.Rates.Count}/{AdBlueRealPrices.SupplementCurrencies.Length}.");
                    fx=AdBlueRealPrices.MergeSupplementFx(fx,extra,AdBlueRealPrices.UsableFx(prior?.Fx,DateTime.UtcNow),DateTime.UtcNow);

                }
                if(!providers.ContainsKey("Frankfurter FX"))providers["Frankfurter FX"]=new("skipped","No new ECB base rates; prior valid rates retained if available.");
                var quote=await Try("MITECO",async()=>AdBlueRealPrices.ParseSpainProduct(
                    await http.GetStringAsync(AdBlueRealPrices.SpainProductUrl,ct),DateTime.UtcNow)??
                    throw new InvalidDataException("No current Spanish AdBlue pump sample."),providers,ct);
                // Public Romania station endpoint has a weekly per-IP quota. No retries or manual workflow calls.
                AdBlueRealPrices.Quote? romania=null;
                if(RomaniaAllowed(Environment.GetEnvironmentVariable("ROADCHRON_ROMANIA_SCHEDULED"),
                    (fx??prior?.Fx)?.Rates.ContainsKey("RON")==true)){
                    romania=await Try("PretCarburant RO",async()=>{
                        var text=await http.GetStringAsync(AdBlueRealPrices.RomaniaUrl,ct);
                        return AdBlueRealPrices.ParseRomania(text,DateTime.UtcNow)??
                            throw new InvalidDataException("No sufficient current Romanian pump AdBlue observations (minimum 3 unique stations).");
                    },providers,ct);
                }
                if(!providers.ContainsKey("PretCarburant RO"))
                    providers["PretCarburant RO"]=new("skipped","Weekly quota: first scheduled run only, with usable RON rates.");
                var vat=new List<AdBlueRealPrices.Quote>();
                if(AdBlueRealPrices.VatEnabled){
                    var attempts=new Dictionary<string,CentralPriceFeed.ProviderStatus>();
                    foreach(var source in AdBlueRealPrices.VatSources){
                        var catalogue=await Try("VATservice "+source.Country,async()=>AdBlueRealPrices.ParseVat(await http.GetStringAsync(source.Url,ct),source,fx??prior?.Fx,DateTime.UtcNow)??throw new InvalidDataException("Invalid catalogue price or FX."),attempts,ct);
                        if(catalogue!=null)vat.Add(catalogue);
                    }
                }
                if(AdBlueRealPrices.VatEnabled)
                    providers["VATservice"]=new(vat.Count==AdBlueRealPrices.VatSources.Length?"ok":vat.Count==0?"failed":"partial",
                        $"Current catalogues: {vat.Count}/{AdBlueRealPrices.VatSources.Length}.");
                now=DateTime.UtcNow;
                if(ats==null&&ets==null&&fx==null&&quote==null&&romania==null&&vat.Count==0)throw new InvalidDataException("All approved sources failed; previous publication preserved.");
                var quotes=CentralPriceFeed.MergeAdBlue(oldQuotes,new[]{quote,romania}.OfType<AdBlueRealPrices.Quote>().Concat(vat),now);
                var rates=AdBlueRealPrices.UsableFx(fx??prior?.Fx,now);
                var payload=new CentralPriceFeed.Payload(1,now,MergeUs(ats,oldAts,now),Merge(ets,oldEts,false,now),
                    quotes,new(),AdBlueRealPrices.ValidFx(rates,now)?rates:null,providers);
                if(payload.Ats?.Prices.Count!=51||payload.Ats.Prices.Values.Any(p=>p is not {Petrol:not null,Diesel:not null,MidGrade:not null,Premium:not null})||
                    payload.Ets2?.Prices.Count!=36||payload.Ets2.Prices.Values.Any(p=>p is not {Petrol:not null,Diesel:not null}))
                    throw new InvalidDataException("Incomplete fuel dataset after approved cache merge; previous publication preserved.");
                string signed=CentralPriceFeed.Sign(payload,signing);CentralPriceFeed.Verify(signed,config.PublicKeyPem,now);
                AdBlueRealPrices.Atomic(Path.GetFullPath(output),signed);
                bool partial=providers.Values.Any(p=>p.State is "failed" or "partial");
                Console.WriteLine(partial?"PARTIAL: provider errors; retained usable prior observations.":"OK: all attempted providers succeeded.");
                Console.WriteLine($"Prepared {payload.Ats?.Prices.Count??0} ATS (includes labelled regional estimates), {payload.Ets2?.Prices.Count??0} ETS2, {payload.AdBlue.Count} AdBlue observations, 0 direct DEF states (client uses EIA proxy index); FX {payload.Fx?.Rates.Count??0}.");return 0;
            } finally { try{Directory.Delete(root,true);}catch{} }
        }catch(Exception ex){Console.Error.WriteLine(ex.Message);if(args is ["check-inputs",..])Console.Error.WriteLine(ex.StackTrace);if(args.Length>0 && args[0]=="keygen")Console.Error.WriteLine(ex.StackTrace);return 1;}
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
    private static bool RomaniaAllowed(string? scheduled, bool hasRon) => scheduled=="true"&&hasRon;

    private static async Task<T?> Try<T>(string label, Func<Task<T>> read,
        Dictionary<string,CentralPriceFeed.ProviderStatus> providers, CancellationToken ct) where T:class
    {
        try
        {
            var result=await read();
            providers[label]=new("ok");
            return result;
        }
        catch(Exception ex) when(!ct.IsCancellationRequested)
        {
            var chain=new List<string>();
            for(Exception? cause=ex;cause!=null;cause=cause.InnerException)
                chain.Add(cause.GetType().Name+": "+cause.Message.Replace('\r',' ').Replace('\n',' '));
            var detail=string.Join(" | ",chain);
            Console.Error.WriteLine(label+": "+detail);
            providers[label]=new("failed",detail[..Math.Min(300,detail.Length)]);
            return null;
        }
    }

    private static RealFuelPrices.Feed? Approved(RealFuelPrices.Feed? feed,bool ats,DateTime now)
    {
        if(!RealFuelPrices.ValidFeed(feed,ats,now))return null;
        var prices=feed!.Prices.Where(p=>feed.DateFor(p.Key).Date>=now.Date.AddDays(-Math.Max(30,RealFuelPrices.ObservationAgeDays(p.Key)))&&
            (ats?RealFuelPrices.ApprovedUsSource(feed.Sources?.GetValueOrDefault(p.Key)):feed.Sources?.GetValueOrDefault(p.Key)==RealFuelPrices.OpenVanSource)).ToDictionary(p=>p.Key,p=>p.Value);
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
    private static RealFuelPrices.Feed? MergeUs(RealFuelPrices.Feed? current, RealFuelPrices.Feed? previous, DateTime now)
    {
        // Schema 1 stores one observation week per region. Avoid giving an old grade the date of a new one.
        static bool Complete(RealFuelPrices.Feed? feed) => feed?.Prices.Count==51 &&
            feed.Prices.Values.All(p=>p is { Petrol:not null,Diesel:not null,MidGrade:not null,Premium:not null });
        if(!Complete(current)&&Complete(previous)&&previous!.Date.Date>=now.Date.AddDays(-14)) return previous;
        return Merge(current,previous,true,now);
    }


    private static Dictionary<string,decimal> ParseOpenVanFx(string json,DateTime now)
    {
        using var doc=JsonDocument.Parse(json);
        var root=doc.RootElement;
        if(root.TryGetProperty("success",out var ok)&&ok.ValueKind!=JsonValueKind.True)
            throw new InvalidDataException("OpenVan FX returned failure.");
        if(!DateTimeOffset.TryParse(root.GetProperty("meta").GetProperty("updated_at").GetString(),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var stamp)||
           stamp.UtcDateTime>now.AddMinutes(5)||stamp.UtcDateTime<now.AddHours(-48))throw new InvalidDataException("OpenVan FX stale or invalid timestamp.");
        var rates=new Dictionary<string,decimal>(StringComparer.Ordinal);
        foreach(var row in root.GetProperty("rates").EnumerateObject())
            if(row.Value.ValueKind==JsonValueKind.Number&&row.Value.TryGetDecimal(out var rate)&&rate is >=0.000001m and <=1000000m)rates[row.Name]=rate;
        if(rates.GetValueOrDefault("EUR")!=1m)throw new InvalidDataException("OpenVan FX base must be EUR.");
        return rates;
    }
    private static RealFuelPrices.Feed ParseOpenVan(string json,IReadOnlyDictionary<string,decimal> rates,DateTime now)
    {
        using var doc=JsonDocument.Parse(json);
        var root=doc.RootElement;
        if(root.GetProperty("success").ValueKind!=JsonValueKind.True)throw new InvalidDataException("OpenVan fuel returned failure.");
        var data=root.GetProperty("data");
        var prices=new Dictionary<string,RealFuelPrices.Price>();
        var dates=new Dictionary<string,DateTime>();
        static string? Str(JsonElement e,string name)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
        decimal? Grade(JsonElement row,params string[] keys)
        {
            if(!row.TryGetProperty("prices",out var p)||p.ValueKind!=JsonValueKind.Object)return null;
            foreach(var k in keys){
                if(!p.TryGetProperty(k,out var v)||v.ValueKind!=JsonValueKind.Number||!v.TryGetDecimal(out var amount)||amount<=0)continue;
                var currency=row.TryGetProperty("currencies",out var currencies)?Str(currencies,k):null;
                var unit=row.TryGetProperty("units",out var units)?Str(units,k):null;
                currency??=Str(row,"currency");unit??=Str(row,"unit");
                if(currency==null||!rates.TryGetValue(currency,out var rate)||rate<=0)continue;
                decimal volume=unit switch {"liter"=>1m,"gallon"=>RealFuelPrices.LitersPerUsGallon,"imperial_gallon"=>4.54609m,_=>0m};
                if(volume==0)continue;
                var eur=amount/rate/volume;
                if(eur is >=.15m and <=8m)return Math.Round(eur,4);
            }
            return null;
        }
        foreach(var key in RealFuelPrices.DiagnosticRegions(false)){
            var iso=RealFuelPrices.RegionCode(key)?.ToUpperInvariant();
            if(iso==null)continue;
            if(iso=="KS")iso="XK";
            if(!data.TryGetProperty(iso,out var row)&&!(iso=="XK"&&data.TryGetProperty("KS",out row)))continue;
            var code=Str(row,"country_code");
            if(code!=iso&&!(iso=="XK"&&code=="KS"))continue;
            if(row.TryGetProperty("is_excluded",out var excluded)&&excluded.ValueKind==JsonValueKind.True)continue;
            if(!DateTimeOffset.TryParse(Str(row,"fetched_at"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var stamp)||
               stamp.UtcDateTime>now.AddMinutes(5)||stamp.UtcDateTime.Date<now.Date.AddDays(-RealFuelPrices.ObservationAgeDays(key)))continue;
            var petrol=Grade(row,"gasoline","gasoline_regular");var diesel=Grade(row,"diesel","diesel_regular");
            if(petrol==null||diesel==null)continue; // schema 1 has one source/date per country pair
            prices[key]=new(petrol,diesel);dates[key]=stamp.UtcDateTime.Date;
        }
        if(prices.Count==0)throw new InvalidDataException("No valid fresh OpenVan country pairs.");
        return new(dates.Values.Max(),prices,dates,prices.Keys.ToDictionary(k=>k,_=>RealFuelPrices.OpenVanSource));
    }
    private static async Task<RealFuelPrices.Feed> ReadOpenVanAsync(HttpClient http,CancellationToken ct)
    {
        var json=await http.GetStringAsync(RealFuelPrices.OpenVanPrices,ct);
        var fx=ParseOpenVanFx(await http.GetStringAsync(RealFuelPrices.OpenVanFx,ct),DateTime.UtcNow);
        return ParseOpenVan(json,fx,DateTime.UtcNow);
    }
    private static int ReportOpenVan(RealFuelPrices.Feed feed)
    {
        var keys=RealFuelPrices.DiagnosticRegions(false);
        foreach(var k in keys){
            var code=RealFuelPrices.RegionCode(k)?.ToUpperInvariant();
            if(feed.Prices.TryGetValue(k,out var price))Console.WriteLine(FormattableString.Invariant($"{code,-3} {k,-18} Petrol {price.Petrol:F4} Diesel {price.Diesel:F4} EUR/L | {feed.DateFor(k):yyyy-MM-dd}"));
            else Console.WriteLine($"{code,-3} {k,-18} MISSING / stale / invalid");
        }
        Console.WriteLine($"OpenVan coverage: {feed.Prices.Count}/{keys.Length} countries; {feed.Prices.Count*2}/{keys.Length*2} prices. Dates are provider fetch timestamps, not independently verified observation dates.");
        return feed.Prices.Count==keys.Length?0:2;
    }
    private static async Task<int> CheckOpenVanAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"roadchron-openvan-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            PublicDataHttp.SetDownloadsEnabled(root,true);
            using var http=PublicDataHttp.Create(root,30,24*1024*1024);
            using var cts=new CancellationTokenSource(TimeSpan.FromMinutes(2));
            return ReportOpenVan(await ReadOpenVanAsync(http,cts.Token));
        } finally {try{Directory.Delete(root,true);}catch{}}
    }

    private static readonly string[] PaddGroups = [
        "R1X|connecticut maine massachusetts newhampshire rhodeisland vermont",
        "R1Y|delaware districtofcolumbia maryland newjersey newyork pennsylvania",
        "R1Z|florida georgia northcarolina southcarolina virginia westvirginia",
        "R20|illinois indiana iowa kansas kentucky michigan minnesota missouri nebraska northdakota ohio oklahoma southdakota tennessee wisconsin",
        "R30|alabama arkansas louisiana mississippi newmexico texas",
        "R40|colorado idaho montana utah wyoming",
        "R5XCA|alaska arizona hawaii nevada oregon washington",
        "R50|california"
    ];
    private static readonly Dictionary<string,string> Regions = PaddGroups.SelectMany(g=>{
        var parts=g.Split('|');return parts[1].Split(' ').Select(s=>(state:s,region:parts[0]));
    }).ToDictionary(p=>p.state,p=>p.region,StringComparer.Ordinal);

    private static readonly Dictionary<string,string> EiaStateCodes = DefRealPrices.StateNames
        .ToDictionary(p=>p.Value,p=>"S"+p.Key,StringComparer.Ordinal);

    private static RealFuelPrices.Feed BuildUs(EiaWorkbooks.Table?[] tables)
    {
        var date=tables.OfType<EiaWorkbooks.Table>().Select(t=>t.Date).DefaultIfEmpty(DateTime.MinValue).Max();
        var prices=new Dictionary<string,RealFuelPrices.Price>();
        var estimated=new Dictionary<string,string[]>();
        foreach(var state in RealFuelPrices.DiagnosticRegions(true))
        {
            var values=new decimal?[4];
            var flags=new List<string>();
            var stateCode=EiaStateCodes[state];
            for(int i=0;i<4;i++)
            {
                var t=tables[i];
                if(t==null||t.Date!=date)continue;
                if(t.UsdPerGallon.TryGetValue(stateCode,out var direct))
                {
                    values[i]=direct/RealFuelPrices.LitersPerUsGallon;
                    continue;
                }
                var region=Regions[state];
                var parent=region switch { "R1X" or "R1Y" or "R1Z"=>"R10", "R5XCA"=>"R50", _=>"NUS" };
                var regional=t.UsdPerGallon.GetValueOrDefault(region,
                    t.UsdPerGallon.GetValueOrDefault(parent,t.UsdPerGallon["NUS"]));
                values[i]=regional/RealFuelPrices.LitersPerUsGallon;
                flags.Add(RealFuelPrices.FuelFields[i]);
            }
            if(values[0]==null&&values[1]==null)continue;
            prices[state]=new(values[0],values[1],values[2],values[3]);
            if(flags.Count>0)estimated[state]=flags.ToArray();
        }
        if(prices.Count==0)throw new InvalidDataException("No usable EIA state/region data.");
        return new(date,prices,prices.Keys.ToDictionary(k=>k,_=>date),
            prices.Keys.ToDictionary(k=>k,_=>RealFuelPrices.EiaSource),null,estimated);
    }

    private sealed record GaugeRegular(DateTime Date, Dictionary<string,(decimal Price,bool Estimated)> States);
    private static DateTime GaugeDate(string value) => DateTime.ParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture);
    private static decimal GaugePrice(string value)
    {
        if(!decimal.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var price)||price is <.6m or >30m)
            throw new InvalidDataException("Invalid US Gas Gauge USD/gallon price.");
        return price;
    }
    private static void GaugeFresh(DateTime date,DateTime now)
    {
        if(date>now.Date||date<now.Date.AddDays(-14))throw new InvalidDataException("US Gas Gauge observation stale or future dated.");
    }
    private static GaugeRegular ParseGaugeRegular(string csv,DateTime now)
    {
        using var parser=new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(csv.TrimStart('\uFEFF')));
        parser.SetDelimiters(",");
        if(!(parser.ReadFields()??[]).SequenceEqual(new[]{"state","state_abbr","price_usd_per_gal","price_basis","rank_cheapest","week_ending"}))
            throw new InvalidDataException("US Gas Gauge Regular CSV format changed.");
        var states=new Dictionary<string,(decimal Price,bool Estimated)>();
        DateTime? week=null;
        while(!parser.EndOfData){
            var row=parser.ReadFields()??[];
            if(row.Length!=6||!DefRealPrices.StateNames.TryGetValue(row[1],out var key)||row[3] is not ("eia_published" or "usgasgauge_estimate"))throw new InvalidDataException("Invalid Regular row.");
            var date=GaugeDate(row[5]); GaugeFresh(date,now);
            if(week.HasValue&&week.Value!=date)throw new InvalidDataException("Mixed Regular weeks.");
            week=date;
            if(!states.TryAdd(key,(GaugePrice(row[2]),row[3]=="usgasgauge_estimate")))throw new InvalidDataException("Duplicate Regular state: "+key);
        }
        if(states.Count!=51||week==null)throw new InvalidDataException($"US Gas Gauge Regular: {states.Count}/51 states; use EIA fallback.");
        return new(week.Value,states);
    }
    private static EiaWorkbooks.Table ParseGaugeDiesel(string csv,DateTime now)
    {
        using var parser=new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(csv.TrimStart('\uFEFF')));
        parser.SetDelimiters(",");
        if(!(parser.ReadFields()??[]).SequenceEqual(new[]{"area","area_code","week_ending","price_nominal_usd_per_gal","price_real_usd_per_gal"}))
            throw new InvalidDataException("US Gas Gauge Diesel CSV format changed.");
        var weeks=new Dictionary<(string,DateTime),decimal>();
        while(!parser.EndOfData){
            var row=parser.ReadFields()??[];
            if(row.Length!=5||row[1] is not ("NUS" or "SCA"))throw new InvalidDataException("Invalid Diesel row.");
            var date=GaugeDate(row[2]);
            if(date<now.Date.AddDays(-14))continue;
            GaugeFresh(date,now);
            var price=GaugePrice(row[3]);
            if(!weeks.TryAdd((row[1],date),price))throw new InvalidDataException("Duplicate Diesel observation.");
        }
        var week=weeks.Keys.Select(x=>x.Item2).DefaultIfEmpty().Max();
        GaugeFresh(week,now);
        if(!weeks.TryGetValue(("NUS",week),out var us)||!weeks.TryGetValue(("SCA",week),out var ca))throw new InvalidDataException("Incomplete latest US/CA diesel pair.");
        return new(week,new(){["NUS"]=us,["SCA"]=ca});
    }
    private static RealFuelPrices.Feed ApplyGauge(RealFuelPrices.Feed feed,GaugeRegular? regular,EiaWorkbooks.Table? diesel)
    {
        var prices=new Dictionary<string,RealFuelPrices.Price>(feed.Prices);
        var sources=new Dictionary<string,string>(feed.Sources!);
        var estimated=new Dictionary<string,string[]>(feed.Estimated??new());
        foreach(var key in prices.Keys.ToArray()){
            var flags=estimated.GetValueOrDefault(key)?.ToList()??new List<string>();
            var p=prices[key]; bool used=false;
            if(regular!=null&&regular.Date==feed.Date&&regular.States.TryGetValue(key,out var row)){
                var original=p;
                p=p with {Petrol=row.Price/RealFuelPrices.LitersPerUsGallon};
                // Regional grade estimates may be below the new state Regular (AK/HI).
                // Keep existing regional estimates unless inverted; retain EIA spreads for the repair.
                var minimum=0.01m/RealFuelPrices.LitersPerUsGallon;
                if(p.Petrol is decimal petrol&&p.MidGrade is decimal mid&&mid<petrol&&flags.Contains("MidGrade"))
                    p=p with {MidGrade=petrol+Math.Max(minimum,(original.MidGrade??mid)-(original.Petrol??mid))};
                if(p.MidGrade is decimal middle&&p.Premium is decimal premium&&premium<middle&&flags.Contains("Premium"))
                    p=p with {Premium=middle+Math.Max(minimum,(original.Premium??premium)-(original.MidGrade??premium))};
                flags.Remove("Petrol"); if(row.Estimated)flags.Add("Petrol"); used=true;
            }
            if(diesel!=null&&diesel.Date==feed.Date&&key=="california"){
                p=p with {Diesel=diesel.UsdPerGallon["SCA"]/RealFuelPrices.LitersPerUsGallon};
                flags.Remove("Diesel");used=true;
            }
            prices[key]=p;if(used)sources[key]=RealFuelPrices.UsGasGaugeSource;
            if(flags.Count==0)estimated.Remove(key);else estimated[key]=flags.ToArray();
        }
        return feed with {Prices=prices,Sources=sources,Estimated=estimated};
    }
    private static async Task<int> CheckUsGasGaugeAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"roadchron-us-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{
            PublicDataHttp.SetDownloadsEnabled(root,true);
            using var http=PublicDataHttp.Create(root,30,24*1024*1024);
            using var cts=new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var statuses=new Dictionary<string,CentralPriceFeed.ProviderStatus>();
            var feed=await ReadUsAsync(http,statuses,cts.Token);
            foreach(var (name,status) in statuses)Console.WriteLine($"{name}: {status.State} {status.Detail}");
            if(feed==null)return 2;
            foreach(var (abbr,key) in DefRealPrices.StateNames.OrderBy(p=>p.Key)){
                if(!feed.Prices.TryGetValue(key,out var p)){Console.WriteLine($"{abbr} MISSING");continue;}
                string Val(decimal? value)=>value.HasValue?(value.Value*RealFuelPrices.LitersPerUsGallon).ToString("F3",CultureInfo.InvariantCulture):"MISSING";
                Console.WriteLine($"{abbr} {key,-20} Regular {Val(p.Petrol)} Diesel {Val(p.Diesel)} Midgrade {Val(p.MidGrade)} Premium {Val(p.Premium)} USD/gal | {feed.DateFor(key):yyyy-MM-dd} | estimates: {string.Join(",",feed.Estimated?.GetValueOrDefault(key)??[])}");
            }
            int count=feed.Prices.Values.Sum(p=>new[]{p.Petrol,p.Diesel,p.MidGrade,p.Premium}.Count(x=>x.HasValue));
            Console.WriteLine($"ATS coverage: {feed.Prices.Count}/51 regions; {count}/204 fuel prices (DEF excluded; unchanged model).");
            return count==204&&statuses.GetValueOrDefault("USGasGauge Regular")?.State=="ok"&&statuses.GetValueOrDefault("USGasGauge Diesel")?.State=="ok"?0:2;
        } finally {try{Directory.Delete(root,true);}catch{}}
    }
    private static async Task<RealFuelPrices.Feed?> ReadUsAsync(HttpClient http, Dictionary<string,CentralPriceFeed.ProviderStatus> providers, CancellationToken ct)
    {
        var regular=await Try("USGasGauge Regular",async()=>ParseGaugeRegular(await http.GetStringAsync(RealFuelPrices.UsGasGaugeRegular,ct),DateTime.UtcNow),providers,ct);
        var diesel=await Try("USGasGauge Diesel",async()=>ParseGaugeDiesel(await http.GetStringAsync(RealFuelPrices.UsGasGaugeDiesel,ct),DateTime.UtcNow),providers,ct);
        var tables=new EiaWorkbooks.Table?[4];
        for(int i=0;i<tables.Length;i++)
        {
            int fuel=i;
            tables[i]=await Try("EIA "+RealFuelPrices.FuelFields[i],
                async()=>EiaWorkbooks.Parse(await http.GetByteArrayAsync(EiaWorkbooks.Urls[fuel],ct),fuel,DateTime.UtcNow),providers,ct);
        }
        var newest=tables.OfType<EiaWorkbooks.Table>().Select(t=>t.Date).DefaultIfEmpty().Max();
        for(int i=0;i<tables.Length;i++)
            if(tables[i] is {} table&&table.Date<newest)
                providers["EIA "+RealFuelPrices.FuelFields[i]]=new("partial","Workbook has a different observation week.");
        if(newest==DateTime.MinValue)return null;
        if(regular!=null&&regular.Date!=newest)providers["USGasGauge Regular"]=new("partial","Different observation week; EIA fallback.");
        if(diesel!=null&&diesel.Date!=newest)providers["USGasGauge Diesel"]=new("partial","Different observation week; EIA fallback.");
        if(diesel!=null&&diesel.Date==newest&&tables[1]?.Date==newest){
            // Keep EIA regional values; prefer US Gas Gauge for the national fallback and California.
            var values=new Dictionary<string,decimal>(tables[1]!.UsdPerGallon);
            foreach(var (key,value) in diesel.UsdPerGallon)values[key]=value;
            tables[1]=new(newest,values);
        }
        if(diesel?.Date==newest&&tables[1]?.Date!=newest){
            providers["USGasGauge Diesel"]=new("partial","EIA regional diesel unavailable; keep prior complete feed if usable.");
            diesel=null;
        }
        if(tables[0]?.Date!=newest&&tables[1]?.Date!=newest&&regular?.Date!=newest)return null;
        // Supply a Regular table only when EIA is unavailable for this week; all 51 rows are then overwritten with their correct estimate flags.
        if(tables[0]?.Date!=newest&&regular!=null&&regular.Date==newest){
            var values=regular.States.ToDictionary(x=>EiaStateCodes[x.Key],x=>x.Value.Price);
            values["NUS"]=regular.States.Values.Average(x=>x.Price); // not published or used: every state has its own row
            tables[0]=new(newest,values);
        }
        return ApplyGauge(BuildUs(tables),regular,diesel);
    }

    private static void CheckInputs(string folder, DateTime now)
    {
        var tables=new[]{"eia-reg.xls","eia-diesel.xls","eia-mid.xls","eia-prem.xls"}
            .Select((file,i)=>EiaWorkbooks.Parse(File.ReadAllBytes(Path.Combine(folder,file)),i,now)).ToArray();
        var us=BuildUs(tables);
        if(us.Prices.Count!=51||us.Prices.Values.Any(p=>p is not { Petrol:not null,Diesel:not null,MidGrade:not null,Premium:not null }))
            throw new InvalidDataException("EIA sample does not cover all four fuels.");
        foreach(var field in RealFuelPrices.FuelFields)
            Console.WriteLine($"EIA {field} {us.Date:yyyy-MM-dd}: {us.Prices.Keys.Count(k=>!us.IsEstimated(k,field))}/51 observed; remaining estimates.");
        var eu=ParseOpenVan(File.ReadAllText(Path.Combine(folder,"openvan.json")),ParseOpenVanFx(File.ReadAllText(Path.Combine(folder,"openvan-fx.json")),now),now);
        if(ReportOpenVan(eu)!=0)throw new InvalidDataException("Incomplete OpenVan fixture.");
        var es=AdBlueRealPrices.ParseSpainProduct(File.ReadAllText(Path.Combine(folder,"spain.json")),now)
            ??throw new InvalidDataException("Spanish sample failed.");
        Console.WriteLine($"Spain: {es.Samples} public stations; {es.Local:F3} EUR/l, {es.Published:yyyy-MM-dd}.");
        var fx=AdBlueRealPrices.ParseFx(File.ReadAllText(Path.Combine(folder,"ecb.xml")),now);
        Console.WriteLine($"ECB: {fx.Rates.Count} rates; {fx.Date:yyyy-MM-dd}.");
        var roPath=Path.Combine(folder,"romania.json");
        if(File.Exists(roPath))
        {
            var ro=AdBlueRealPrices.ParseRomania(File.ReadAllText(roPath),now);
            Console.WriteLine(ro==null?"Romania: insufficient verified pump observations.":$"Romania: {ro.Samples} stations; {ro.Local:F3} RON/l, oldest {ro.Published:yyyy-MM-dd}.");
        }
        var config=JsonSerializer.Deserialize<CentralPriceFeed.Config>(File.ReadAllText("TelemetryHost/CentralPriceFeedConfig.json"))!;
        var prior=CentralPriceFeed.Verify(File.ReadAllText(Path.Combine(folder,"prior.json")),config.PublicKeyPem,now.AddDays(1));
        Console.WriteLine($"Existing production ECDSA signature verified ({prior.GeneratedUtc:O}).");
    }

    private static void SelfTest(){
        static void Check(bool test,string message){if(!test)throw new InvalidDataException(message);Console.WriteLine("PASS: "+message);}
        var now=new DateTime(2026,11,26,0,0,0,DateTimeKind.Utc);
        var statuses=new Dictionary<string,CentralPriceFeed.ProviderStatus>();
        var failed=Try("MITECO",()=>Task.FromException<AdBlueRealPrices.Quote>(
            new HttpRequestException("TLS",new System.Security.Authentication.AuthenticationException("Chain failed"))),
            statuses,CancellationToken.None).GetAwaiter().GetResult();
        Check(failed==null&&statuses["MITECO"].Detail!.Contains("AuthenticationException: Chain failed"),
            "provider failures retain inner TLS reason and produce no observation");
        var table=new EiaWorkbooks.Table(new DateTime(2026,11,23),new(){["NUS"]=4m,["R40"]=4.50m,["SCA"]=5m});
        var feed=BuildUs([table,table,table,table]);
        var gcsv="state,state_abbr,price_usd_per_gal,price_basis,rank_cheapest,week_ending\n"+string.Join("\n",DefRealPrices.StateNames.Select(x=>$"{x.Value},{x.Key},3.25,{(x.Key=="CA"?"eia_published":"usgasgauge_estimate")},1,2026-11-23"));
        var gr=ParseGaugeRegular(gcsv,now);
        var gd=ParseGaugeDiesel("area,area_code,week_ending,price_nominal_usd_per_gal,price_real_usd_per_gal\nU.S.,NUS,2026-11-23,4.01,4.01\nCalifornia,SCA,2026-11-23,5.01,5.01",now);
        var mixed=ApplyGauge(feed,gr,gd);
        Check(mixed.Prices.Count==51&&mixed.Prices["california"].Diesel==5.01m/RealFuelPrices.LitersPerUsGallon&&mixed.Prices["idaho"].Diesel==feed.Prices["idaho"].Diesel,"US Gas Gauge CA diesel and EIA regional diesel");
        Check(mixed.Prices["idaho"].Petrol==3.25m/RealFuelPrices.LitersPerUsGallon&&mixed.IsEstimated("idaho","Petrol")&&!mixed.IsEstimated("california","Petrol")&&mixed.Prices["idaho"].Premium==feed.Prices["idaho"].Premium,"US Gas Gauge Regular flags, unchanged EIA premium");
        Check(Approved(mixed,true,now)?.Prices.Count==51&&PublicDataHttp.AllowedSource(RealFuelPrices.UsGasGaugeSource)&&PublicDataHttp.CollectorAllowed(new(RealFuelPrices.UsGasGaugeRegular))&&PublicDataHttp.CollectorAllowed(new(RealFuelPrices.UsGasGaugeDiesel))&&!PublicDataHttp.CollectorAllowed(new(RealFuelPrices.UsGasGaugeRegular+"?other=1")),"US Gas Gauge signed source and exact download endpoints");
        using(var testKey=ECDsa.Create(ECCurve.NamedCurves.nistP256)){
            var testPayload=new CentralPriceFeed.Payload(1,now,mixed,null,new(),new(),null,new(){["USGasGauge Regular"]=new("ok"),["USGasGauge Diesel"]=new("ok")});
            var signedGauge=CentralPriceFeed.Sign(testPayload,testKey);
            Check(CentralPriceFeed.Verify(signedGauge,testKey.ExportSubjectPublicKeyInfoPem(),now).Ats?.Prices.Count==51,"signed US Gas Gauge feed accepted by updated client");
        }
        var high=gr with {States=new(gr.States){["alaska"]=(6m,true)}};
        var repaired=ApplyGauge(feed,high,null).Prices["alaska"];
        Check(repaired.Petrol<repaired.MidGrade&&repaired.MidGrade<repaired.Premium,"inverted estimated grades repaired; EIA spread retained");
        Check(ApplyGauge(feed,gr with {Date=gr.Date.AddDays(-7)},null).Prices["idaho"]==feed.Prices["idaho"],"no cross-week overlay");
        try{ParseGaugeRegular(gcsv+"\nCalifornia,CA,3.25,eia_published,1,2026-11-23",now);throw new Exception("Duplicate accepted");}catch(InvalidDataException){}
        Check(PublicDataHttp.CollectorAllowed(new(RealFuelPrices.OpenVanPrices))&&PublicDataHttp.CollectorAllowed(new(RealFuelPrices.OpenVanFx))&&!PublicDataHttp.CollectorAllowed(new(RealFuelPrices.OpenVanPrices+"&extra=1")),"exact OpenVan endpoints only");
        var ovFx=ParseOpenVanFx("""{"success":true,"rates":{"EUR":1,"PLN":4},"meta":{"updated_at":"2026-11-26T00:00:00Z"}}""",now);
        var ov=ParseOpenVan("""{"success":true,"data":{"DE":{"country_code":"DE","currency":"EUR","unit":"liter","fetched_at":"2026-11-25T00:00:00Z","prices":{"gasoline":1.9,"diesel":2.1}},"PL":{"country_code":"PL","currency":"EUR","unit":"gallon","currencies":{"diesel":"PLN","gasoline":"PLN"},"units":{"diesel":"liter","gasoline":"liter"},"fetched_at":"2026-11-25T00:00:00Z","prices":{"gasoline":6,"diesel":8}},"XK":{"country_code":"XK","currency":"EUR","unit":"liter","fetched_at":"2026-11-25T00:00:00Z","prices":{"gasoline_regular":1.4,"diesel_regular":1.8}},"FR":{"country_code":"FR","currency":"EUR","unit":"liter","fetched_at":"2026-10-01T00:00:00Z","prices":{"gasoline":1.9,"diesel":2.1}},"IT":{"country_code":"IT","currency":"EUR","unit":"kg","fetched_at":"2026-11-25T00:00:00Z","prices":{"gasoline":1.9,"diesel":2.1}}}}""",ovFx,now);
        Check(ov.Prices.Count==3&&ov.Prices["poland"].Diesel==2m&&ov.Prices["kosovo"].Petrol==1.4m&&ov.DateFor("germany")==now.AddDays(-1),"OpenVan mapping, grade units/currencies, stale and invalid units rejected");
        Check(Approved(ov,false,now)!=null&&Approved(new(now,new(){["switzerland"]=new(2.1m,2.5m)},null,new(){["switzerland"]="https://www.euenergyprices.eu/en/fuel"}),false,now)==null,"OpenVan only EU publisher; legacy cache not republished");
        var supplemental=AdBlueRealPrices.ParseSupplementFx("[{\"base\":\"EUR\",\"quote\":\"RSD\",\"rate\":117,\"date\":\"2026-11-25\"}]",now);
        var ecb=new AdBlueRealPrices.Fx(now,now,new(){["EUR"]=1m,["USD"]=1.1m,["PLN"]=4m,["CZK"]=25m,["CAD"]=1.5m,["MXN"]=20m});
        var complete=AdBlueRealPrices.MergeSupplementFx(ecb,supplemental,null,now);
        Check(complete.Rates["RSD"]==117m&&complete.SupplementDates!["RSD"]==now.AddDays(-1)&&AdBlueRealPrices.ValidFx(complete,now),"supplementary blended FX with original observation date");
        Check(PublicDataHttp.CollectorAllowed(new(AdBlueRealPrices.SupplementFxUrl))&&!PublicDataHttp.CollectorAllowed(new(AdBlueRealPrices.SupplementFxUrl+"&expand=providers")),"exact blended FX endpoint only");
        Check(RomaniaAllowed("true",true)&&!RomaniaAllowed("false",true)&&!RomaniaAllowed(null,true)&&!RomaniaAllowed("true",false),"RO scheduled quota gate");
        Check(MergeUs(BuildUs([table,null,null,null]),feed,now)==feed,"partial EIA outage preserves recent complete set and dates");
        Check(Merge(null,feed,true,now.AddDays(31))==null,"retention capped without re-dating");
        Check(!PublicDataHttp.CollectorAllowed(new(RealFuelPrices.EiaSource))&&EiaWorkbooks.Urls.All(url=>PublicDataHttp.CollectorAllowed(new(url))),"EIA only signed-off exact workbook URLs");
        Check(PublicDataHttp.CollectorAllowed(new(AdBlueRealPrices.SpainProductUrl))&&!PublicDataHttp.CollectorAllowed(new(AdBlueRealPrices.SpainUrl)),"MITECO exact filtered endpoint");
        Check(Regions.Count==51,"all states mapped exactly once");
        Check(feed.Prices.Count==51&&feed.Date==new DateTime(2026,11,23),"EIA latest observation date and coverage");
        Check(!feed.IsEstimated("california","Diesel")&&feed.IsEstimated("idaho","Diesel"),"direct and estimated prices kept separate");
        Check(feed.Prices["idaho"].Diesel==4.5m/RealFuelPrices.LitersPerUsGallon,"regional fallback uses USD per litre exactly once");
        Check(RealFuelPrices.ValidFeed(feed,true,now),"estimated feed valid");
        Check(PublicDataHttp.CollectorAllowed(new(EiaWorkbooks.Urls[0]))&&!PublicDataHttp.CollectorAllowed(new("https://gasprices.aaa.com/todays-state-averages/"))&&!PublicDataHttp.CollectorAllowed(new("https://www.amzs.si/na-poti/cene-goriv-po-evropi"))&&!PublicDataHttp.CollectorAllowed(new(DefRealPrices.PilotSource)),"collector denies legacy providers");
        Check(Approved(new(now,new(){["california"]=new(1m,1m)},null,new(){["california"]="https://gasprices.aaa.com/todays-state-averages/"}),true,now)==null,"restricted signed prior data excluded");
        string RowRo(string id,string price,string date,string extra="")=>"{\"id\":\""+id+"\",\"tip\":\"adblue\",\"pret\":"+price+",\"observat_la\":\""+date+"\""+extra+"}";
        string Ro(params string[] rows)=>"{\"status\":\"ok\",\"statii\":["+string.Join(",",rows)+"]}";
        var r1=RowRo("a","3","2026-11-25");var r2=RowRo("b","4","2026-11-26");var r3=RowRo("c","5","2026-11-24");
        var ro=AdBlueRealPrices.ParseRomania(Ro(r1,r2,r3,r1,RowRo("d","20","2026-11-25",",\"nesigur\":true"),RowRo("e","10","2026-10-01")),now);
        Check(ro is {Country:"RO",Currency:"RON",Local:4m}&&ro.Published==new DateTime(2026,11,24),"RO current unique station mean and oldest observation date");
        Check(AdBlueRealPrices.ParseRomania(Ro(r1,r2),now)==null,"RO small sample rejected");
        Check(AdBlueRealPrices.ParseRomania(Ro(r1,r2,r3,RowRo("c","6","2026-11-25")),now)==null,"RO conflicting duplicates excluded");
        Check(PublicDataHttp.CollectorAllowed(new(AdBlueRealPrices.RomaniaUrl))&&!PublicDataHttp.CollectorAllowed(new(AdBlueRealPrices.RomaniaUrl+"&other=1")),"RO exact query only");
        Check(!AdBlueRealPrices.ApprovedQuote(new("PL","PLN",3,now,AdBlueRealPrices.Sources.First(s=>s.Country=="PL").Url)),"unlicensed HTML remains excluded");
        var vatSource=AdBlueRealPrices.VatSources.First(x=>x.Country=="LV");
        var vat=AdBlueRealPrices.ParseVat(" 0,914 EUR ",vatSource,null,now);
        Check(vat is {Local:.914m}&&AdBlueRealPrices.VatQuote(vat),"VATservice strict decimal fixture");
        Check(AdBlueRealPrices.ParseVat("<html>0.914 USD</html>",vatSource,null,now)==null,"VATservice rejects wrong currency");
        Check(AdBlueRealPrices.ParseVat("<body>0,914 EUR</body>",vatSource,null,now) is {Local:.914m},"VATservice basic HTML wrapper");
        Check(AdBlueRealPrices.ParseVat("error 500",vatSource,null,now)==null,"VATservice rejects error response");
        Check(PublicDataHttp.CollectorAllowed(new(vatSource.Url))==AdBlueRealPrices.VatEnabled,"VATservice opt-in network policy");
        var index=AdBlueRealPrices.Compute([new("E","EUR",1m,now,AdBlueRealPrices.SpainUrl,now)],null,now);
        Check(index["E"].Kind=="public"&&index["PL"].Kind=="index"&&index["PL"].Eur==.8217m,"EU measured priority and labelled index");
        var defIndex=DefRealPrices.Compute(new(),now,feed);
        Check(defIndex.Count==52&&defIndex.Values.All(x=>x.Kind=="index"&&x.Samples==0)&&defIndex["CA"].UsdPerGallon>defIndex["TX"].UsdPerGallon,"DEF EIA proxy is labelled estimate, all states");
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload=new CentralPriceFeed.Payload(1,now,feed,null,[ro!],new(),null);
        Check(CentralPriceFeed.Verify(CentralPriceFeed.Sign(payload,key),key.ExportSubjectPublicKeyInfoPem(),now).AdBlue.Single().Country=="RO","RO signed feed round trip");
        Check(CentralPriceFeed.Verify(CentralPriceFeed.Sign(payload,key),key.ExportSubjectPublicKeyInfoPem(),now).Ats!.IsEstimated("idaho","Diesel"),"signed estimates round trip");
    }
}
