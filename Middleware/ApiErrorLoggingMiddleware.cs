namespace GpsTcpProxy.Middleware;

public sealed class ApiErrorLoggingMiddleware
{
    private readonly RequestDelegate _next;

    public ApiErrorLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);

            if (context.Response.StatusCode >= 400)
                LogFailedRequest(context, null);
        }
        catch (Exception ex)
        {
            LogFailedRequest(context, ex);
            throw;
        }
    }

    private static void LogFailedRequest(HttpContext context, Exception? exception)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
            return;

        var statusCode = context.Response.StatusCode;
        if (exception != null)
            statusCode = StatusCodes.Status500InternalServerError;

        var reason = exception == null
            ? DescribeStatus(statusCode)
            : $"{exception.GetType().Name}: {exception.Message}";

        TrafficLogger.LogInfo(
            $"[API ERROR] {context.Request.Method} {context.Request.Path}{context.Request.QueryString} → {statusCode} {reason}");
    }

    private static string DescribeStatus(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "Bad Request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not Found",
        StatusCodes.Status500InternalServerError => "Internal Server Error",
        _ => $"HTTP {statusCode}"
    };
}
