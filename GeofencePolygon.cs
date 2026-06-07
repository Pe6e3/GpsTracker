namespace GpsTcpProxy;

public static class GeofencePolygon
{
    public const int MinPoints = 4;
    public const int MaxPoints = 8;

    public static bool IsValidPointCount(int count) =>
        count >= MinPoints && count <= MaxPoints;

    public static bool Contains(IReadOnlyList<GeofencePoint> polygon, double latitude, double longitude)
    {
        if (polygon.Count < 3)
            return false;

        var inside = false;
        var j = polygon.Count - 1;

        for (var i = 0; i < polygon.Count; i++)
        {
            var yi = polygon[i].Lat;
            var xi = polygon[i].Lon;
            var yj = polygon[j].Lat;
            var xj = polygon[j].Lon;

            var intersects = yi > latitude != yj > latitude &&
                longitude < ((xj - xi) * (latitude - yi) / (yj - yi)) + xi;

            if (intersects)
                inside = !inside;

            j = i;
        }

        return inside;
    }
}

public sealed class GeofencePoint
{
    public double Lat { get; init; }
    public double Lon { get; init; }
}
