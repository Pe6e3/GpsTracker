using System.Text;

using GpsTcpProxy;

using GpsTcpProxy.Services;

using Microsoft.AspNetCore.Authentication.JwtBearer;

using Microsoft.IdentityModel.Tokens;



var baseDir = AppContext.BaseDirectory;

var settingsPath = Path.Combine(baseDir, "appsettings.json");

var settings = ProxySettings.Load(settingsPath);



AppTime.Configure(settings);

DeviceRegistry.Load(Path.Combine(baseDir, "devices.json"));

PlatformSerial.Initialize(settings.DatabasePath);

LogFiles.Configure(settings.LogRetentionDays);



var builder = WebApplication.CreateBuilder(args);



builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);

builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);



builder.WebHost.UseUrls($"http://0.0.0.0:{settings.ApiPort}");



builder.Services.AddSingleton(settings);

builder.Services.AddSingleton(settings.Mqtt);

builder.Services.AddSingleton(settings.Telegram);

builder.Services.AddSingleton(settings.TrackProcessing);

builder.Services.AddSingleton<TelegramNotificationGate>();

builder.Services.AddSingleton<TelegramService>();

builder.Services.AddSingleton(sp =>

{

    var geofenceStore = new GeofenceStore(settings.DatabasePath);

    geofenceStore.Initialize();

    return geofenceStore;

});

builder.Services.AddSingleton<GeofenceService>();

builder.Services.AddSingleton<TheftDetectionService>();

builder.Services.AddSingleton<TelemetryStore>(sp =>

{

    var store = new TelemetryStore(

        settings.DatabasePath,

        sp.GetRequiredService<GeofenceService>(),

        sp.GetRequiredService<TheftDetectionService>());

    store.Initialize();

    return store;

});

builder.Services.AddSingleton<TrackPointStore>(sp =>

{

    var trackPointStore = new TrackPointStore(settings.DatabasePath);

    trackPointStore.Initialize();

    return trackPointStore;

});

builder.Services.AddSingleton<TrackProcessor>();

builder.Services.AddSingleton<ConnectionManager>();

builder.Services.AddSingleton<AuthService>();

builder.Services.AddSingleton<TrackQueryService>();

builder.Services.AddSingleton<ServiceStatusService>();

builder.Services.AddSingleton<OwnTracksDeviceHandler>();

builder.Services.AddSingleton<MqttService>();

builder.Services.AddSingleton<TelegramBotService>();

builder.Services.AddHostedService<TcpProxyHostedService>();

builder.Services.AddHostedService<LogRotationHostedService>();

builder.Services.AddHostedService<MqttHostedService>();

builder.Services.AddHostedService<TelegramBotHostedService>();

builder.Services.AddHostedService<TrackProcessingHostedService>();



builder.Services.AddControllers();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)

    .AddJwtBearer(options =>

    {

        options.TokenValidationParameters = new TokenValidationParameters

        {

            ValidateIssuer = true,

            ValidateAudience = true,

            ValidateLifetime = true,

            ValidateIssuerSigningKey = true,

            ValidIssuer = settings.JwtIssuer,

            ValidAudience = settings.JwtAudience,

            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.JwtSecret))

        };

    });

builder.Services.AddAuthorization();



var app = builder.Build();



var notificationGate = app.Services.GetRequiredService<TelegramNotificationGate>();

var telegramService = app.Services.GetRequiredService<TelegramService>();

notificationGate.AutoResumed += message => _ = telegramService.SendRawAsync(message);



app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();



TrafficLogger.LogInfo($"GpsTcpProxy v{AppVersion.Version}");

TrafficLogger.LogInfo($"HTTP API: http://0.0.0.0:{settings.ApiPort}");

TrafficLogger.LogInfo($"JT/T808 GPS: порт {settings.ListenPort} (режим: {(settings.ProxyMode ? "proxy" : "server")})");

if (settings.Mqtt.Enabled)

    TrafficLogger.LogInfo($"MQTT OwnTracks: {settings.Mqtt.Host}:{settings.Mqtt.Port}, topic {settings.Mqtt.Topic}");

else

    TrafficLogger.LogInfo("MQTT OwnTracks: disabled");



if (settings.Telegram.Enabled &&

    !string.IsNullOrWhiteSpace(settings.Telegram.BotToken) &&

    !string.IsNullOrWhiteSpace(settings.Telegram.ChatId))

{

    var features = new List<string> { "геозоны", "угон", "команды stop/start" };

    TrafficLogger.LogInfo($"Telegram: enabled ({string.Join(", ", features)})");

}

else

    TrafficLogger.LogInfo("Telegram: disabled");



if (settings.TheftDetection.Enabled)

    TrafficLogger.LogInfo($"Theft detection: enabled (phone={settings.TheftDetection.PhoneDeviceId})");

else

    TrafficLogger.LogInfo("Theft detection: disabled");



if (settings.TrackProcessing.Enabled)

    TrafficLogger.LogInfo(

        $"Track processing: enabled (every {settings.TrackProcessing.RunEverySeconds}s, delay {settings.TrackProcessing.ProcessingDelayMinutes}m)");

else

    TrafficLogger.LogInfo("Track processing: disabled");



try

{

    app.Run();

}

catch (IOException ex) when (ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))

{

    TrafficLogger.LogInfo($"Порт API {settings.ApiPort} занят другим процессом.");

    TrafficLogger.LogInfo($"Проверка: ss -tlnp | grep {settings.ApiPort}");

    Environment.Exit(1);

}

