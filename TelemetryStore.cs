using System.Globalization;
using GpsTcpProxy.Models;
using GpsTcpProxy.Protocol;
using GpsTcpProxy.Services;
using Microsoft.Data.Sqlite;

namespace GpsTcpProxy;

public sealed class TelemetryStore : IDisposable
{
    public const double OwnTracksNoCoordinatesAccuracyM = 50;

    private readonly string _connectionString;
    private readonly string _databaseFullPath;
    private readonly GeofenceService? _geofenceService;
    private readonly TheftDetectionService? _theftDetectionService;
    private readonly object _lock = new();

    public TelemetryStore(
        string databasePath,
        GeofenceService? geofenceService = null,
        TheftDetectionService? theftDetectionService = null)
    {
        _geofenceService = geofenceService;
        _theftDetectionService = theftDetectionService;
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
                    latitude REAL,
                    longitude REAL,
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

            EnsureColumn(connection, "accuracy", "REAL");
            EnsureColumn(connection, "battery", "INTEGER");
            EnsureColumn(connection, "source_topic", "TEXT");
            EnsureColumn(connection, "geofence_names", "TEXT");
            EnsureNullableCoordinates(connection);
        }
    }

    private string ResolveGeofenceNamesJson(string deviceId, double latitude, double longitude)
    {
        if (_geofenceService == null)
            return GeofenceStore.SerializeNames(Array.Empty<string>());

        return _geofenceService.ProcessNewPoint(deviceId, latitude, longitude).GeofenceNamesJson;
    }

    public void SaveOwnTracksLocation(string deviceId, OwnTracksLocationMessage location)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return;

        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            normalizedId = deviceId.Trim();

        var deviceName = DeviceRegistry.GetDisplayName(normalizedId);
        if (deviceName == normalizedId || deviceName == "?")
            deviceName = null;

        var receivedAtUtc = DateTime.UtcNow;
        var gpsTimeUtc = AppTime.AsUtc(location.TimestampUtc);
        if ((receivedAtUtc - gpsTimeUtc).TotalMinutes > 10)
            gpsTimeUtc = receivedAtUtc;
        var discardCoordinates = location.Accuracy.HasValue && location.Accuracy.Value > OwnTracksNoCoordinatesAccuracyM;
        double? latitude = discardCoordinates ? null : location.Latitude;
        double? longitude = discardCoordinates ? null : location.Longitude;
        var geofenceNamesJson = discardCoordinates
            ? GeofenceStore.SerializeNames(Array.Empty<string>())
            : ResolveGeofenceNamesJson(normalizedId, location.Latitude, location.Longitude);

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
                    status_flags,
                    accuracy,
                    battery,
                    source_topic,
                    geofence_names
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
                    $status_flags,
                    $accuracy,
                    $battery,
                    $source_topic,
                    $geofence_names
                );
                """;
            command.Parameters.AddWithValue("$device_id", normalizedId);
            command.Parameters.AddWithValue("$device_name", (object?)deviceName ?? DBNull.Value);
            command.Parameters.AddWithValue("$latitude", latitude.HasValue ? latitude.Value : DBNull.Value);
            command.Parameters.AddWithValue("$longitude", longitude.HasValue ? longitude.Value : DBNull.Value);
            command.Parameters.AddWithValue("$altitude", location.Altitude ?? 0);
            command.Parameters.AddWithValue("$speed_kmh", location.VelocityKmh ?? 0);
            command.Parameters.AddWithValue("$direction", location.Course ?? 0);
            command.Parameters.AddWithValue("$gps_time_utc", FormatUtc(gpsTimeUtc));
            command.Parameters.AddWithValue("$received_at_utc", FormatUtc(receivedAtUtc));
            command.Parameters.AddWithValue("$message_serial", 0);
            command.Parameters.AddWithValue("$alarm_flags", 0);
            command.Parameters.AddWithValue("$status_flags", 0);
            command.Parameters.AddWithValue("$accuracy", (object?)location.Accuracy ?? DBNull.Value);
            command.Parameters.AddWithValue("$battery", (object?)location.Battery ?? DBNull.Value);
            command.Parameters.AddWithValue("$source_topic", location.Topic);
            command.Parameters.AddWithValue("$geofence_names", geofenceNamesJson);
            command.ExecuteNonQuery();
        }
    }

    private void AnalyzeTheft(
        string deviceId,
        double latitude,
        double longitude,
        double speedKmh,
        DateTime gpsTimeUtc,
        string geofenceNamesJson)
    {
        _theftDetectionService?.Analyze(deviceId, latitude, longitude, speedKmh, gpsTimeUtc, geofenceNamesJson);
    }

    private static void EnsureNullableCoordinates(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'table' AND name = 'telemetry_points';
            """;
        var tableSql = command.ExecuteScalar()?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(tableSql))
            return;

        if (!tableSql.Contains("latitude REAL NOT NULL", StringComparison.Ordinal))
            return;

        using var transaction = connection.BeginTransaction();

        using (var rebuild = connection.CreateCommand())
        {
            rebuild.Transaction = transaction;
            rebuild.CommandText = """
                CREATE TABLE telemetry_points_new (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    device_id TEXT NOT NULL,
                    device_name TEXT,
                    latitude REAL,
                    longitude REAL,
                    altitude INTEGER NOT NULL DEFAULT 0,
                    speed_kmh REAL NOT NULL DEFAULT 0,
                    direction INTEGER NOT NULL DEFAULT 0,
                    gps_time_utc TEXT NOT NULL,
                    received_at_utc TEXT NOT NULL,
                    message_serial INTEGER NOT NULL DEFAULT 0,
                    alarm_flags INTEGER NOT NULL DEFAULT 0,
                    status_flags INTEGER NOT NULL DEFAULT 0,
                    accuracy REAL,
                    battery INTEGER,
                    source_topic TEXT,
                    geofence_names TEXT
                );

                INSERT INTO telemetry_points_new (
                    id,
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
                    status_flags,
                    accuracy,
                    battery,
                    source_topic,
                    geofence_names
                )
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
                    received_at_utc,
                    message_serial,
                    alarm_flags,
                    status_flags,
                    accuracy,
                    battery,
                    source_topic,
                    geofence_names
                FROM telemetry_points;

                DROP TABLE telemetry_points;
                ALTER TABLE telemetry_points_new RENAME TO telemetry_points;

                CREATE INDEX IF NOT EXISTS ix_telemetry_device_gps_time
                    ON telemetry_points(device_id, gps_time_utc);
                """;
            rebuild.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void EnsureColumn(SqliteConnection connection, string columnName, string columnType)
    {
        using var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(telemetry_points);";

        using var reader = info.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE telemetry_points ADD COLUMN {columnName} {columnType};";
        alter.ExecuteNonQuery();
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
        var geofenceNamesJson = ResolveGeofenceNamesJson(deviceId, location.Latitude, location.Longitude);

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
                    status_flags,
                    geofence_names
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
                    $status_flags,
                    $geofence_names
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
            command.Parameters.AddWithValue("$geofence_names", geofenceNamesJson);
            command.ExecuteNonQuery();
        }

        AnalyzeTheft(deviceId, location.Latitude, location.Longitude, location.SpeedKmh, gpsTimeUtc, geofenceNamesJson);
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
        var geofenceNamesJson = ResolveGeofenceNamesJson(deviceId, location.Latitude, location.Longitude);

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
                    status_flags,
                    geofence_names
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
                    $status_flags,
                    $geofence_names
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
            command.Parameters.AddWithValue("$geofence_names", geofenceNamesJson);
            command.ExecuteNonQuery();
        }

        AnalyzeTheft(deviceId, location.Latitude, location.Longitude, location.SpeedKmh, gpsTimeUtc, geofenceNamesJson);
    }

    public TelemetryPoint? GetLatestPosition(string deviceId, double? maxAccuracyMeters = null)
    {
        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            return null;

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();

            var filters = new List<string>
            {
                "device_id = $device_id",
                "latitude IS NOT NULL",
                "longitude IS NOT NULL"
            };
            command.Parameters.AddWithValue("$device_id", normalizedId);

            if (maxAccuracyMeters.HasValue)
            {
                filters.Add("(accuracy IS NULL OR accuracy <= $max_accuracy)");
                command.Parameters.AddWithValue("$max_accuracy", maxAccuracyMeters.Value);
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
                    received_at_utc,
                    accuracy,
                    geofence_names,
                    battery
                FROM telemetry_points
                WHERE {string.Join(" AND ", filters)}
                ORDER BY gps_time_utc DESC, id DESC
                LIMIT 1;
                """;

            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            return new TelemetryPoint
            {
                Id = reader.GetInt64(0),
                DeviceId = reader.GetString(1),
                DeviceName = reader.IsDBNull(2) ? null : reader.GetString(2),
                Latitude = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                Longitude = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                Altitude = reader.GetInt32(5),
                SpeedKmh = reader.GetDouble(6),
                Direction = reader.GetInt32(7),
                GpsTimeUtc = ParseUtc(reader.GetString(8)),
                ReceivedAtUtc = ParseUtc(reader.GetString(9)),
                Accuracy = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                Geofences = GeofenceStore.DeserializeNames(reader.IsDBNull(11) ? null : reader.GetString(11)),
                Battery = reader.IsDBNull(12) ? null : reader.GetInt32(12)
            };
        }
    }

    public IReadOnlyList<string> GetDeviceIdsWithTelemetry()
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT device_id
                FROM telemetry_points
                ORDER BY device_id;
                """;

            using var reader = command.ExecuteReader();
            var deviceIds = new List<string>();

            while (reader.Read())
                deviceIds.Add(reader.GetString(0));

            return deviceIds;
        }
    }

    public IReadOnlyList<TelemetryPoint> GetRawTelemetryAfter(
        string deviceId,
        long? afterRawId,
        DateTime? toUtc = null,
        int limit = 10000,
        DateTime? fromUtcMin = null)
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

            var filters = new List<string>
            {
                "device_id = $device_id",
                "latitude IS NOT NULL",
                "longitude IS NOT NULL"
            };
            command.Parameters.AddWithValue("$device_id", normalizedId);
            command.Parameters.AddWithValue("$limit", limit);

            if (afterRawId.HasValue)
            {
                filters.Add("id > $after_raw_id");
                command.Parameters.AddWithValue("$after_raw_id", afterRawId.Value);
            }

            if (toUtc.HasValue)
            {
                filters.Add("gps_time_utc <= $to_utc");
                command.Parameters.AddWithValue("$to_utc", FormatUtc(toUtc.Value));
            }

            if (fromUtcMin.HasValue)
            {
                filters.Add("gps_time_utc >= $from_utc_min");
                command.Parameters.AddWithValue("$from_utc_min", FormatUtc(fromUtcMin.Value));
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
                    received_at_utc,
                    accuracy,
                    geofence_names
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
                    Latitude = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                    Longitude = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    Altitude = reader.GetInt32(5),
                    SpeedKmh = reader.GetDouble(6),
                    Direction = reader.GetInt32(7),
                    GpsTimeUtc = ParseUtc(reader.GetString(8)),
                    ReceivedAtUtc = ParseUtc(reader.GetString(9)),
                    Accuracy = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                    Geofences = GeofenceStore.DeserializeNames(reader.IsDBNull(11) ? null : reader.GetString(11))
                });
            }

            return points;
        }
    }

    public IReadOnlyList<TelemetryPoint> GetRawTelemetryInRangeBeforeId(
        string deviceId,
        DateTime fromUtc,
        DateTime toUtc,
        long beforeRawId,
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
            command.CommandText = """
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
                    received_at_utc,
                    accuracy,
                    geofence_names
                FROM telemetry_points
                WHERE device_id = $device_id
                  AND latitude IS NOT NULL
                  AND longitude IS NOT NULL
                  AND gps_time_utc >= $from_utc
                  AND gps_time_utc <= $to_utc
                  AND id < $before_raw_id
                ORDER BY gps_time_utc ASC, id ASC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$device_id", normalizedId);
            command.Parameters.AddWithValue("$from_utc", FormatUtc(fromUtc));
            command.Parameters.AddWithValue("$to_utc", FormatUtc(toUtc));
            command.Parameters.AddWithValue("$before_raw_id", beforeRawId);
            command.Parameters.AddWithValue("$limit", limit);

            using var reader = command.ExecuteReader();
            var points = new List<TelemetryPoint>();

            while (reader.Read())
            {
                points.Add(new TelemetryPoint
                {
                    Id = reader.GetInt64(0),
                    DeviceId = reader.GetString(1),
                    DeviceName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Latitude = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                    Longitude = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    Altitude = reader.GetInt32(5),
                    SpeedKmh = reader.GetDouble(6),
                    Direction = reader.GetInt32(7),
                    GpsTimeUtc = ParseUtc(reader.GetString(8)),
                    ReceivedAtUtc = ParseUtc(reader.GetString(9)),
                    Accuracy = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                    Geofences = GeofenceStore.DeserializeNames(reader.IsDBNull(11) ? null : reader.GetString(11))
                });
            }

            return points;
        }
    }

    public long? GetFirstRawIdAtOrAfter(string deviceId, DateTime fromUtc)
    {
        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            return null;

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id
                FROM telemetry_points
                WHERE device_id = $device_id
                  AND latitude IS NOT NULL
                  AND longitude IS NOT NULL
                  AND gps_time_utc >= $from_utc
                ORDER BY gps_time_utc ASC, id ASC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$device_id", normalizedId);
            command.Parameters.AddWithValue("$from_utc", FormatUtc(fromUtc));

            var value = command.ExecuteScalar();
            if (value == null || value is DBNull)
                return null;

            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
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
                    received_at_utc,
                    accuracy,
                    geofence_names
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
                    Latitude = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                    Longitude = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    Altitude = reader.GetInt32(5),
                    SpeedKmh = reader.GetDouble(6),
                    Direction = reader.GetInt32(7),
                    GpsTimeUtc = ParseUtc(reader.GetString(8)),
                    ReceivedAtUtc = ParseUtc(reader.GetString(9)),
                    Accuracy = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                    Geofences = GeofenceStore.DeserializeNames(reader.IsDBNull(11) ? null : reader.GetString(11))
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

        var nowLocal = AppTime.NowLocal();
        var todayStartLocal = nowLocal.Date;
        var monthStartLocal = new DateTime(nowLocal.Year, nowLocal.Month, 1);
        var todayFromUtc = AppTime.LocalToUtc(todayStartLocal);
        var todayToUtc = AppTime.LocalToUtc(nowLocal);
        var monthFromUtc = AppTime.LocalToUtc(monthStartLocal);
        var monthToUtc = todayToUtc;

        lock (_lock)
        {
            using var connection = OpenConnection();

            var pointsCount = QueryPointsCount(connection, normalizedId);
            var lastReceivedUtc = QueryLastReceivedUtc(connection, normalizedId);
            var lastGpsUtc = QueryLastGpsUtc(connection, normalizedId);
            var todayStats = QueryPeriodStats(connection, normalizedId, todayFromUtc, todayToUtc);
            var monthStats = QueryPeriodStats(connection, normalizedId, monthFromUtc, monthToUtc);

            return new DeviceTelemetryStats
            {
                PointsCount = pointsCount,
                LastTelemetryAgoSeconds = lastReceivedUtc.HasValue
                    ? (long)Math.Max(0, (DateTime.UtcNow - lastReceivedUtc.Value).TotalSeconds)
                    : null,
                LastGpsAgoSeconds = lastGpsUtc.HasValue
                    ? (long)Math.Max(0, (DateTime.UtcNow - lastGpsUtc.Value).TotalSeconds)
                    : null,
                MonthKm = monthStats.DistanceKm,
                TodayPointsRaw = todayStats.PointsCount,
                TodayKmRaw = todayStats.DistanceKm,
                MonthPointsRaw = monthStats.PointsCount,
                MonthKmRaw = monthStats.DistanceKm
            };
        }
    }

    public PeriodTrackStats GetPeriodStats(string deviceId, DateTime fromUtc, DateTime toUtc)
    {
        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            return EmptyPeriodStats();

        lock (_lock)
        {
            using var connection = OpenConnection();
            return QueryPeriodStats(connection, normalizedId, fromUtc, toUtc);
        }
    }

    private static DeviceTelemetryStats EmptyStats() =>
        new()
        {
            PointsCount = 0,
            LastTelemetryAgoSeconds = null,
            MonthKm = 0,
            TodayPointsRaw = 0,
            TodayKmRaw = 0,
            MonthPointsRaw = 0,
            MonthKmRaw = 0
        };

    private static PeriodTrackStats EmptyPeriodStats() =>
        new()
        {
            PointsCount = 0,
            HeartbeatPointsCount = 0,
            DistanceKm = 0
        };

    private static PeriodTrackStats QueryPeriodStats(
        SqliteConnection connection,
        string deviceId,
        DateTime fromUtc,
        DateTime toUtc)
    {
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = """
            SELECT COUNT(*)
            FROM telemetry_points
            WHERE device_id = $device_id
              AND gps_time_utc >= $from_utc
              AND gps_time_utc <= $to_utc
              AND latitude IS NOT NULL
              AND longitude IS NOT NULL;
            """;
        countCommand.Parameters.AddWithValue("$device_id", deviceId);
        countCommand.Parameters.AddWithValue("$from_utc", FormatUtc(fromUtc));
        countCommand.Parameters.AddWithValue("$to_utc", FormatUtc(toUtc));
        var pointsCount = (long)(countCommand.ExecuteScalar() ?? 0L);

        using var distanceCommand = connection.CreateCommand();
        distanceCommand.CommandText = """
            SELECT latitude, longitude
            FROM telemetry_points
            WHERE device_id = $device_id
              AND gps_time_utc >= $from_utc
              AND gps_time_utc <= $to_utc
              AND latitude IS NOT NULL
              AND longitude IS NOT NULL
            ORDER BY gps_time_utc ASC, id ASC;
            """;
        distanceCommand.Parameters.AddWithValue("$device_id", deviceId);
        distanceCommand.Parameters.AddWithValue("$from_utc", FormatUtc(fromUtc));
        distanceCommand.Parameters.AddWithValue("$to_utc", FormatUtc(toUtc));

        using var reader = distanceCommand.ExecuteReader();

        double? previousLat = null;
        double? previousLon = null;
        var totalKm = 0.0;

        while (reader.Read())
        {
            var lat = reader.GetDouble(0);
            var lon = reader.GetDouble(1);

            if (previousLat.HasValue && previousLon.HasValue)
                totalKm += GeoDistance.HaversineKm(previousLat.Value, previousLon.Value, lat, lon);

            previousLat = lat;
            previousLon = lon;
        }

        return new PeriodTrackStats
        {
            PointsCount = pointsCount,
            DistanceKm = totalKm
        };
    }

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

    private static DateTime? QueryLastGpsUtc(SqliteConnection connection, string deviceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(gps_time_utc)
            FROM telemetry_points
            WHERE device_id = $device_id
              AND latitude IS NOT NULL
              AND longitude IS NOT NULL;
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
              AND latitude IS NOT NULL
              AND longitude IS NOT NULL
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
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
                continue;

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
