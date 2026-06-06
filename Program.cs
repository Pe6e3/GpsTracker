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

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);

builder.WebHost.UseUrls($"http://0.0.0.0:{settings.ApiPort}");

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<TelemetryStore>(_ =>
{
    var store = new TelemetryStore(settings.DatabasePath);
    store.Initialize();
    return store;
});
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<TrackQueryService>();
builder.Services.AddHostedService<TcpProxyHostedService>();

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

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

TrafficLogger.LogInfo($"HTTP API: http://0.0.0.0:{settings.ApiPort}");
TrafficLogger.LogInfo($"TCP GPS: порт {settings.ListenPort} (запуск после API)");

try
{
    app.Run();
}
catch (IOException ex) when (ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
{
    TrafficLogger.LogInfo($"Порт API {settings.ApiPort} занят другим процессом.");
    TrafficLogger.LogInfo($"Проверка: ss -tlnp | grep {settings.ApiPort}");
    TrafficLogger.LogInfo("Измените ApiPort в appsettings.json (5080 занят SocketServer на этом сервере).");
    throw;
}
