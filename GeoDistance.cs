namespace GpsTcpProxy;

public static class GeoDistance
{
    private const double EarthRadiusKm = 6371.0;

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusKm * c;
    }

    public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2) =>
        HaversineKm(lat1, lon1, lat2, lon2) * 1000.0;

    public static double PerpendicularDistanceMeters(
        double pointLat,
        double pointLon,
        double lineStartLat,
        double lineStartLon,
        double lineEndLat,
        double lineEndLon)
    {
        var lineLengthM = HaversineMeters(lineStartLat, lineStartLon, lineEndLat, lineEndLon);
        if (lineLengthM < 0.01)
            return HaversineMeters(pointLat, pointLon, lineStartLat, lineStartLon);

        var latScale = 111_320.0;
        var lonScale = 111_320.0 * Math.Cos(DegreesToRadians((lineStartLat + lineEndLat) / 2.0));

        var x1 = lineStartLon * lonScale;
        var y1 = lineStartLat * latScale;
        var x2 = lineEndLon * lonScale;
        var y2 = lineEndLat * latScale;
        var x0 = pointLon * lonScale;
        var y0 = pointLat * latScale;

        var dx = x2 - x1;
        var dy = y2 - y1;
        var t = ((x0 - x1) * dx + (y0 - y1) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0.0, 1.0);

        var projX = x1 + t * dx;
        var projY = y1 + t * dy;
        var distX = x0 - projX;
        var distY = y0 - projY;

        return Math.Sqrt(distX * distX + distY * distY);
    }

    private static double DegreesToRadians(double degrees) =>
        degrees * Math.PI / 180.0;
}
