using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoadChron;

// Only patch verified price, sign text and currency fields in existing definitions.
// Source formats are deliberately validated: a changed web layout must not produce a mod.
internal static class RealFuelPrices
{
    internal const string AtsSource = "https://gasprices.aaa.com/todays-state-averages/";
    internal const string EtsSource = "https://www.euenergyprices.eu/en/fuel";
    internal const string AmzsSource = "https://www.amzs.si/na-poti/cene-goriv-po-evropi";
    internal const decimal LitersPerUsGallon = 3.785411784m;
    internal const string ModName = "roadchron_real_fuel_prices.scs";
    internal sealed record Price(decimal? Petrol, decimal? Diesel, decimal? MidGrade = null, decimal? Premium = null);
    internal sealed record Feed(DateTime Date, Dictionary<string, Price> Prices, Dictionary<string, DateTime>? RegionDates = null, Dictionary<string,string>? Sources = null, Dictionary<string,DateTime>? Expired = null)
    {
        internal DateTime DateFor(string key) => RegionDates?.GetValueOrDefault(key, Date) ?? Date;
    }

    internal static Feed MergeObservations(Feed current, Feed? cached, bool ats, DateTime today)
    {
        if (!ValidFeed(cached, ats, today)) return current;
        var prices = new Dictionary<string, Price>(current.Prices, StringComparer.Ordinal);
        var dates = current.Prices.Keys.ToDictionary(key => key, current.DateFor, StringComparer.Ordinal);
        var sources = current.Prices.Keys.ToDictionary(key => key, key => current.Sources?.GetValueOrDefault(key) ?? (ats ? AtsSource : EtsSource));
        foreach (var (key, price) in cached!.Prices)
        {
            var date = cached.DateFor(key);
            if (dates.TryGetValue(key, out var currentDate) && currentDate >= date) continue;
            prices[key] = price;
            dates[key] = date;
            sources[key] = cached.Sources?.GetValueOrDefault(key) ?? (ats ? AtsSource : EtsSource);
        }
        return new(dates.Values.Max(), prices, dates, sources, current.Expired);
    }
    private static readonly Regex Row = new(@"<tr\b[^>]*>(.*?)</tr\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex Cell = new(@"<t[dh]\b[^>]*>(.*?)</t[dh]\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static string Plain(string value) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " ")), @"\s+", " ").Trim();
    internal static string Key(string value) => new(value.ToLowerInvariant().Where(c => c is >= 'a' and <= 'z').ToArray());
    private static readonly HashSet<string> UsStates = new(("Alabama|Alaska|Arizona|Arkansas|California|Colorado|Connecticut|Delaware|District of Columbia|Florida|Georgia|Hawaii|Idaho|Illinois|Indiana|Iowa|Kansas|Kentucky|Louisiana|Maine|Maryland|Massachusetts|Michigan|Minnesota|Mississippi|Missouri|Montana|Nebraska|Nevada|New Hampshire|New Jersey|New Mexico|New York|North Carolina|North Dakota|Ohio|Oklahoma|Oregon|Pennsylvania|Rhode Island|South Carolina|South Dakota|Tennessee|Texas|Utah|Vermont|Virginia|Washington|West Virginia|Wisconsin|Wyoming").Split('|').Select(Key), StringComparer.Ordinal);
    private static readonly HashSet<string> EuCountries = new(("Austria|Belgium|Bulgaria|Croatia|Cyprus|Czechia|Czech Republic|Denmark|Estonia|Finland|France|Germany|Greece|Hungary|Ireland|Italy|Latvia|Lithuania|Luxembourg|Malta|Netherlands|Norway|Poland|Portugal|Romania|Slovakia|Slovenia|Spain|Sweden|Switzerland|United Kingdom").Split('|').Select(Key), StringComparer.Ordinal);
    internal static readonly Dictionary<string, string> EuropeanCountryCodes = ("at:Austria|be:Belgium|bg:Bulgaria|hr:Croatia|cy:Cyprus|cz:Czechia|dk:Denmark|ee:Estonia|fi:Finland|fr:France|de:Germany|gr:Greece|hu:Hungary|ie:Ireland|it:Italy|lv:Latvia|lt:Lithuania|lu:Luxembourg|mt:Malta|nl:Netherlands|no:Norway|pl:Poland|pt:Portugal|ro:Romania|sk:Slovakia|si:Slovenia|es:Spain|se:Sweden|ch:Switzerland|gb:United Kingdom")
        .Split('|').Select(x => x.Split(':')).ToDictionary(x => Key(x[1]), x => x[0], StringComparer.Ordinal);
    internal static string[] DiagnosticRegions(bool ats) => ats ? UsStates.Order().ToArray() : EuCodes.Values.Except(["cyprus","malta","ireland"]).Distinct().Order().ToArray();
    internal static string[] GameCountries(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".sui" or ".sii").Select(CountryKey).Distinct().Order().ToArray() : [];
    internal static string? RegionCode(string key) => EuCodes.FirstOrDefault(p => p.Value==key).Key;
    internal static string CountryKey(string filename) => Key(Path.GetFileNameWithoutExtension(filename)) switch
    {
        "uk" or "greatbritain" or "britain" or "england" => "unitedkingdom",
        "czech" or "czechrepublic" => "czechia",
        "holland" => "netherlands",
        "bosniaherzegovina" or "bosniaandherzegovina" => "bosnia",
        "northmacedonia" => "macedonia",
        var key => key
    };
    internal static Feed ParseAmzs(string html, DateTime today)
    {
        var prices = new Dictionary<string,Price>();
        var dates = new Dictionary<string,DateTime>();
        var expired = new Dictionary<string,DateTime>();
        foreach (Match table in Regex.Matches(html, @"<table\b[^>]*>(.*?)</table\s*>", RegexOptions.Singleline|RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
        {
            var rows = Row.Matches(table.Groups[1].Value);
            if (rows.Count<2 || !Cell.Matches(rows[0].Value).Select(c=>Plain(c.Groups[1].Value)).SequenceEqual(["Država","95","98","Diesel","Zadnja sprememba"])) continue;
            foreach (Match row in rows.Skip(1))
            {
                var cells=Cell.Matches(row.Value);
                if (cells.Count!=5) continue;
                var flag=Regex.Match(cells[0].Value,@"\bflag-icon-([a-z]{2})\b");
                if (!EuCodes.TryGetValue(flag.Groups[1].Value,out var country)) continue;
                if (!DateTime.TryParseExact(Plain(cells[4].Groups[1].Value),"d. M. yyyy",CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces,out var date) ||
                    date.Date>today.Date) continue;
                if(date.Date<today.Date.AddDays(-14)){expired[country]=date;continue;}
                decimal? Euros(Match cell)
                {
                    var match=Regex.Match(Plain(cell.Groups[1].Value), @"^(?:\d+(?:[,.]\d+)? [A-Z]{3} \((?<eur>\d+(?:[,.]\d+)?) EUR\)|(?<eur>\d+(?:[,.]\d+)?) EUR)$");
                    return decimal.TryParse(match.Groups["eur"].Value.Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value)&&ValidPrice(value)?value:null;
                }
                if (Euros(cells[3]) is not decimal diesel) continue;
                if (!prices.TryAdd(country,new(Euros(cells[1]),diesel))) throw new InvalidDataException("Duplicate country in AMZS table");
                dates[country]=date;
            }
            break;
        }
        if (prices.Count==0) throw new InvalidDataException("No dated EUR/l observations in the AMZS table.");
        return new(dates.Values.Max(),prices,dates,prices.Keys.ToDictionary(k=>k,_=>AmzsSource),expired);
    }
    internal static Feed Parse(string html, bool ats, DateTime today)
    {
        string body = Plain(Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1\s*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline));
        var dateMatch = ats ? Regex.Match(body, @"Price\s+as\s+of\s+(\d{1,2}/\d{1,2}/\d{2,4})", RegexOptions.IgnoreCase)
            : Regex.Match(body, @"Weekly\s+consumer\s+prices\s*[^\d]{0,12}(\d{1,2}\s+[A-Za-z]+\s+\d{4})", RegexOptions.IgnoreCase);
        var formats = ats ? new[] { "M/d/yy", "M/d/yyyy" } : new[] { "d MMM yyyy", "d MMMM yyyy" };
        if (!dateMatch.Success || !DateTime.TryParseExact(dateMatch.Groups[1].Value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
            || date.Date > today.Date.AddDays(1) || date.Date < today.Date.AddDays(ats ? -7 : -45))
            throw new InvalidDataException("Price feed date is missing, invalid or stale.");
        var prices = new Dictionary<string, Price>(StringComparer.Ordinal);
        bool header = false;
        foreach (Match row in Row.Matches(html))
        {
            string[] fields = Cell.Matches(row.Groups[1].Value).Cast<Match>().Select(c => Plain(c.Groups[1].Value)).ToArray();
            if (ats && fields.Length == 5 && Key(fields[0]) == "state" && Key(fields[1]) == "regular" && Key(fields[2]) == "midgrade" && Key(fields[3]) == "premium" && Key(fields[4]) == "diesel") { header = true; continue; }
            if (!ats && fields.Length >= 3 && Key(fields[^3]) == "country" && Key(fields[^2]) == "petrol" && Key(fields[^1]) == "diesel") { header = true; continue; }
            if (!header) continue;
            if (ats && fields.Length != 5 || !ats && fields.Length is not (3 or 4)) continue;
            string key = Key(ats ? fields[0] : fields[^3]);
            if (!(ats ? UsStates : EuCountries).Contains(key)) continue;
            if (key == "czechrepublic") key = "czechia";
            string petrol = ats ? fields[1] : fields[^2], diesel = fields[^1];
            decimal? a = ParseAmount(petrol, ats), b = ParseAmount(diesel, ats);
            if (ats ? a is null && b is null : b is null) continue;
            if (!prices.TryAdd(key, new Price(a, b, ats ? ParseAmount(fields[2], true) : null, ats ? ParseAmount(fields[3], true) : null))) throw new InvalidDataException("Duplicate region in feed: " + key);
        }
        if (!ats && prices.Count==0)
        {
            // New server-rendered catalog contains two named series in EUR/l.
            // The weekly bulletin date only describes EU members. Other rows
            // can be monthly observations and require an individual date.
            var catalog=html.Replace("\\\"","\"",StringComparison.Ordinal);
            var series=new Dictionary<string,Dictionary<string,decimal>>();
            foreach(Match part in Regex.Matches(catalog,"\"mode\":\"(gasoline|diesel)\",\"entries\":(\\[[^\\[\\]]{1,32000}\\])",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(2)))
            {
                using var json=JsonDocument.Parse(part.Groups[2].Value);
                var values=new Dictionary<string,decimal>();
                foreach(var row in json.RootElement.EnumerateArray()){
                    if(row.ValueKind!=JsonValueKind.Object||!row.TryGetProperty("slug",out var slug)||slug.ValueKind!=JsonValueKind.String||
                       !row.TryGetProperty("countryCode",out var code)||code.ValueKind!=JsonValueKind.String||
                       !row.TryGetProperty("name",out var name)||name.ValueKind!=JsonValueKind.String||
                       !row.TryGetProperty("value",out var val)||val.ValueKind!=JsonValueKind.Number||!val.TryGetDecimal(out var amount)||!ValidPrice(amount))continue;
                    string key=Key(name.GetString()!);
                    if(key is "switzerland" or "norway" or "unitedkingdom"||!EuropeanCountryCodes.TryGetValue(key,out var iso)||
                       slug.GetString()!=iso||code.GetString()!=iso.ToUpperInvariant())continue;
                    if(!values.TryAdd(key,amount))throw new InvalidDataException("Duplicate European price row");
                }
                if(!series.TryAdd(part.Groups[1].Value,values))throw new InvalidDataException("Ambiguous European price series");
            }
            if(series.TryGetValue("diesel",out var diesel)&&series.TryGetValue("gasoline",out var petrol))
                foreach(var (key,value) in diesel)prices[key]=new(petrol.TryGetValue(key,out var gas)?gas:null,value);
        }
        if (prices.Count == 0) throw new InvalidDataException("No verified prices in the main table.");
        return new(date.Date, prices);
    }
    // The redesigned Europe landing page may render only petrol. Country pages
    // expose an explicitly labelled historical table with both fuels and dates.
    internal static (DateTime Date, Price Price) ParseEuropeanCountry(string html, string countryKey, DateTime today)
    {
        // Verify identity against the heading, not merely the requested URL.
        var heading = Regex.Match(html, @"<h1\b[^>]*>(.*?)</h1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!heading.Success || Key(Plain(heading.Groups[1].Value)) != "fuelpricesin" + countryKey)
            throw new InvalidDataException("Unexpected country page.");
        bool header = false;
        foreach (Match row in Row.Matches(html))
        {
            var f = Cell.Matches(row.Groups[1].Value).Cast<Match>().Select(c => Plain(c.Groups[1].Value)).ToArray();
            if (f.Length == 3 && Key(f[0]) == "week" && Key(f[1]) == "petrol" && Key(f[2]) == "diesel") { header = true; continue; }
            if (!header || f.Length != 3) continue;
            string dateText = f[0].Replace("Sept", "Sep", StringComparison.OrdinalIgnoreCase);
            if (!DateTime.TryParseExact(dateText, new[] { "d MMM yyyy", "d MMMM yyyy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
                throw new InvalidDataException("Invalid country observation date.");
            if (date.Date > today.Date.AddDays(1) || date.Date < today.Date.AddDays(-45))
                throw new InvalidDataException("Country observation is stale.");
            var diesel = ParseAmount(f[2], false);
            if (diesel is null) throw new InvalidDataException("No valid diesel price for latest country observation.");
            return (date.Date, new Price(ParseAmount(f[1], false), diesel));
        }
        throw new InvalidDataException("No labelled diesel history table.");
    }
    private static decimal? ParseAmount(string text, bool ats)
    {
        var match = Regex.Match(text, ats ? @"^\$\s*(\d+(?:\.\d+)?)$" : @"^(?:€\s*)?(\d+(?:[.,]\d+)?)\s*(ct|€|EUR)?\s*/\s*L\s*[↑↓↔]?\s*$", RegexOptions.IgnoreCase);
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal amount)) return null;
        if (ats) amount /= LitersPerUsGallon;
        else if (match.Groups[2].Value.Equals("ct", StringComparison.OrdinalIgnoreCase)) amount /= 100m;
        else if (!match.Groups[2].Success && !text.StartsWith('€')) return null; // never guess units
        return amount is >= 0.15m and <= 8m ? amount : null;
    }
    // Cache files are external input too. Never trust deserialization alone.
    private static bool ValidPrice(decimal? value) => value is >= 0.15m and <= 8m;
    internal static bool ValidFeed(Feed? feed, bool ats, DateTime today)
    {
        if (feed is null || feed.Date == DateTime.MinValue || feed.Date.Date > today.Date.AddDays(1)
            || feed.Prices is null || feed.Prices.Count == 0) return false;
        foreach (var (key, price) in feed.Prices)
        {
            if (!(ats ? UsStates.Contains(key) : EuCodes.ContainsValue(key)) || price is null) return false;
            var date = feed.DateFor(key);
            if (date == DateTime.MinValue || date.Date > feed.Date.Date) return false;
            if (ats ? !ValidPrice(price.Petrol) && !ValidPrice(price.Diesel) : !ValidPrice(price.Diesel))
                return false;
            if (new[] { price.Petrol, price.Diesel, price.MidGrade, price.Premium }
                .Any(value => value.HasValue && !ValidPrice(value))) return false;
        }
        return true;
    }

    private static bool ValidRate(decimal value) => value is >= 0.000001m and <= 1000000m;
    internal static bool ValidRates(IReadOnlyDictionary<string, decimal>? rates)
    {
        return rates is not null && rates.TryGetValue("EUR", out var eur) && eur == 1m
            && rates.TryGetValue("USD", out var usd) && ValidRate(usd)
            && rates.All(item => Regex.IsMatch(item.Key, "^[A-Z]{3}$") && ValidRate(item.Value));
    }

    private static readonly Regex PriceField = new(@"(?m)^(?<prefix>[\t ]*)(?<field>fuel_price|petrol_price)(?<separator>[\t ]*:[\t ]*)(?<value>&[0-9a-fA-F]{8}|[+-]?\d+(?:\.\d*)?(?:[eE][+-]?\d+)?)(?=[\t ]*(?:\r?$|//|#))");
    internal static string? Patch(string original, Price price, bool ats)
    {
        var matches = PriceField.Matches(original).Cast<Match>().ToArray();
        if (matches.Count(m => m.Groups["field"].Value == "fuel_price") != 1 || matches.Count(m => m.Groups["field"].Value == "petrol_price") > 1) return null;
        // Only update a fuel field supplied by this game definition.
        return PriceField.Replace(original, m =>
        {
            string field = m.Groups["field"].Value;
            decimal? value = field == "fuel_price" ? price.Diesel : price.Petrol;
            if (!ValidPrice(value)) return m.Value;
            return m.Groups["prefix"].Value + field + m.Groups["separator"].Value + value!.Value.ToString("0.0000", CultureInfo.InvariantCulture);
        });
    }
    internal static string DateLabel(DateTime date) => date == DateTime.MinValue ? "none (game defaults)" : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    internal static async Task PublishAsync(string staged,string target,Action requireClosed,CancellationToken stop)
    {
        // Both files are in the mod directory. Rename never leaves a window with no installed mod.
        for(int attempt=0;;attempt++)
        {
            stop.ThrowIfCancellationRequested();
            requireClosed();
            try { File.Move(staged,target,overwrite:true); return; }
            catch(Exception ex) when(attempt<5 && (ex is IOException or UnauthorizedAccessException) && (ex.HResult & 0xffff) is 5 or 32 or 33 or 1175)
            { await Task.Delay(TimeSpan.FromMilliseconds(100 << attempt),stop); }
        }
    }
    internal static int BuildMod(string countryDirectory, string output, Feed feed, bool ats, bool template = false, string? definitions = null, IReadOnlyDictionary<string, decimal>? rates = null)
    {
        int count = 0;
        using var archive = ZipFile.Open(output, ZipArchiveMode.Create);
        foreach (string file in Directory.EnumerateFiles(countryDirectory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!new[] { ".sui", ".sii" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            if (!feed.Prices.TryGetValue(CountryKey(file), out var price)) continue;
            // A bundled template has placeholders, not original game prices. Never publish
            // an ATS definition unless both prices are verified, or an ETS2 one without diesel.
            if (new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("Unexpectedly large country file.");
            string original = File.ReadAllText(file, new UTF8Encoding(false, true));
            if (template && (price.Diesel is null || (price.Petrol is null && (ats || PriceField.Matches(original).Any(m=>m.Groups["field"].Value=="petrol_price"))))) continue;
            string? patched = Patch(original, price, ats);
            if (patched is null || patched == original) continue;
            Write(archive, "def/country/" + Path.GetFileName(file), patched);
            count++;
        }
        if (definitions is not null)
        {
            string board = Path.Combine(definitions, "board");
            string locale = Path.Combine(definitions, "fuel_price_strings.sui");
            var formats = File.Exists(locale) ? ReadPylonFormats(File.ReadAllText(locale)) : new Dictionary<string, string>();
            if (Directory.Exists(board))
                foreach (string file in Directory.EnumerateFiles(board, "*.sii", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
                {
                    string original = File.ReadAllText(file);
                    string patched = PatchPylon(original, feed, ats, rates, formats);
                    if (patched == original) continue;
                    Write(archive, "def/sign/board/" + Path.GetRelativePath(board, file).Replace('\\', '/'), patched);
                    count++;
                }
            string economy = Path.Combine(definitions, "economy_data.sii");
            if (rates is not null && File.Exists(economy))
            {
                string original = File.ReadAllText(economy), patched = PatchCurrency(original, rates, ats);
                if (original != patched) { Write(archive, "def/economy_data.sii", patched); count++; }
            }
        }
        // Missing countries are deliberately omitted, so the game uses its original definitions.
        // A valid partial feed may contain just one matching region.
        Write(archive, "manifest.sii", "SiiNunit\n{\nmod_package : .package_name {\n package_version: \"0.4\"\n display_name: \"RoadChron Real Fuel Prices 0.4 AUTO\"\n author: \"RoadChron\"\n category[]: \"other\"\n description_file: \"mod_description.txt\"\n}\n}\n");
        var oldest = feed.Prices.Keys.Select(feed.DateFor).DefaultIfEmpty(feed.Date).Min();
        var dates = oldest.Date == feed.Date.Date ? DateLabel(feed.Date) : $"{DateLabel(oldest)} to {DateLabel(feed.Date)} (includes cached observations)";
        Write(archive, "mod_description.txt", "RoadChron Real Fuel Prices 0.4 AUTO\nObservation dates: " + dates + "\n" + (ats ? "Source: AAA Fuel Prices, " + AtsSource : "Sources: AMZS " + AmzsSource + "; EU EnergyPrices " + EtsSource + " (CC BY 4.0) where used.") + "\nCountry fuel definitions, sign board prices and currency rates. Exchange rates: " + AdBlueRealPrices.FxUrl + " (where available).\n");
        return count;
    }
    private static readonly Dictionary<string,string> UsCodes = ("AL:Alabama|AK:Alaska|AZ:Arizona|AR:Arkansas|CA:California|CO:Colorado|CT:Connecticut|DE:Delaware|DC:District of Columbia|FL:Florida|GA:Georgia|HI:Hawaii|ID:Idaho|IL:Illinois|IN:Indiana|IA:Iowa|KS:Kansas|KY:Kentucky|LA:Louisiana|ME:Maine|MD:Maryland|MA:Massachusetts|MI:Michigan|MN:Minnesota|MS:Mississippi|MO:Missouri|MT:Montana|NE:Nebraska|NV:Nevada|NH:New Hampshire|NJ:New Jersey|NM:New Mexico|NY:New York|NC:North Carolina|ND:North Dakota|OH:Ohio|OK:Oklahoma|OR:Oregon|PA:Pennsylvania|RI:Rhode Island|SC:South Carolina|SD:South Dakota|TN:Tennessee|TX:Texas|UT:Utah|VT:Vermont|VA:Virginia|WA:Washington|WV:West Virginia|WI:Wisconsin|WY:Wyoming").Split('|').Select(v=>v.Split(':')).ToDictionary(v=>v[0].ToLowerInvariant(),v=>Key(v[1]));
    private static readonly Dictionary<string,string> EuCodes = EuropeanCountryCodes.ToDictionary(v=>v.Value,v=>v.Key)
        .Concat(new Dictionary<string,string>{{"al","albania"},{"ba","bosnia"},{"is","iceland"},{"ks","kosovo"},{"mk","macedonia"},{"me","montenegro"},{"ru","russia"},{"rs","serbia"},{"tr","turkey"}}).ToDictionary();
    private static readonly Dictionary<string,string> LocalCurrency = ("at:EUR|be:EUR|bg:EUR|hr:EUR|cy:EUR|cz:CZK|dk:DKK|ee:EUR|fi:EUR|fr:EUR|de:EUR|gr:EUR|hu:HUF|ie:EUR|it:EUR|lv:EUR|lt:EUR|lu:EUR|mt:EUR|nl:EUR|no:NOK|pl:PLN|pt:EUR|ro:RON|sk:EUR|si:EUR|es:EUR|se:SEK|ch:CHF|gb:GBP|al:ALL|ba:BAM|is:ISK|ks:EUR|mk:MKD|me:EUR|ru:RUB|rs:RSD|tr:TRY").Split('|').Select(v=>v.Split(':')).ToDictionary(v=>v[0],v=>v[1]);
    internal static Dictionary<string, string> ReadPylonFormats(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, "key\\[\\]\\s*:\\s*\"(fp_[a-z0-9_]+)\"\\s*val\\[\\]\\s*:\\s*\"([0-9]+(?:\\.[0-9]+)?)\""))
            result.TryAdd(m.Groups[1].Value,m.Groups[2].Value);
        return result;
    }
    internal static string PatchPylon(string original, Feed feed, bool ats, IReadOnlyDictionary<string, decimal>? rates, IReadOnlyDictionary<string, string> formats)
    {
        // Same substitution as the in-game verified V4. Only text fields, never models or sign IDs.
        return Regex.Replace(original, "(?m)^(?<prefix>[ \\t]*text[ \\t]*:[ \\t]*\")@@(?<key>fp_(?<region>[a-z]{2})_(?<grade>[a-z0-9_]+))@@(?=\")", m =>
        {
            string region = m.Groups["region"].Value;
            string grade = m.Groups["grade"].Value;
            string key = m.Groups["key"].Value;
            if (!(ats ? UsCodes : EuCodes).TryGetValue(region,out var country) || !feed.Prices.TryGetValue(country,out var price)) return m.Value;
            decimal? value = grade switch { "diesel" => price.Diesel, "regular" when ats => price.Petrol, "plus" when ats => price.MidGrade, "premium" when ats => price.Premium, "natural95" when !ats => price.Petrol, _ => null };
            if (!ValidPrice(value)) return m.Value;
            decimal amount = value!.Value;
            if (ats)
            {
                amount *= LitersPerUsGallon;
            }
            else
            {
                if (!LocalCurrency.TryGetValue(region, out var currency)) return m.Value;
                if (currency != "EUR")
                {
                    if (rates is null || !rates.TryGetValue(currency, out var rate) || !ValidRate(rate))
                        return m.Value;
                    amount *= rate;
                }
            }

            int decimals = 2;
            if (formats.TryGetValue(key, out var old))
            {
                int dot = old.IndexOf('.');
                decimals = dot < 0 ? 0 : Math.Min(3, old.Length - dot - 1);
            }
            if (!ats && region=="cz") amount=Math.Round(amount,1,MidpointRounding.AwayFromZero);
            return m.Groups["prefix"].Value+Math.Round(amount,decimals,MidpointRounding.AwayFromZero).ToString("F"+decimals,CultureInfo.InvariantCulture);
        });
    }
    internal static string PatchCurrency(string original, IReadOnlyDictionary<string, decimal> eurRates, bool ats)
    {
        decimal divisor = 1m;
        if (ats && (!eurRates.TryGetValue("USD",out divisor) || !ValidRate(divisor))) return original;
        // In the original economy definition each code is immediately followed by its ratio.
        return Regex.Replace(original, "(?m)^(?<prefix>[ \\t]*currency_code\\[\\][ \\t]*:[ \\t]*\"(?<code>[A-Z]{3})\"[ \\t]*\\r?\\n[ \\t]*currency_ratio\\[\\][ \\t]*:[ \\t]*)(?<value>[0-9]+(?:\\.[0-9]+)?)(?=[ \\t]*(?:\\r?$|#|//))", m =>
        {
            string code = m.Groups["code"].Value;
            if (code == (ats ? "USD" : "EUR")) return m.Groups["prefix"].Value + "1";
            decimal rate;
            if (code == "EUR") rate = 1m;
            else if (!eurRates.TryGetValue(code, out rate)) return m.Value;
            if (!ValidRate(rate)) return m.Value;
            decimal converted = rate / divisor;
            if (!ValidRate(converted)) return m.Value;
            return m.Groups["prefix"].Value + converted.ToString("0.########", CultureInfo.InvariantCulture);
        });
    }

    private static void Write(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.ExternalAttributes = 0;
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
