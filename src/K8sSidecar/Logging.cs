using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace K8sSidecar;

/// <summary>
/// Provides structured JSON logging to stdout, matching the Python sidecar's log format.
/// </summary>
public static class Logging
{
    private static ILoggerFactory? _factory;

    public static ILoggerFactory Factory => _factory ?? throw new InvalidOperationException("Logging not initialized");

    public static void Initialize(string level)
    {
        var logLevel = ParseLevel(level);

        _factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(logLevel);
            builder.AddProvider(new JsonConsoleLoggerProvider(logLevel));
        });
    }

    public static ILogger CreateLogger(string category)
    {
        return Factory.CreateLogger(category);
    }

    public static ILogger<T> CreateLogger<T>()
    {
        return Factory.CreateLogger<T>();
    }

    private static LogLevel ParseLevel(string level)
    {
        // Support Python-style level names as well as .NET style
        return level.ToUpperInvariant() switch
        {
            "DEBUG" => LogLevel.Debug,
            "TRACE" => LogLevel.Trace,
            "INFO" or "INFORMATION" => LogLevel.Information,
            "WARN" or "WARNING" => LogLevel.Warning,
            "ERROR" => LogLevel.Error,
            "CRITICAL" or "FATAL" => LogLevel.Critical,
            _ => LogLevel.Information
        };
    }
}

/// <summary>
/// A simple JSON console logger provider that outputs one JSON object per line.
/// </summary>
internal sealed class JsonConsoleLoggerProvider : ILoggerProvider
{
    private readonly LogLevel _minLevel;

    public JsonConsoleLoggerProvider(LogLevel minLevel)
    {
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) => new JsonConsoleLogger(categoryName, _minLevel);
    public void Dispose() { }
}

internal sealed class JsonConsoleLogger : ILogger
{
    private readonly string _category;
    private readonly LogLevel _minLevel;

    public JsonConsoleLogger(string category, LogLevel minLevel)
    {
        _category = category;
        _minLevel = minLevel;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var msg = formatter(state, exception);
        var level = logLevel switch
        {
            LogLevel.Trace => "DEBUG",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARNING",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRITICAL",
            _ => "INFO"
        };

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("time", DateTimeOffset.UtcNow.ToString("o"));
        writer.WriteString("level", level);
        writer.WriteString("msg", msg);
        if (exception != null)
            writer.WriteString("exception", exception.ToString());
        writer.WriteEndObject();
        writer.Flush();

        var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Console.WriteLine(json);
    }
}
