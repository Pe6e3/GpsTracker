using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public static class TrackOutlierFilter
{
    public static IReadOnlyList<TelemetryPoint> RemoveOutliers(
        IReadOnlyList<TelemetryPoint> points,
        TrackProcessingSettings settings,
        DeviceProtocol protocol)
    {
        if (points.Count < 3)
            return points;

        var outliers = FindOutlierIds(points, settings, protocol);
        if (outliers.Count == 0)
            return points;

        return points.Where(p => !outliers.Contains(p.Id)).ToArray();
    }

    public static IReadOnlyList<TelemetryPoint> FilterForRuntime(
        IReadOnlyList<TelemetryPoint> points,
        TrackProcessingSettings settings,
        DeviceProtocol protocol) =>
        RemoveOutliers(points, settings, protocol);

    private static HashSet<long> FindOutlierIds(
        IReadOnlyList<TelemetryPoint> points,
        TrackProcessingSettings settings,
        DeviceProtocol protocol)
    {
        var outliers = new HashSet<long>();
        var maxSpeedKmh = GetMaxSpeedKmh(protocol, settings);

        for (var i = 1; i < points.Count - 1; i++)
        {
            var previous = points[i - 1];
            var current = points[i];
            var next = points[i + 1];

            if (!HasCoordinates(previous, current, next))
                continue;

            if (IsSingleJumpOutlier(previous, current, next, settings))
                outliers.Add(current.Id);
        }

        for (var i = 1; i < points.Count; i++)
        {
            var previous = points[i - 1];
            var current = points[i];

            if (!HasCoordinates(previous, current))
                continue;

            if (!HasImpossibleSpeed(previous, current, maxSpeedKmh))
                continue;

            if (i >= points.Count - 1)
                continue;

            var next = points[i + 1];
            if (!HasCoordinates(current, next))
                continue;

            if (IsSingleJumpOutlier(previous, current, next, settings))
                outliers.Add(current.Id);
        }

        return outliers;
    }

    private static bool IsSingleJumpOutlier(
        TelemetryPoint previous,
        TelemetryPoint current,
        TelemetryPoint next,
        TrackProcessingSettings settings)
    {
        var distanceAb = DistanceMeters(previous, current);
        var distanceBc = DistanceMeters(current, next);
        var distanceAc = DistanceMeters(previous, next);
        var timeAb = (current.GpsTimeUtc - previous.GpsTimeUtc).TotalSeconds;
        var timeBc = (next.GpsTimeUtc - current.GpsTimeUtc).TotalSeconds;

        return distanceAb > settings.JumpDistanceMeters
            && distanceBc > settings.JumpDistanceMeters
            && distanceAc < settings.ReturnDistanceMeters
            && timeAb < settings.JumpTimeSeconds
            && timeBc < settings.JumpTimeSeconds;
    }

    private static bool HasImpossibleSpeed(TelemetryPoint previous, TelemetryPoint current, double maxSpeedKmh)
    {
        var timeDeltaSeconds = (current.GpsTimeUtc - previous.GpsTimeUtc).TotalSeconds;
        if (timeDeltaSeconds <= 0)
            return false;

        var distanceKm = GeoDistance.HaversineKm(
            previous.Latitude!.Value,
            previous.Longitude!.Value,
            current.Latitude!.Value,
            current.Longitude!.Value);
        var speedKmh = distanceKm / (timeDeltaSeconds / 3600.0);

        return speedKmh > maxSpeedKmh;
    }

    private static double GetMaxSpeedKmh(DeviceProtocol protocol, TrackProcessingSettings settings) =>
        protocol == DeviceProtocol.OwnTracks
            ? settings.PhoneMaxSpeedKmh
            : settings.VehicleMaxSpeedKmh;

    private static bool HasCoordinates(params TelemetryPoint[] points) =>
        points.All(p => p.Latitude.HasValue && p.Longitude.HasValue);

    private static double DistanceMeters(TelemetryPoint from, TelemetryPoint to) =>
        GeoDistance.HaversineMeters(
            from.Latitude!.Value,
            from.Longitude!.Value,
            to.Latitude!.Value,
            to.Longitude!.Value);
}
