using ExcelDataReader;
using RoadChron;

internal static class EiaWorkbooks
{
    internal sealed record Table(DateTime Date, Dictionary<string, decimal> UsdPerGallon);

    internal static readonly string[] Products = ["EMM_EPMR", "EMD_EPD2D", "EMM_EPMM", "EMM_EPMP"];
    internal static readonly string[] Urls = new[] { "EPMR", "EPD2D", "EPMM", "EPMP" }
        .Select(product => $"https://www.eia.gov/dnav/pet/xls/PET_PRI_GND_A_{product}_PTE_DPGAL_W.xls").ToArray();

    static EiaWorkbooks() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    internal static Table Parse(byte[] workbook, int fuel, DateTime now)
    {
        using var stream = new MemoryStream(workbook, writable: false);
        using var excel = ExcelReaderFactory.CreateBinaryReader(stream);
        var series = new Dictionary<string, (DateTime Date, decimal Price)>(StringComparer.Ordinal);
        string prefix = Products[fuel] + "_PTE_";
        do
        {
            if (!excel.Name.StartsWith("Data ", StringComparison.Ordinal) || !excel.Read() ||
                !excel.Read() || excel.GetValue(0) is not string source || source != "Sourcekey") continue;

            var columns = new Dictionary<int, string>();
            for (int col = 1; col < excel.FieldCount; col++)
            {
                if (excel.GetValue(col) is not string id || !id.StartsWith(prefix, StringComparison.Ordinal) ||
                    !id.EndsWith("_DPG", StringComparison.Ordinal)) continue;
                var region = id[prefix.Length..^4];
                if (region is "NUS" or "R10" or "R1X" or "R1Y" or "R1Z" or "R20" or "R30" or "R40" or "R50" or "R5XCA" ||
                    region.Length == 3 && region[0] == 'S' && AtsStateCodes.IsValid(region[1..]))
                    columns.Add(col, region);
            }
            if (!excel.Read() || excel.GetValue(0) is not "Date") continue;
            foreach (var col in columns.Keys)
                if (excel.GetValue(col) is not string title ||
                    !title.StartsWith("Weekly ", StringComparison.Ordinal) ||
                    !title.TrimEnd().EndsWith("(Dollars per Gallon)", StringComparison.Ordinal))
                    throw new InvalidDataException("EIA series frequency or price unit changed.");
            while (excel.Read())
            {
                if (excel.GetValue(0) is not DateTime timestamp || timestamp.Date > now.Date ||
                    timestamp.Date < now.Date.AddDays(-21)) continue;
                foreach (var (col, region) in columns)
                {
                    if (excel.GetValue(col) is not double value || value is not (>= .6 and <= 30)) continue;
                    var price = (decimal)value;
                    if (series.TryGetValue(region, out var old))
                    {
                        if (old.Date == timestamp.Date && old.Price != price)
                            throw new InvalidDataException("Conflicting EIA observations for " + region);
                        if (old.Date >= timestamp.Date) continue;
                    }
                    series[region] = (timestamp.Date, price);
                }
            }
        } while (excel.NextResult());

        if (!series.TryGetValue("NUS", out var national))
            throw new InvalidDataException("No recent national EIA observation for " + Products[fuel]);
        return new(national.Date, series.Where(s => s.Value.Date == national.Date)
            .ToDictionary(s => s.Key, s => s.Value.Price, StringComparer.Ordinal));
    }
}
