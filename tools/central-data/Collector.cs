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
                (prior.Ats==null||oldAts?.Prices.Count==prior.Ats.Prices.Count)&&
                (prior.Ets2==null||oldEts?.Prices.Count==prior.Ets2.Prices.Count);
            if(!force&&Environment.GetEnvironmentVariable("ROADCHRON_ROMANIA_SCHEDULED")!="true"&&previousApproved&&prior!.GeneratedUtc>=now.AddDays(-6)){Console.WriteLine("Already published recently; no requests sent.");return 0;}
            string root=Path.Combine(Path.GetTempPath(),"roadchron-collector-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            using var cts=new CancellationTokenSource(TimeSpan.FromMinutes(20));var ct=cts.Token;
            try {
                PublicDataHttp.SetDownloadsEnabled(root,true);
                using var http=PublicDataHttp.Create(root,30,24*1024*1024);
                var providers=new Dictionary<string,CentralPriceFeed.ProviderStatus>(StringComparer.Ordinal);
                var ats=await ReadUsAsync(http,providers,ct);
                var ets=await Try("EU EnergyPrices",async()=>ParseEurope(await http.GetStringAsync(RealFuelPrices.EuropeCsv,ct),DateTime.UtcNow),providers,ct);
                var fx=await Try("ECB",async()=>AdBlueRealPrices.ParseFx(await http.GetStringAsync(AdBlueRealPrices.FxUrl,ct),DateTime.UtcNow),providers,ct);
                if(fx!=null){
                    var uk=ets?.Prices.ContainsKey("unitedkingdom")==true&&ets.DateFor("unitedkingdom")>=DateTime.UtcNow.Date.AddDays(-9)?null:await Try("UK DESNZ",()=>ReadUkAsync(http,fx,ct),providers,ct);
                    var norway=ets?.Prices.ContainsKey("norway")==true?null:await Try("Norway SSB",async()=>ParseNorway(await http.GetStringAsync(RealFuelPrices.NorwayData,ct),fx,DateTime.UtcNow),providers,ct);
                    ets=Merge(uk,ets,false,DateTime.UtcNow);
                    ets=Merge(norway,ets,false,DateTime.UtcNow);
                }
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
                var rates=fx??prior?.Fx;
                var payload=new CentralPriceFeed.Payload(1,now,MergeUs(ats,oldAts,now),Merge(ets,oldEts,false,now),
                    quotes,new(),AdBlueRealPrices.ValidFx(rates,now)?rates:null,providers);
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
            (ats?feed.Sources?.GetValueOrDefault(p.Key)==RealFuelPrices.EiaSource:feed.Sources?.GetValueOrDefault(p.Key) is RealFuelPrices.EtsSource or RealFuelPrices.UkSource or RealFuelPrices.NorwaySource)).ToDictionary(p=>p.Key,p=>p.Value);
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

    private static RealFuelPrices.Feed ParseEurope(string csv, DateTime now)
    {
        using var parser=new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(csv.TrimStart('\uFEFF')));
        parser.SetDelimiters(",");
        if(!(parser.ReadFields()??[]).SequenceEqual(new[]{"date","country_code","country","fuel_type","price_eur_l"}))
            throw new InvalidDataException("EU EnergyPrices CSV columns or units changed.");

        var countries=RealFuelPrices.EuropeanCountryCodes
            .ToDictionary(item=>item.Value.ToUpperInvariant(),item=>item.Key,StringComparer.Ordinal);
        var observations=new Dictionary<(string Key,DateTime Date),RealFuelPrices.Price>();
        while(!parser.EndOfData)
        {
            var row=parser.ReadFields()??[];
            if(row.Length!=5||!countries.TryGetValue(row[1],out var key)||
               !DateTime.TryParseExact(row[0],"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)||
               row[3] is not ("gasoline" or "diesel")||
               !decimal.TryParse(row[4],NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value)||
               value is <.15m or >8m)continue;
            // BFS records the covered month by its first day. Report the period end like SSB.
            if(key=="switzerland")
            {
                if(date.Day!=1)throw new InvalidDataException("Unexpected Swiss observation period.");
                date=date.AddMonths(1).AddDays(-1);
            }
            int maxAge=RealFuelPrices.ObservationAgeDays(key);
            if(date>now.Date||date<now.Date.AddDays(-maxAge))continue;
            var existing=observations.GetValueOrDefault((key,date))??new(null,null);
            var prior=row[3]=="diesel"?existing.Diesel:existing.Petrol;
            if(prior.HasValue&&prior.Value!=value)throw new InvalidDataException("Conflicting European observations for "+key);
            observations[(key,date)]=row[3]=="diesel"?existing with {Diesel=value}:existing with {Petrol=value};
        }
        var latest=observations.Where(item=>item.Value is {Petrol:not null,Diesel:not null})
            .GroupBy(item=>item.Key.Key,StringComparer.Ordinal)
            .Select(group=>group.MaxBy(item=>item.Key.Date)).ToArray();
        if(latest.Length==0)throw new InvalidDataException("No verified European fuel prices.");
        return new(latest.Max(item=>item.Key.Date),
            latest.ToDictionary(item=>item.Key.Key,item=>item.Value),
            latest.ToDictionary(item=>item.Key.Key,item=>item.Key.Date),
            latest.ToDictionary(item=>item.Key.Key,_=>RealFuelPrices.EtsSource));
    }

    private static RealFuelPrices.Feed EuropeanOne(string country,string source,DateTime date,decimal petrol,decimal diesel,decimal rate,DateTime now){
        if(rate<=0||date.Date>now.Date||date.Date<now.Date.AddDays(-RealFuelPrices.ObservationAgeDays(country)))throw new InvalidDataException("Invalid observation date or rate.");
        var feed=new RealFuelPrices.Feed(date,new(){[country]=new(petrol/rate,diesel/rate)},new(){[country]=date},new(){[country]=source});
        if(!RealFuelPrices.ValidFeed(feed,false,now))throw new InvalidDataException("Invalid country price.");return feed;
    }
    private static async Task<RealFuelPrices.Feed> ReadUkAsync(HttpClient http,AdBlueRealPrices.Fx fx,CancellationToken ct){
        var page=await http.GetStringAsync(RealFuelPrices.UkSource,ct);
        var links=Regex.Matches(page,"href\\s*=\\s*[\"']([^\"']+)[\"']",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(3)).Cast<Match>()
            .Select(m=>WebUtility.HtmlDecode(m.Groups[1].Value)).Where(s=>Uri.TryCreate(s,UriKind.Absolute,out var uri)&&PublicDataHttp.UkCsvAllowed(uri)).Distinct().ToArray();
        if(links.Length!=1)throw new InvalidDataException("Unique UK current CSV not found.");
        return ParseUk(await http.GetStringAsync(links[0],ct),fx,DateTime.UtcNow);
    }
    private static RealFuelPrices.Feed ParseUk(string csv,AdBlueRealPrices.Fx fx,DateTime now){
        using var parser=new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(csv.TrimStart('\uFEFF')));
        parser.SetDelimiters(",");parser.HasFieldsEnclosedInQuotes=true;
        var h=parser.ReadFields()??[];
        if(h.Length<3||h[0]!="Date"||h[1]!="ULSP (Ultra low sulphur unleaded petrol) Pump price in pence/litre"||h[2]!="ULSD (Ultra low sulphur diesel) Pump price in pence/litre")throw new InvalidDataException("UK CSV units or layout changed.");
        var latest=DateTime.MinValue;decimal petrol=0,diesel=0;
        while(!parser.EndOfData){var row=parser.ReadFields()??[];if(row.Length!=h.Length||!DateTime.TryParseExact(row[0],"dd/MM/yyyy",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)||date<=latest||date>now.Date)continue;
            if(!decimal.TryParse(row[1],NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var p)||!decimal.TryParse(row[2],NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var d))continue;
            latest=date;petrol=p/100;diesel=d/100;
        }
        if(latest<now.Date.AddDays(-14)||!fx.Rates.TryGetValue("GBP",out var rate))throw new InvalidDataException("No recent UK price or GBP rate.");
        return EuropeanOne("unitedkingdom",RealFuelPrices.UkSource,latest,petrol,diesel,rate,now);
    }
    private static RealFuelPrices.Feed ParseNorway(string json,AdBlueRealPrices.Fx fx,DateTime now){
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;var dims=root.GetProperty("dimension");
        var ids=root.GetProperty("id").EnumerateArray().Select(x=>x.GetString()).ToArray();var sizes=root.GetProperty("size").EnumerateArray().Select(x=>x.GetInt32()).ToArray();
        if(ids.Length!=3||sizes.Length!=3||ids.Distinct().Count()!=3||!ids.Contains("PetroleumProd")||!ids.Contains("Tid")||!ids.Contains("ContentsCode")||sizes[Array.IndexOf(ids,"PetroleumProd")]!=2||sizes[Array.IndexOf(ids,"Tid")]!=1||sizes[Array.IndexOf(ids,"ContentsCode")]!=1)throw new InvalidDataException("SSB dimensions changed.");
        var products=dims.GetProperty("PetroleumProd").GetProperty("category").GetProperty("index");
        var times=dims.GetProperty("Tid").GetProperty("category").GetProperty("index");
        var metric=dims.GetProperty("ContentsCode").GetProperty("category");
        if(!metric.GetProperty("index").TryGetProperty("Priser",out _))throw new InvalidDataException("SSB price series absent.");
        var month=times.EnumerateObject().Single().Name;
        if(!DateTime.TryParseExact(month,"yyyy'M'MM",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)||!fx.Rates.TryGetValue("NOK",out var rate))throw new InvalidDataException("SSB date or FX missing.");
        var values=root.GetProperty("value");if(values.GetArrayLength()!=2)throw new InvalidDataException("SSB values changed.");
        int p=products.GetProperty("031").GetInt32(),d=products.GetProperty("035").GetInt32();if(p==d||p<0||p>1||d<0||d>1)throw new InvalidDataException("SSB product order invalid.");
        return EuropeanOne("norway",RealFuelPrices.NorwaySource,date.AddMonths(1).AddDays(-1),values[p].GetDecimal(),values[d].GetDecimal(),rate,now);
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

    private static async Task<RealFuelPrices.Feed?> ReadUsAsync(HttpClient http, Dictionary<string,CentralPriceFeed.ProviderStatus> providers, CancellationToken ct)
    {
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
        return tables[0]?.Date==newest||tables[1]?.Date==newest?BuildUs(tables):null;
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
        var eu=ParseEurope(File.ReadAllText(Path.Combine(folder,"europe.csv")),now);
        Console.WriteLine($"Europe CSV: {eu.Prices.Count} countries; ETS2 supported {RealFuelPrices.DiagnosticRegions(false).Count(eu.Prices.ContainsKey)}/{RealFuelPrices.DiagnosticRegions(false).Length}; Switzerland {eu.DateFor("switzerland"):yyyy-MM-dd}: {eu.Prices["switzerland"]}");
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
        const string sample="date,country_code,country,fuel_type,price_eur_l\n2026-10-01,CH,Switzerland,gasoline,2.1\n2026-10-01,CH,Switzerland,diesel,2.5";
        var monthly=ParseEurope(sample,now.AddDays(12));
        Check(monthly.DateFor("switzerland")==new DateTime(2026,10,31),"monthly Swiss data survive the next publication lag");
        var table=new EiaWorkbooks.Table(new DateTime(2026,11,23),new(){["NUS"]=4m,["R40"]=4.50m,["SCA"]=5m});
        var feed=BuildUs([table,table,table,table]);
        var fx=new AdBlueRealPrices.Fx(now,now,new(){["GBP"]=.8m,["NOK"]=10m});
        var uk=ParseUk("Date,ULSP (Ultra low sulphur unleaded petrol) Pump price in pence/litre,ULSD (Ultra low sulphur diesel) Pump price in pence/litre\n23/11/2026,120,160",fx,now);
        Check(uk.Prices["unitedkingdom"].Diesel==2m,"UK pence and GBP conversion");
        Check(PublicDataHttp.CollectorAllowed(new(RealFuelPrices.NorwayData))&&!PublicDataHttp.CollectorAllowed(new(RealFuelPrices.NorwayData+"&other=1")),"fixed SSB query only");
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
        Check(PublicDataHttp.CollectorAllowed(new(EiaWorkbooks.Urls[0]))&&!PublicDataHttp.CollectorAllowed(new(RealFuelPrices.AtsSource))&&!PublicDataHttp.CollectorAllowed(new(RealFuelPrices.AmzsSource))&&!PublicDataHttp.CollectorAllowed(new(DefRealPrices.PilotSource)),"collector denies legacy providers");
        Check(Approved(new(now,new(){["california"]=new(1m,1m)},null,new(){["california"]=RealFuelPrices.AtsSource}),true,now)==null,"restricted signed prior data excluded");
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
        Check(CentralPriceFeed.Verify(CentralPriceFeed.Sign(payload with { GeneratedUtc=now.AddDays(12),Ets2=monthly,AdBlue=[],Ats=null },key),
            key.ExportSubjectPublicKeyInfoPem(),now.AddDays(12)).Ets2!.Date==monthly.Date,
            "signed monthly Swiss data retain the original date beyond 30 days");
        Check(CentralPriceFeed.Verify(CentralPriceFeed.Sign(payload,key),key.ExportSubjectPublicKeyInfoPem(),now).AdBlue.Single().Country=="RO","RO signed feed round trip");
        Check(CentralPriceFeed.Verify(CentralPriceFeed.Sign(payload,key),key.ExportSubjectPublicKeyInfoPem(),now).Ats!.IsEstimated("idaho","Diesel"),"signed estimates round trip");
    }
}
