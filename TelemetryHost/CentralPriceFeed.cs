using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoadChron;

// The publisher signs the exact UTF-8 payload; the application pins its public key
// and a single immutable HTTPS URL at build time. No keys or URLs come from the feed.
internal static class CentralPriceFeed
{
    internal const int MaxBytes = 512 * 1024;
    internal const string CacheName = "central-fuel-signed.json";
    internal sealed record Config(string Url, string PublicKeyPem);
    internal sealed record Payload(int Schema, DateTime GeneratedUtc, RealFuelPrices.Feed? Ats,
        RealFuelPrices.Feed? Ets2, List<AdBlueRealPrices.Quote> AdBlue,
        Dictionary<string,DefRealPrices.Observation> Def, AdBlueRealPrices.Fx? Fx);
    internal sealed record Envelope(string Payload, string Signature);
    private static readonly SemaphoreSlim Gate = new(1);
    private static readonly Lazy<Config?> BuiltIn = new(ReadConfiguration);
    internal static Config? Configuration => BuiltIn.Value;
    internal static string EndpointLabel => Configuration?.Url ?? "not configured";
    internal static Uri? Endpoint(string? text)
    {
        if (text == null || !Uri.TryCreate(text,UriKind.Absolute,out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !Regex.IsMatch(uri.Host,@"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.github\.io\z") ||
            !Regex.IsMatch(uri.AbsolutePath,@"\A/(?:[A-Za-z0-9_.-]+/)?fuel-prices\.json\z") || uri.AbsoluteUri != text) return null;
        return uri;
    }
    private static Config? ReadConfiguration()
    {
        try {
            using var stream=typeof(CentralPriceFeed).Assembly.GetManifestResourceStream("RoadChron.CentralPriceConfig");
            var c=stream==null?null:JsonSerializer.Deserialize<Config>(stream);
            if(c==null||Endpoint(c.Url)==null||string.IsNullOrWhiteSpace(c.PublicKeyPem))return null;
            using var key=ECDsa.Create(); key.ImportFromPem(c.PublicKeyPem);
            if(key.KeySize!=256||!c.PublicKeyPem.Contains("BEGIN PUBLIC KEY",StringComparison.Ordinal))return null;
            return c;
        } catch { return null; }
    }
    internal static string Sign(Payload payload,ECDsa key)
    {
        if(key.KeySize!=256)throw new InvalidDataException("Expected P-256 signing key.");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(payload);
        var result=JsonSerializer.Serialize(new Envelope(Convert.ToBase64String(bytes),Convert.ToBase64String(key.SignData(bytes,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
        if(result.Length>MaxBytes)throw new InvalidDataException("Central feed too large.");
        return result;
    }
    internal static Payload Verify(string text,string publicKeyPem,DateTime now,bool allowExpired=false)
    {
        if(text.Length>MaxBytes)throw new InvalidDataException("Central feed too large.");
        var e=JsonSerializer.Deserialize<Envelope>(text)??throw new InvalidDataException("Missing envelope.");
        var data=Convert.FromBase64String(e.Payload);var signature=Convert.FromBase64String(e.Signature);
        using var key=ECDsa.Create();key.ImportFromPem(publicKeyPem);
        if(key.KeySize!=256||signature.Length!=64||!key.VerifyData(data,signature,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("Central feed signature rejected.");
        var p=JsonSerializer.Deserialize<Payload>(data)??throw new InvalidDataException("Missing payload.");
        if(p.Schema!=1||p.GeneratedUtc.Kind!=DateTimeKind.Utc||p.GeneratedUtc>now.AddMinutes(5)||p.GeneratedUtc==DateTime.MinValue||
            (!allowExpired&&p.GeneratedUtc<now.AddDays(-30))||p.AdBlue==null||p.Def==null||p.AdBlue.Count>100||p.Def.Count>51)
            throw new InvalidDataException("Invalid schema, timestamp or collection size.");
        var reference=p.GeneratedUtc;
        foreach(var (feed,ats) in new[]{(p.Ats,true),(p.Ets2,false)}) {
            if(feed==null)continue;
            if(!RealFuelPrices.ValidFeed(feed,ats,reference)||feed.Date.Date>reference.Date.AddDays(1)||feed.Prices.Count>60||
                feed.Prices.Keys.Any(k=>feed.DateFor(k).Date<reference.Date.AddDays(-30))||
                feed.Sources?.Any(s=>!feed.Prices.ContainsKey(s.Key)||!PublicDataHttp.AllowedSource(s.Value))==true)
                throw new InvalidDataException("Invalid fuel observations.");
        }
        var sourceKeys=new HashSet<(string,string)>();
        foreach(var q in p.AdBlue) {
            if(q==null||!AdBlueRealPrices.Neighbours.ContainsKey(q.Country)||q.Local<=0||q.Local>5000||
                q.Checked>reference.AddMinutes(5)||q.Checked<reference.AddDays(-30)||q.Currency==null||!Regex.IsMatch(q.Currency,"^[A-Z]{3}$")||
                (q.Published is DateTime d&&(d.Date>q.Checked.Date.AddDays(1)||d.Date<q.Checked.Date.AddDays(-14)))||
                !((q.Country=="E"&&q.Currency=="EUR"&&q.Source==AdBlueRealPrices.SpainUrl)||AdBlueRealPrices.Sources.Any(s=>s.Country==q.Country&&s.Currency==q.Currency&&s.Url==q.Source))||
                !sourceKeys.Add((q.Country,q.Source)))throw new InvalidDataException("Invalid AdBlue observation.");
        }
        foreach(var (state,q) in p.Def) {
            if(!DefRealPrices.Neighbours.ContainsKey(state)||q==null||q.UsdPerGallon<.5m||q.UsdPerGallon>15m||q.Checked>reference.AddMinutes(5)||q.Checked<reference.AddDays(-30)||
                q.Samples<0||q.Samples>10000||q.Sources is not {Length:>0 and <=3}||q.Sources.Any(s=>s!=DefRealPrices.PilotSource&&(!PublicDataHttp.AllowedSource(s)||!s.StartsWith(DefRealPrices.Source+"location/"+state.ToLowerInvariant()+"/",StringComparison.Ordinal))))
                throw new InvalidDataException("Invalid DEF observation.");
        }
        if(p.Fx!=null&&!AdBlueRealPrices.ValidFx(p.Fx,reference))throw new InvalidDataException("Invalid FX observations.");
        if(p.Ats==null&&p.Ets2==null&&p.AdBlue.Count==0&&p.Def.Count==0&&p.Fx==null)throw new InvalidDataException("Empty central feed.");
        return p;
    }
    internal static Payload? ReadVerified(string path,string key,DateTime now,bool allowExpired=false)
    {
        try { if(new FileInfo(path).Length>MaxBytes)return null;return Verify(File.ReadAllText(path),key,now,allowExpired); }catch{return null;}
    }
    internal static async Task<Payload?> GetAsync(string root,CancellationToken ct)
    {
        var config=Configuration;
        if(config==null)return null; // fail closed until the owner configures the publication URL and key
        await Gate.WaitAsync(ct);
        try {
            var now=DateTime.UtcNow;var cache=Path.Combine(root,CacheName);
            var old=ReadVerified(cache,config.PublicKeyPem,now,true);
            var usable=old!=null&&old.GeneratedUtc>=now.AddDays(-30)?old:null;
            if(PublicDataHttp.Offline(root))return usable;
            var attempt=Path.Combine(root,"central-attempt.json");
            if(DefRealPrices.NextRefresh(AdBlueRealPrices.Load<DefRealPrices.RefreshAttempt>(attempt),now)>now)return usable;
            AdBlueRealPrices.Atomic(attempt,JsonSerializer.Serialize(new DefRealPrices.RefreshAttempt(now)));
            try {
                using var http=PublicDataHttp.Create(root,20,MaxBytes,Endpoint(config.Url));
                var text=await http.GetStringAsync(config.Url,ct);
                var current=Verify(text,config.PublicKeyPem,DateTime.UtcNow);
                if(current.GeneratedUtc<now.AddDays(-3)||old!=null&&current.GeneratedUtc<old.GeneratedUtc)
                    throw new InvalidDataException("Stale or rolled-back central feed.");
                if(PublicDataHttp.Offline(root))return usable;
                AdBlueRealPrices.Atomic(cache,text);
                Log(root,"Verified central feed: "+current.GeneratedUtc.ToString("O"));
                return current;
            } catch(Exception ex) when(!ct.IsCancellationRequested) { Log(root,"Central feed unavailable: "+ex.Message);return usable; }
        } finally { Gate.Release(); }
    }
    private static void Log(string root,string s)=>RoadChronBoundedFileAppender.AppendLine(Path.Combine(root,"central-prices.log"),$"[{DateTime.UtcNow:O}] {s}");
    internal static List<AdBlueRealPrices.Quote> MergeAdBlue(IEnumerable<AdBlueRealPrices.Quote> old,IEnumerable<AdBlueRealPrices.Quote> incoming,DateTime now) =>
        old.Concat(incoming).Where(q=>q!=null&&q.Checked>=now.AddDays(-30)&&q.Checked<=now.AddMinutes(5))
            .GroupBy(q=>(q.Country,q.Source)).Select(g=>g.OrderByDescending(q=>q.Published??q.Checked).ThenByDescending(q=>q.Checked).First()).ToList();
    internal static Dictionary<string,DefRealPrices.Observation> MergeDef(Dictionary<string,DefRealPrices.Observation> old,Dictionary<string,DefRealPrices.Observation> incoming,DateTime now) =>
        old.Concat(incoming).Where(q=>q.Value!=null&&q.Value.Checked>=now.AddDays(-30)&&q.Value.Checked<=now.AddMinutes(5))
            .GroupBy(q=>q.Key,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.MaxBy(q=>q.Value.Checked).Value,StringComparer.Ordinal);
}
