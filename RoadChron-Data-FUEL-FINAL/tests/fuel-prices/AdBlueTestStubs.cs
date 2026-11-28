namespace RoadChron;
// Standalone data/parser test project: no live game or network calls.
internal sealed class ScsSharedMemoryReader {
    internal TelemetryResponse Read(string? preferredGame = null)=>new();
}
internal sealed record TelemetryResponse(bool Connected=false,bool GameRunning=false,string? GameName=null,double? SpeedKmh=null,bool? EngineEnabled=null,double? PositionX=null,double? PositionZ=null);
