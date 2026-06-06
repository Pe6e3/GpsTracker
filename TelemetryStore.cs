using System.Globalization;
using GpsTcpProxy.Protocol;
using Microsoft.Data.Sqlite;

namespace GpsTcpProxy;

public sealed class TelemetryStore : IDisposable
{
    private readonly string _connectionString;
    private readonly string _databaseFullPath;
    private readonly object _lock = new();

    public TelemetryStore(string databasePath)
    {
        _databaseFullPath = Path.IsPathRooted(databasePath)
            ? databasePath
            : Path.Combine(AppContext.BaseDirectory, databasePath);

        var directory = Path.GetDirectoryName(_databaseFullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databaseFullPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public void Initialize()
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS telemetry_points (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    device_id TEXT NOT NULL,
                    device_name TEXT,
                    latitude REAL NOT NULL,
                    longitude REAL NOT NULL,
                    altitude INTEGER NOT NULL DEFAULT 0,
                    speed_kmh REAL NOT NULL DEFAULT 0,
                    direction INTEGER NOT NULL DEFAULT 0,
                    gps_time_utc TEXT NOT NULL,
                    received_at_utc TEXT NOT NULL,
                    message_serial INTEGER NOT NULL DEFAULT 0,
                    alarm_flags INTEGER NOT NULL DEFAULT 0,
                    status_flags INTEGER NOT NULL DEFAULT 0
                );

                CREATE INDEX IF NOT EXISTS ix_telemetry_device_gps_time
                    ON telemetry_points(device_id, gps_time_utc);
                """;
            command.ExecuteNonQuery();
        }
    }

    public void SaveLocation(Jt808Message message)
    {
        if (message.MessageId != Jt808Parser.MsgLocationReport)
            return;

        var location = Jt808Parser.ParseLocationReport(message.Body);
        if (location == null)
            return;

        if (!TryGetGpsTimeUtc(message.Body, out var gpsTimeUtc))
            return;

        var deviceId = DeviceRegistry.NormalizeId(message.TerminalId);
        if (string.IsNullOrEmpty(deviceId))
            return;

        var deviceName = DeviceRegistry.GetDisplayName(message.TerminalId);
        if (deviceName == deviceId || deviceName == "?")
            deviceName = null;

        var receivedAtUtc = DateTime.UtcNow;

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO telemetry_points (
                    device_id,
                    device_name,
                    latitude,
                    longitude,
                    altitude,
                    speed_kmh,
                    direction,
                    gps_time_utc,
                    received_at_utc,
                    message_serial,
                    alarm_flags,
                    status_flags
                ) VALUES (
                    $device_id,
                    $device_name,
                    $latitude,
                    $longitude,
                    $altitude,
                    $speed_kmh,
                    $direction,
                    $gps_time_utc,
                    $received_at_utc,
                    $message_serial,
                    $alarm_flags,
                    $status_flags
                );
                """;
            command.Parameters.AddWithValue("$device_id", deviceId);
            command.Parameters.AddWithValue("$device_name", (object?)deviceName ?? DBNull.Value);
            command.Parameters.AddWithValue("$latitude", location.Latitude);
            command.Parameters.AddWithValue("$longitude", location.Longitude);
            command.Parameters.AddWithValue("$altitude", location.Altitude);
            command.Parameters.AddWithValue("$speed_kmh", location.SpeedKmh);
            command.Parameters.AddWithValue("$direction", location.Direction);
            command.Parameters.AddWithValue("$gps_time_utc", FormatUtc(gpsTimeUtc));
            command.Parameters.AddWithValue("$received_at_utc", FormatUtc(receivedAtUtc));
            command.Parameters.AddWithValue("$message_serial", message.Serial);
            command.Parameters.AddWithValue("$alarm_flags", location.AlarmFlags);
            command.Parameters.AddWithValue("$status_flags", location.StatusFlags);
            command.ExecuteNonQuery();
        }
    }

