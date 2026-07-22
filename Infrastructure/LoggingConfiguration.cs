using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace DOSApi.Infrastructure;

/// <summary>
/// Centralized Serilog configuration for the application.
/// Configures console and file sinks with JSON formatting, daily rolling files,
/// and automatic creation of the logs directory.
/// </summary>
public static class LoggingConfiguration
{
    /// <summary>
    /// Configures Serilog for the application. Must be called before building the host.
    /// </summary>
    /// <param name="builder">The WebApplicationBuilder instance.</param>
    public static void ConfigureSerilog(WebApplicationBuilder builder)
    {
        // Ensure the logs directory exists
        var logsDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
        if (!Directory.Exists(logsDirectory))
        {
            Directory.CreateDirectory(logsDirectory);
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithEnvironmentUserName()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .Enrich.WithProperty("Application", "DOSApi")
            .WriteTo.Console()
            .WriteTo.File(
                path: System.IO.Path.Combine(logsDirectory, "log-.txt"),
                fileSizeLimitBytes: 100 * 1024 * 1024, // 100 MB per file
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                formatter: new JsonFormatter(),
                shared: true)
            .CreateLogger();

        // Redirect built-in logging to Serilog
        builder.Host.UseSerilog();
    }
}