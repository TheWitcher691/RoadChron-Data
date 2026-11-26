namespace RoadChron;

public static class TachographCountryCodes
{
    private static readonly HashSet<string> ValidCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "A", "AL", "AND", "ARM", "AZ", "B", "BG", "BIH", "BY", "CH", "CY", "CZ", "D", "DK", "E", "EST",
        "F", "FIN", "FO", "GE", "GR", "H", "HR", "I", "IRL", "IS", "KZ", "L", "LT", "LV", "M", "MC", "MD",
        "MK", "MNE", "N", "NL", "P", "PL", "RO", "RKS", "RSM", "RUS", "S", "SK", "SLO", "SRB", "TR", "UA", "UK", "V", "FL"
    };
    private static readonly Dictionary<string, string> CountryNameToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Austria"] = "A", ["Albania"] = "AL", ["Andorra"] = "AND", ["Armenia"] = "ARM",
        ["Kosovo"] = "RKS", ["XK"] = "RKS", ["KS"] = "RKS", ["RKS"] = "RKS",
        ["Azerbaijan"] = "AZ", ["Belgium"] = "B", ["Bulgaria"] = "BG", ["Bosnia and Herzegovina"] = "BIH",
        ["Belarus"] = "BY", ["Switzerland"] = "CH", ["Cyprus"] = "CY", ["Czechia"] = "CZ", ["Czech Republic"] = "CZ",
        ["Germany"] = "D", ["Deutschland"] = "D", ["Denmark"] = "DK", ["Spain"] = "E", ["Estonia"] = "EST",
        ["France"] = "F", ["Finland"] = "FIN", ["Faroe Islands"] = "FO", ["Georgia"] = "GE", ["Greece"] = "GR",
        ["Hungary"] = "H", ["Croatia"] = "HR", ["Italy"] = "I", ["Ireland"] = "IRL", ["Iceland"] = "IS",
        ["Kazakhstan"] = "KZ", ["Luxembourg"] = "L", ["Liechtenstein"] = "FL", ["Lithuania"] = "LT", ["Latvia"] = "LV",
        ["Malta"] = "M", ["Monaco"] = "MC", ["Moldova"] = "MD", ["North Macedonia"] = "MK", ["Macedonia"] = "MK",
        ["Montenegro"] = "MNE", ["Norway"] = "N", ["Netherlands"] = "NL", ["The Netherlands"] = "NL", ["Portugal"] = "P",
        ["Poland"] = "PL", ["Romania"] = "RO", ["San Marino"] = "RSM", ["Russia"] = "RUS", ["Sweden"] = "S",
        ["Slovakia"] = "SK", ["Slovenia"] = "SLO", ["Serbia"] = "SRB", ["Turkey"] = "TR", ["Türkiye"] = "TR",
        ["Ukraine"] = "UA", ["United Kingdom"] = "UK", ["Great Britain"] = "UK", ["Vatican City"] = "V",
        ["AT"] = "A", ["AD"] = "AND", ["AM"] = "ARM", ["BE"] = "B", ["BA"] = "BIH", ["DE"] = "D",
        ["ES"] = "E", ["EE"] = "EST", ["FI"] = "FIN", ["HU"] = "H", ["IE"] = "IRL", ["IT"] = "I",
        ["LI"] = "FL", ["LU"] = "L", ["MT"] = "M", ["MK"] = "MK", ["ME"] = "MNE", ["NL"] = "NL",
        ["PL"] = "PL", ["PT"] = "P", ["RO"] = "RO", ["SM"] = "RSM", ["SE"] = "S", ["SI"] = "SLO",
        ["RS"] = "SRB", ["GB"] = "UK", ["VA"] = "V", ["TR"] = "TR", ["CZ"] = "CZ", ["DK"] = "DK",
        ["FR"] = "F", ["GR"] = "GR", ["HR"] = "HR", ["IS"] = "IS", ["NO"] = "N", ["SK"] = "SK",
        ["CH"] = "CH", ["CY"] = "CY", ["BG"] = "BG", ["BY"] = "BY", ["MD"] = "MD", ["RU"] = "RUS",
        ["UA"] = "UA", ["GE"] = "GE", ["AZ"] = "AZ", ["KZ"] = "KZ"
    };

    public static bool IsValid(string? code) => !string.IsNullOrWhiteSpace(code) && ValidCodes.Contains(code.Trim());

    public static string? FromCountryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = name.Trim();
        if (IsValid(normalized)) return normalized.ToUpperInvariant();
        return CountryNameToCode.TryGetValue(normalized, out var code) ? code : null;
    }
}

public static class AtsStateCodes
{
    private static readonly Dictionary<string, string> StateNameToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Alabama"] = "AL", ["Alaska"] = "AK", ["Arizona"] = "AZ", ["Arkansas"] = "AR",
        ["California"] = "CA", ["Colorado"] = "CO", ["Connecticut"] = "CT", ["Delaware"] = "DE",
        ["District of Columbia"] = "DC", ["Washington, D.C."] = "DC", ["Washington D.C."] = "DC",
        ["Florida"] = "FL", ["Georgia"] = "GA", ["Hawaii"] = "HI", ["Idaho"] = "ID",
        ["Illinois"] = "IL", ["Indiana"] = "IN", ["Iowa"] = "IA", ["Kansas"] = "KS",
        ["Kentucky"] = "KY", ["Louisiana"] = "LA", ["Maine"] = "ME", ["Maryland"] = "MD",
        ["Massachusetts"] = "MA", ["Michigan"] = "MI", ["Minnesota"] = "MN", ["Mississippi"] = "MS",
        ["Missouri"] = "MO", ["Montana"] = "MT", ["Nebraska"] = "NE", ["Nevada"] = "NV",
        ["New Hampshire"] = "NH", ["New Jersey"] = "NJ", ["New Mexico"] = "NM", ["New York"] = "NY",
        ["North Carolina"] = "NC", ["North Dakota"] = "ND", ["Ohio"] = "OH", ["Oklahoma"] = "OK",
        ["Oregon"] = "OR", ["Pennsylvania"] = "PA", ["Rhode Island"] = "RI", ["South Carolina"] = "SC",
        ["South Dakota"] = "SD", ["Tennessee"] = "TN", ["Texas"] = "TX", ["Utah"] = "UT",
        ["Vermont"] = "VT", ["Virginia"] = "VA", ["Washington"] = "WA", ["West Virginia"] = "WV",
        ["Wisconsin"] = "WI", ["Wyoming"] = "WY"
    };
    private static readonly HashSet<string> ValidCodes = StateNameToCode.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsValid(string? code) => !string.IsNullOrWhiteSpace(code) && ValidCodes.Contains(code.Trim());

    public static string? FromStateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = name.Trim();
        if (IsValid(normalized)) return normalized.ToUpperInvariant();
        return StateNameToCode.TryGetValue(normalized, out var code) ? code : null;
    }
}

