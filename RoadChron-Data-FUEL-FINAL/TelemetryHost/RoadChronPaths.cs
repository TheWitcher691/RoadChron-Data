namespace RoadChron;

/// <summary>Canonical per-user storage location for RoadChron databases, logs and WebView2 data.</summary>
public static class RoadChronPaths
{
    public static string LocalDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RoadChron");
}
