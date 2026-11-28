using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoadChron;

// Only patch verified price, sign text and currency fields in existing definitions.
// Source formats are deliberately validated: a changed web layout must not produce a mod.
internal static class RealFuelPrices
{
    internal const string UsGasGaugeSource = "https://usgasgauge.com/gas-price-data";
    internal const string UsGasGaugeRegular = "https://usgasgauge.com/gas-price-data/gas-prices-by-state-current.csv";
    internal const string UsGasGaugeDiesel = "https://usgasgauge.com/gas-price-data/diesel-prices-weekly.csv";
    // ATS rows tagged US Gas Gauge combine that provider with EIA for missing diesel regions and other grades.
    internal static bool ApprovedUsSource(string? source) => source is EiaSource or UsGasGaugeSource;
    internal static bool ApprovedFuelSource(string? source, bool ats) => ats ? ApprovedUsSource(source) : source == OpenVanSource;
    internal const string OpenVanSource = "https://openvan.camp/";
    internal const string OpenVanPrices = "https://openvan.camp/api/fuel/prices?source=RoadChron";
    internal const string OpenVanFx = "https://openvan.camp/api/currency/rates?source=RoadChron";
    internal const string EiaSource = "https://www.eia.gov/dnav/pet/pet_pri_gnd_a_epmr_pte_dpgal_w.htm";
    internal static readonly string[] EiaPages = [EiaSource,
        "https://www.eia.gov/dnav/pet/pet_pri_gnd_a_epd2d_pte_dpgal_w.htm",
        "https://www.eia.gov/dnav/pet/pet_pri_gnd_a_epmm_pte_dpgal_w.htm",
        "https://www.eia.gov/dnav/pet/pet_pri_gnd_a_epmp_pte_dpgal_w.htm"];
    internal static readonly string[] FuelFields = ["Petrol", "Diesel", "MidGrade", "Premium"];
    internal const decimal LitersPerUsGallon = 3.785411784m;
    internal const string ModName = "roadchron_real_fuel_prices.scs";
    internal sealed record Price(decimal? Petrol, decimal? Diesel, decimal? MidGrade = null, decimal? Premium = null);
    internal sealed record Feed(DateTime Date, Dictionary<string, Price> Prices, Dictionary<string, DateTime>? RegionDates = null, Dictionary<string,string>? Sources = null, Dictionary<string,DateTime>? Expired = null, Dictionary<string,string[]>? Estimated = null)
    {
        internal bool IsEstimated(string key, string field) => Estimated?.GetValueOrDefault(key)?.Contains(field, StringComparer.Ordinal) == true;
        internal DateTime DateFor(string key) => RegionDates?.GetValueOrDefault(key, Date) ?? Date;
    }

    internal static Feed MergeObservations(Feed current, Feed? cached, bool ats, DateTime today)
    {
        if (!ValidFeed(cached, ats, today)) return current;
        var prices = new Dictionary<string, Price>(current.Prices, StringComparer.Ordinal);
        var dates = current.Prices.Keys.ToDictionary(key => key, current.DateFor, StringComparer.Ordinal);
        var sources = current.Prices.Keys.ToDictionary(key => key, key => current.Sources?.GetValueOrDefault(key) ?? "RoadChron legacy cache (source unspecified)");
        var estimated = current.Estimated == null ? new Dictionary<string,string[]>(StringComparer.Ordinal) : new Dictionary<string,string[]>(current.Estimated, StringComparer.Ordinal);
        foreach (var (key, price) in cached!.Prices)
        {
            var date = cached.DateFor(key);
            if (current.Prices.ContainsKey(key) && ApprovedFuelSource(current.Sources?.GetValueOrDefault(key), ats) &&
                !ApprovedFuelSource(cached.Sources?.GetValueOrDefault(key), ats)) continue;
            if (dates.TryGetValue(key, out var currentDate) && currentDate >= date) continue;
            prices[key] = price;
            estimated.Remove(key);
            if(cached.Estimated?.TryGetValue(key,out var flags)==true)estimated[key]=flags;
            dates[key] = date;
            sources[key] = cached.Sources?.GetValueOrDefault(key) ?? "RoadChron legacy cache (source unspecified)";
        }
        return new(dates.Values.Max(), prices, dates, sources, current.Expired, estimated.Count==0?null:estimated);
    }
    // Local simulation only. Never persist this result as observations or sign it.
    internal static Feed CompleteEuropeanFeed(Feed raw, DateTime now)
    {
        var prices = new Dictionary<string, Price>(raw.Prices, StringComparer.Ordinal);
        var dates = raw.Prices.Keys.ToDictionary(k => k, raw.DateFor, StringComparer.Ordinal);
        var sources = raw.Sources == null ? new Dictionary<string,string>() : new Dictionary<string,string>(raw.Sources);
        var flags = raw.Estimated == null ? new Dictionary<string,string[]>() : new Dictionary<string,string[]>(raw.Estimated);
        // Use fresh observed values only; never feed an estimate back into the model.
        var anchors = raw.Prices.Where(p => raw.DateFor(p.Key).Date >= now.Date.AddDays(-ObservationAgeDays(p.Key)) &&
            raw.DateFor(p.Key).Date <= now.Date.AddDays(1)).ToArray();
        decimal? Median(string field, Func<Price,decimal?> get)
        {
            var values = anchors.Where(p => !raw.IsEstimated(p.Key,field)).Select(p => get(p.Value))
                .Where(v => ValidPrice(v)).Select(v => v!.Value).Order().ToArray();
            return values.Length == 0 ? null : Math.Round((values[(values.Length-1)/2]+values[values.Length/2])/2,4);
        }
        var petrol = Median("Petrol", p => p.Petrol);
        var diesel = Median("Diesel", p => p.Diesel);
        if (petrol == null && diesel == null) return raw;
        var anchorDate = anchors.Select(p => raw.DateFor(p.Key)).Min();
        foreach (var key in DiagnosticRegions(false))
        {
            var existing = prices.GetValueOrDefault(key) ?? new Price(null,null);
            var estimated = flags.GetValueOrDefault(key)?.ToList() ?? new List<string>();
            var p = existing.Petrol; var d = existing.Diesel;
            if (p == null && petrol != null) { p = petrol; estimated.Add("Petrol"); }
            if (d == null && diesel != null) { d = diesel; estimated.Add("Diesel"); }
            if (estimated.Count == 0 || p == null && d == null) continue;
            prices[key] = existing with { Petrol=p, Diesel=d };
            flags[key] = estimated.Distinct(StringComparer.Ordinal).ToArray();
            if (!dates.ContainsKey(key)) dates[key]=anchorDate;
            if (!raw.Prices.ContainsKey(key)) sources[key]="RoadChron EU median (simulation estimate)";
        }
        return raw with { Prices=prices, RegionDates=dates, Sources=sources, Estimated=flags };
    }

