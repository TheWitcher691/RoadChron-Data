using System.Text.Json;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace RoadChron;

// The parser's horizontal coordinates are x/y (SCS X/Z). Country IDs come
// directly from SCS node flags; city labels alone never determine live position.
// This is a conservative proximity estimate, not a geographic border polygon.
public sealed class LocalScsRegionResolver
{
    internal static LocalScsRegionResolver Shared { get; } = new(RoadChronPaths.LocalDataDirectory);
    private readonly string _root;
    public LocalScsRegionResolver(string root) => _root=root;
    public sealed record RegionResult(bool Detected,string? CountryName,string? CountryCode,string? RegionType,bool Area,string? Confidence,string Message,string? CityName=null);
    private sealed record Region(string Code,string Name);
    private readonly record struct Point(double X,double Z,Region Region);
    private sealed class Index
    {
        public Dictionary<(int,int),List<Point>> Grid {get;}=new();
        public Dictionary<string,Region?> Cities {get;}=new(StringComparer.Ordinal);
        public HashSet<string> Regions {get;}=new(StringComparer.Ordinal);
        public int Nodes {get;set;}
        public int Conflicts {get;set;}
        public int CityCount {get;set;}
    }
    private sealed class Slot
    {
        public SemaphoreSlim Gate {get;}=new(1);
        public string? Stamp;
        public Index? Data;
        public string Status="not loaded";
        public DateTime Retry;
    }
    private readonly ConcurrentDictionary<string,Slot> _slots=new(StringComparer.Ordinal);
    private const int Cell=256;
    private const double Radius=600, BorderMargin=80;
    private static (int,int) Tile(double x,double z)=>((int)Math.Floor(x/Cell),(int)Math.Floor(z/Cell));
    private static bool ValidGame(string game)=>game is "ATS" or "ETS2";
    private string[] Paths(string game)
    {
        string dir=Path.Combine(_root,"truckdash-trial",game,"parsed"), prefix=game=="ATS"?"usa":"europe";
        return new[]{"countries","cities","nodes"}.Select(p=>Path.Combine(dir,prefix+"-"+p+".json")).ToArray();
    }
    private static string Stamp(string[] paths)=>string.Join("|",paths.Select(p=>{var f=new FileInfo(p);return f.Exists?$"{f.Length}:{f.LastWriteTimeUtc.Ticks}":"missing";}));
    private static string? Str(JsonElement j,string key)=>j.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
    private static int? Int(JsonElement j,string key)=>j.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out int n)?n:null;
    private static bool Coord(JsonElement j,out double x,out double z)
    {
        x=z=0;
        return j.TryGetProperty("x",out var a)&&a.ValueKind==JsonValueKind.Number&&a.TryGetDouble(out x)&&
            j.TryGetProperty("y",out var b)&&b.ValueKind==JsonValueKind.Number&&b.TryGetDouble(out z)&&
            double.IsFinite(x)&&double.IsFinite(z)&&Math.Abs(x)<=2000000&&Math.Abs(z)<=2000000;
    }
    private static string? Code(string game,string? s)
    {
        if(string.IsNullOrWhiteSpace(s))return null;
        s=s.Trim();if(game=="ATS"&&s.StartsWith("US-",StringComparison.OrdinalIgnoreCase))s=s[3..];
        if(game=="ETS2")s=s.ToLowerInvariant() switch{"czech"=>"CZ","uk" or "great_britain"=>"UK","the_netherlands"=>"NL","bosnia"=>"BIH","north_macedonia"=>"MK",_=>s};
        return game=="ATS"?AtsStateCodes.FromStateName(s.Replace('_',' ')):TachographCountryCodes.FromCountryName(s.Replace('_',' '));
    }
    public static string NormalizeCity(string? city)
    {
        var value = (city ?? "").Trim().ToLowerInvariant().Replace('ł', 'l').Replace('ø', 'o').Replace('đ','d');
        var normalized = value.Normalize(NormalizationForm.FormD);
        var output = new StringBuilder(normalized.Length);
        var separator = false;
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch))
            {
                if (separator && output.Length > 0) output.Append('_');
                output.Append(ch); separator = false;
            }
            else separator = true;
        }
        return output.ToString();
    }
    private static string CityKey(string? s)=>NormalizeCity(s) switch
    {"warsaw"=>"warszawa","brussels"=>"brussel","lisbon"=>"lisboa","gothenburg"=>"goteborg","cologne"=>"koln","belgrade"=>"beograd",var k=>k};
    private static async Task<Index> LoadAsync(string game,string[] paths,CancellationToken ct)
    {
        var data=new Index();var ids=new Dictionary<int,Region>();var tokens=new Dictionary<string,Region>(StringComparer.OrdinalIgnoreCase);
        using(var doc=JsonDocument.Parse(File.ReadAllBytes(paths[0])))
        foreach(var j in doc.RootElement.EnumerateArray())
        {
            var token=Str(j,"token");var name=Str(j,"name");var code=Code(game,name)??Code(game,token)??Code(game,Str(j,"code"));
            if(code is null||Int(j,"id") is not int id||string.IsNullOrWhiteSpace(token))continue;
            var r=new Region(code,name??token);if(!ids.TryAdd(id,r)||!tokens.TryAdd(token,r))throw new InvalidDataException("Duplicate SCS region ID/token.");
            data.Regions.Add(code);
        }
        if(ids.Count==0)throw new InvalidDataException("No supported SCS regions.");
        using(var doc=JsonDocument.Parse(File.ReadAllBytes(paths[1])))
        foreach(var j in doc.RootElement.EnumerateArray())
        {
            if(Str(j,"countryToken") is not string token||!tokens.TryGetValue(token,out var r))continue;
            data.CityCount++;
            foreach(var field in new[]{"token","name","nameLocalized"})
            {
                var key=CityKey(Str(j,field));if(key.Length==0)continue;
                if(data.Cities.TryGetValue(key,out var prev)){if(prev?.Code!=r.Code)data.Cities[key]=null;}
                else data.Cities.Add(key,r);
            }
        }
        // Stream the potentially large node JSON rather than retaining raw documents.
        await using var stream=File.OpenRead(paths[2]);
        await foreach(var j in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream,cancellationToken:ct))
        {
            if(j.ValueKind!=JsonValueKind.Object||!Coord(j,out var x,out var z))continue;
            ids.TryGetValue(Int(j,"forwardCountryId")??int.MinValue,out var a);
            ids.TryGetValue(Int(j,"backwardCountryId")??int.MinValue,out var b);
            if(a is null&&b is null)continue;
            // Both flags are evidence; a mixed node is never silently assigned to one side.
            var tile=Tile(x,z);if(!data.Grid.TryGetValue(tile,out var points))data.Grid[tile]=points=new();
            if(a!=null)points.Add(new(x,z,a));
            if(b!=null&&a?.Code!=b.Code){points.Add(new(x,z,b));if(a!=null)data.Conflicts++;}
            data.Nodes++;
        }
        if(data.Nodes==0||data.CityCount==0)throw new InvalidDataException("SCS node or city region fields missing; export schema not supported.");
        return data;
    }
    private async Task<Index?> GetAsync(string game,CancellationToken ct)
    {
        if(!ValidGame(game))return null;
        var slot=_slots.GetOrAdd(game,_=>new Slot());var paths=Paths(game);
        await slot.Gate.WaitAsync(ct);
        try
        {
            var stamp=Stamp(paths);if(slot.Stamp==stamp&&(slot.Data!=null||DateTime.UtcNow<slot.Retry))return slot.Data;
            slot.Data=null;slot.Stamp=stamp;slot.Status="loading";
            try
            {
                if(paths.Any(p=>!File.Exists(p)))throw new FileNotFoundException("Local SCS countries/cities/nodes missing. Export map in Tools.");
                var index=await Task.Run(()=>LoadAsync(game,paths,ct),ct);
                if(stamp!=Stamp(paths))throw new InvalidDataException("Map changed during read; retry.");
                slot.Data=index;slot.Status=$"{index.Regions.Count} regions / {index.CityCount} cities / {index.Nodes} nodes / {index.Conflicts} mixed nodes";
            }
            catch(Exception ex) when(!ct.IsCancellationRequested&&(ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is JsonException || ex is InvalidOperationException || ex is ArgumentException))
            {slot.Status=ex.Message;slot.Retry=DateTime.UtcNow.AddSeconds(10);}
            return slot.Data;
        }
        finally{slot.Gate.Release();}
    }
    internal bool HasData(string game)=>_slots.TryGetValue(game,out var s)&&s.Data!=null;
    internal string Status(string game)=>_slots.TryGetValue(game,out var s)?s.Status:"not loaded";
    internal async Task<string> InspectAsync(string game,CancellationToken ct){await GetAsync(game,ct);return Status(game);}
    public async Task<string?> LookupCityAsync(string game,string? city,CancellationToken ct)
    {
        var key=CityKey(city);if(key.Length==0)return null;
        var data=await GetAsync(game,ct);return data!=null&&data.Cities.TryGetValue(key,out var r)?r?.Code:null;
    }
    public async Task<RegionResult> ResolveAsync(string game,double x,double z,CancellationToken ct)
    {
        if(!ValidGame(game)||!double.IsFinite(x)||!double.IsFinite(z)||Math.Abs(x)>2000000||Math.Abs(z)>2000000)return Fail("Nieprawidłowa pozycja.");
        var data=await GetAsync(game,ct);if(data==null)return Fail("Brak lokalnych danych SCS. Wyeksportuj mapę w Tools.");
        var nearest=new Dictionary<string,(double Distance,Region Region)>(StringComparer.Ordinal);
        var min=Tile(x-Radius,z-Radius);var max=Tile(x+Radius,z+Radius);
        for(int a=min.Item1;a<=max.Item1;a++)for(int b=min.Item2;b<=max.Item2;b++)
        if(data.Grid.TryGetValue((a,b),out var points))foreach(var p in points)
        {
            double dx=x-p.X,dz=z-p.Z,d=dx*dx+dz*dz;
            if(d<=Radius*Radius&&(!nearest.TryGetValue(p.Region.Code,out var old)||d<old.Distance))nearest[p.Region.Code]=(d,p.Region);
        }
        var sorted=nearest.Values.OrderBy(n=>n.Distance).ToArray();
        if(sorted.Length==0)return Fail("Brak pobliskiego węzła SCS z regionem.");
        if(sorted.Length>1&&Math.Sqrt(sorted[1].Distance)-Math.Sqrt(sorted[0].Distance)<BorderMargin)return Fail("Granica regionów: niejednoznaczne dane SCS. Wybierz ręcznie.");
        var best=sorted[0];return new(true,best.Region.Name,best.Region.Code,game=="ATS"?"state":"country",false,"local-scs-node","Region z lokalnych węzłów SCS; sprawdź przed zapisem.");
    }
    private static RegionResult Fail(string message)=>new(false,null,null,null,false,null,message);
}