    public void SaveGt06Location(string? deviceLabel, Gt06Message message)
    {
        if (message.ProtocolNumber != Gt06Parser.ProtocolLocation)
            return;

        var location = Gt06Parser.ParseLocationReport(message.Payload);
        if (location == null)
            return;

        var deviceId = DeviceRegistry.NormalizeId(deviceLabel ?? string.Empty);
        if (string.IsNullOrEmpty(deviceId) || deviceId == "?")
            return;

        var deviceName = DeviceRegistry.GetDisplayName(deviceId);
        if (deviceName == deviceId || deviceName == "?")
            deviceName = null;

        var receivedAtUtc = DateTime.UtcNow;
        var gpsTimeUtc = AppTime.AsUtc(location.DeviceTimeUtc);

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO telemetry_points (
                    device_id,
                    device_name,
                    latitude,
                    longitude,
                    altitude,
                    speed_kmh,
                    direction,
                    gps_time_utc,
                    received_at_utc,
                    message_serial,
                    alarm_flags,
                    status_flags
                ) VALUES (
                    $device_id,
                    $device_name,
                    $latitude,
                    $longitude,
                    $altitude,
                    $speed_kmh,
                    $direction,
                    $gps_time_utc,
                    $received_at_utc,
                    $message_serial,
                    $alarm_flags,
                    $status_flags
                );
                """;
            command.Parameters.AddWithValue("$device_id", deviceId);
            command.Parameters.AddWithValue("$device_name", (object?)deviceName ?? DBNull.Value);
            command.Parameters.AddWithValue("$latitude", location.Latitude);
            command.Parameters.AddWithValue("$longitude", location.Longitude);
            command.Parameters.AddWithValue("$altitude", 0);
            command.Parameters.AddWithValue("$speed_kmh", location.SpeedKmh);
            command.Parameters.AddWithValue("$direction", location.Direction);
            command.Parameters.AddWithValue("$gps_time_utc", FormatUtc(gpsTimeUtc));
            command.Parameters.AddWithValue("$received_at_utc", FormatUtc(receivedAtUtc));
            command.Parameters.AddWithValue("$message_serial", message.Serial);
            command.Parameters.AddWithValue("$alarm_flags", 0);
            command.Parameters.AddWithValue("$status_flags", 0);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<TelemetryPoint> GetTrack(
        string deviceId,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        int limit = 10000)
    {
        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            return Array.Empty<TelemetryPoint>();

        if (limit <= 0)
            limit = 10000;

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();

            var filters = new List<string> { "device_id = $device_id" };
            command.Parameters.AddWithValue("$device_id", normalizedId);
            command.Parameters.AddWithValue("$limit", limit);

            if (fromUtc.HasValue)
            {
                filters.Add("gps_time_utc >= $from_utc");
                command.Parameters.AddWithValue("$from_utc", FormatUtc(fromUtc.Value));
            }

            if (toUtc.HasValue)
            {
                filters.Add("gps_time_utc <= $to_utc");
                command.Parameters.AddWithValue("$to_utc", FormatUtc(toUtc.Value));
            }

            command.CommandText = $"""
                SELECT
                    id,
                    device_id,
                    device_name,
                    latitude,
                    longitude,
                    altitude,
                    speed_kmh,
                    direction,
                    gps_time_utc,
                    received_at_utc
                FROM telemetry_points
                WHERE {string.Join(" AND ", filters)}
                ORDER BY gps_time_utc ASC, id ASC
                LIMIT $limit;
                """;

            using var reader = command.ExecuteReader();
            var points = new List<TelemetryPoint>();

            while (reader.Read())
            {
                points.Add(new TelemetryPoint
                {
                    Id = reader.GetInt64(0),
                    DeviceId = reader.GetString(1),
                    DeviceName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Latitude = reader.GetDouble(3),
                    Longitude = reader.GetDouble(4),
                    Altitude = reader.GetInt32(5),
                    SpeedKmh = reader.GetDouble(6),
                    Direction = reader.GetInt32(7),
                    GpsTimeUtc = ParseUtc(reader.GetString(8)),
                    ReceivedAtUtc = ParseUtc(reader.GetString(9))
                });
            }

            return points;
        }
    }

    public long GetDatabaseSizeBytes()
    {
        try
        {
            if (!File.Exists(_databaseFullPath))
                return 0;

            return new FileInfo(_databaseFullPath).Length;
        }
        catch
        {
            return 0;
        }
    }

    public DeviceTelemetryStats GetDeviceStats(string deviceId)
    {
        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            return EmptyStats();

        lock (_lock)
        {
            using var connection = OpenConnection();

            var pointsCount = QueryPointsCount(connection, normalizedId);
            var lastReceivedUtc = QueryLastReceivedUtc(connection, normalizedId);
            var monthKm = QueryMonthDistanceKm(connection, normalizedId);

            return new DeviceTelemetryStats
            {
                PointsCount = pointsCount,
                LastTelemetryAgoSeconds = lastReceivedUtc.HasValue
                    ? (long)Math.Max(0, (DateTime.UtcNow - lastReceivedUtc.Value).TotalSeconds)
                    : null,
                MonthKm = monthKm
            };
        }
    }

    private static DeviceTelemetryStats EmptyStats() =>
        new()
        {
            PointsCount = 0,
            LastTelemetryAgoSeconds = null,
            MonthKm = 0
        };

    private static long QueryPointsCount(SqliteConnection connection, string deviceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM telemetry_points
            WHERE device_id = $device_id;
            """;
        command.Parameters.AddWithValue("$device_id", deviceId);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static DateTime? QueryLastReceivedUtc(SqliteConnection connection, string deviceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(received_at_utc)
            FROM telemetry_points
            WHERE device_id = $device_id;
            """;
        command.Parameters.AddWithValue("$device_id", deviceId);
        var value = command.ExecuteScalar();
        if (value == null || value is DBNull)
            return null;

        return ParseUtc(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    private static double QueryMonthDistanceKm(SqliteConnection connection, string deviceId)
    {
        var nowLocal = AppTime.NowLocal();
        var monthStartLocal = new DateTime(nowLocal.Year, nowLocal.Month, 1);
        var fromUtc = AppTime.LocalToUtc(monthStartLocal);
        var toUtc = AppTime.LocalToUtc(nowLocal);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT latitude, longitude
            FROM telemetry_points
            WHERE device_id = $device_id
              AND gps_time_utc >= $from_utc
              AND gps_time_utc <= $to_utc
            ORDER BY gps_time_utc ASC, id ASC;
            """;
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$from_utc", FormatUtc(fromUtc));
        command.Parameters.AddWithValue("$to_utc", FormatUtc(toUtc));

        using var reader = command.ExecuteReader();

        double? prevLat = null;
        double? prevLon = null;
        var totalKm = 0.0;

        while (reader.Read())
        {
            var lat = reader.GetDouble(0);
            var lon = reader.GetDouble(1);

            if (prevLat.HasValue && prevLon.HasValue)
                totalKm += GeoDistance.HaversineKm(prevLat.Value, prevLon.Value, lat, lon);

            prevLat = lat;
            prevLon = lon;
        }

        return totalKm;
    }

    public void Dispose()
    {
    }

    private static bool TryGetGpsTimeUtc(ReadOnlySpan<byte> body, out DateTime gpsTimeUtc)
    {
        gpsTimeUtc = default;
        if (body.Length < 28 || !Jt808Bcd.TryParseDateTime(body.Slice(22, 6), out var deviceTime))
            return false;

        gpsTimeUtc = AppTime.DeviceTimeToUtc(deviceTime);
        return true;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static string FormatUtc(DateTime utc) =>
        AppTime.AsUtc(utc).ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