    internal static string Key(string value) => new(value.ToLowerInvariant().Where(c => c is >= 'a' and <= 'z').ToArray());
    private static readonly HashSet<string> UsStates = new(("Alabama|Alaska|Arizona|Arkansas|California|Colorado|Connecticut|Delaware|District of Columbia|Florida|Georgia|Hawaii|Idaho|Illinois|Indiana|Iowa|Kansas|Kentucky|Louisiana|Maine|Maryland|Massachusetts|Michigan|Minnesota|Mississippi|Missouri|Montana|Nebraska|Nevada|New Hampshire|New Jersey|New Mexico|New York|North Carolina|North Dakota|Ohio|Oklahoma|Oregon|Pennsylvania|Rhode Island|South Carolina|South Dakota|Tennessee|Texas|Utah|Vermont|Virginia|Washington|West Virginia|Wisconsin|Wyoming").Split('|').Select(Key), StringComparer.Ordinal);
    internal static readonly Dictionary<string, string> EuropeanCountryCodes = ("at:Austria|be:Belgium|bg:Bulgaria|hr:Croatia|cy:Cyprus|cz:Czechia|dk:Denmark|ee:Estonia|fi:Finland|fr:France|de:Germany|gr:Greece|hu:Hungary|ie:Ireland|it:Italy|lv:Latvia|lt:Lithuania|lu:Luxembourg|mt:Malta|nl:Netherlands|no:Norway|pl:Poland|pt:Portugal|ro:Romania|sk:Slovakia|si:Slovenia|es:Spain|se:Sweden|ch:Switzerland|gb:United Kingdom")
        .Split('|').Select(x => x.Split(':')).ToDictionary(x => Key(x[1]), x => x[0], StringComparer.Ordinal);
    internal static string[] DiagnosticRegions(bool ats) => ats ? UsStates.Order().ToArray() : EuCodes.Values.Except(["cyprus","malta","ireland"]).Distinct().Order().ToArray();
    internal static int ObservationAgeDays(string key) => key is "switzerland" or "norway" ? 45 : 14;
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
    // Cache files are external input too. Never trust deserialization alone.
    private static bool ValidPrice(decimal? value) => value is >= 0.15m and <= 8m;
    internal static bool ValidFeed(Feed? feed, bool ats, DateTime today)
    {
        if (feed is null || feed.Date == DateTime.MinValue || feed.Date.Date > today.Date.AddDays(1)
            || feed.Prices is null || feed.Prices.Count == 0) return false;
        if(feed.Estimated != null && feed.Estimated.Any(row => !ats || !feed.Prices.TryGetValue(row.Key,out var p) || p==null ||
            row.Value==null || row.Value.Length==0 || row.Value.Distinct().Count()!=row.Value.Length ||
            row.Value.Any(f=>f switch {"Petrol"=>!p.Petrol.HasValue,"Diesel"=>!p.Diesel.HasValue,"MidGrade"=>!p.MidGrade.HasValue,"Premium"=>!p.Premium.HasValue,_=>true})))return false;
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
        Write(archive, "mod_description.txt", "RoadChron Real Fuel Prices 0.4 AUTO\nObservation dates: " + dates + "\n" + ("Sources: " + string.Join("; ",feed.Prices.Keys.Select(k=>feed.Sources?.GetValueOrDefault(k)??"RoadChron legacy cache (source unspecified)").Distinct()) + (feed.Estimated?.Count>0?". Includes RoadChron simulation estimates, not direct region observations.":".") + (ats?" US Gas Gauge (https://usgasgauge.com/gas-price-data, CC BY 4.0 https://creativecommons.org/licenses/by/4.0/) for Regular and available diesel values; EIA for remaining diesel and other grades. US Gas Gauge estimates are not EIA observations. Thank you for free data! Adapted for simulation; no endorsement.":" OpenVan.camp (https://openvan.camp/, CC BY 4.0 https://creativecommons.org/licenses/by/4.0/) where used; historical cache credits: see central dataset SOURCES.md where applicable. Thanks to the providers for freely accessible data! Prices adapted for simulation; no endorsement.")) + "\nCountry fuel definitions, sign board prices and currency rates. Exchange rates: " + AdBlueRealPrices.FxUrl + " (where available); supplemental blended reference rates: https://frankfurter.dev/ (where available, not ECB rates).\n");
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
