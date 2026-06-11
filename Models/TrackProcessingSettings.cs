namespace GpsTcpProxy.Models;

public sealed class TrackProcessingSettings
{
    public bool Enabled { get; set; } = true;
    public int RunEverySeconds { get; set; } = 300;
    public int ProcessingDelayMinutes { get; set; } = 10;
    public int StationaryMinDurationMinutes { get; set; } = 10;
    public int StationaryMinPoints { get; set; } = 5;
    public double StationaryRadiusMeters { get; set; } = 75;
    public double StationaryMaxAverageSpeedKmh { get; set; } = 3;
    public double StationaryMaxPeakSpeedKmh { get; set; } = 8;
    public int StationaryHourlyIntervalMinutes { get; set; } = 60;
    public int MaxStationarySegmentHours { get; set; } = 48;
    public int InitialLookbackDays { get; set; } = 14;
    public double JumpDistanceMeters { get; set; } = 300;
    public double ReturnDistanceMeters { get; set; } = 100;
    public int JumpTimeSeconds { get; set; } = 120;
    public double DouglasPeuckerToleranceMeters { get; set; } = 20;
    public int BriefStopMinDurationSeconds { get; set; } = 8;
    public int BriefStopMaxDurationMinutes { get; set; } = 5;
    public int BriefStopMinPoints { get; set; } = 2;
    public double BriefStopRadiusMeters { get; set; } = 60;
    public double BriefStopMaxAverageSpeedKmh { get; set; } = 4;
    public double BriefStopMaxPeakSpeedKmh { get; set; } = 12;
    public double VehicleMaxSpeedKmh { get; set; } = 180;
    public double PhoneMaxSpeedKmh { get; set; } = 80;
}
