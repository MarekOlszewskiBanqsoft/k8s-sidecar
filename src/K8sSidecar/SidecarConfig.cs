using System;

namespace K8sSidecar;

/// <summary>
/// Centralized configuration parsed from environment variables and CLI arguments.
/// </summary>
public sealed class SidecarConfig
{
    // Required
    public string Label { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;

    // Optional – resource selection
    public string? LabelValue { get; set; }
    public string[] Resources { get; set; } = ["configmap"];
    public string ResourceName { get; set; } = string.Empty;
    public string FolderAnnotation { get; set; } = "k8s-sidecar-target-directory";

    // Method
    public string? Method { get; set; }  // LIST, WATCH, SLEEP

    // Namespace
    public string Namespace { get; set; } = string.Empty;

    // Request / callback
    public string? ReqUrl { get; set; }
    public string? ReqMethod { get; set; }
    public string? ReqPayload { get; set; }
    public bool ReqSkipInit { get; set; }
    public string? ReqUsername { get; set; }
    public string? ReqPassword { get; set; }
    public string? ReqUsernameFile { get; set; }
    public string? ReqPasswordFile { get; set; }
    public string ReqBasicAuthEncoding { get; set; } = "latin1";
    public bool ReqSkipTlsVerify { get; set; }

    // Retry config
    public int ReqRetryTotal { get; set; } = 5;
    public int ReqRetryConnect { get; set; } = 10;
    public int ReqRetryRead { get; set; } = 5;
    public double ReqRetryBackoffFactor { get; set; } = 1.1;
    public double ReqTimeout { get; set; } = 10;

    // Script
    public string? Script { get; set; }

    // Feature flags
    public bool UniqueFilenames { get; set; }
    public bool Enable5xx { get; set; }
    public bool IgnoreAlreadyProcessed { get; set; }
    public bool SkipTlsVerify { get; set; }
    public string? DefaultFileMode { get; set; }

    // Watch timeouts
    public int WatchServerTimeout { get; set; } = 60;
    public int WatchClientTimeout { get; set; } = 66;
    public int SleepTime { get; set; } = 60;
    public int ErrorThrottleSleep { get; set; } = 5;

    // Logging
    public string LogLevel { get; set; } = "Information";
    public string LogFormat { get; set; } = "JSON";

    // Health
    public int HealthPort { get; set; } = 8080;

    public static SidecarConfig FromEnvironment(string[] args)
    {
        var config = new SidecarConfig();

        config.Label = Environment.GetEnvironmentVariable("LABEL") ?? string.Empty;
        config.LabelValue = Environment.GetEnvironmentVariable("LABEL_VALUE");
        config.Folder = Environment.GetEnvironmentVariable("FOLDER") ?? string.Empty;

        var folderAnnotation = Environment.GetEnvironmentVariable("FOLDER_ANNOTATION");
        if (!string.IsNullOrEmpty(folderAnnotation))
            config.FolderAnnotation = folderAnnotation;

        var resource = Environment.GetEnvironmentVariable("RESOURCE") ?? "configmap";
        config.Resources = resource.Equals("both", StringComparison.OrdinalIgnoreCase)
            ? ["secret", "configmap"]
            : [resource.ToLowerInvariant()];

        config.ResourceName = Environment.GetEnvironmentVariable("RESOURCE_NAME") ?? string.Empty;
        config.Method = Environment.GetEnvironmentVariable("METHOD");

        config.ReqUrl = Environment.GetEnvironmentVariable("REQ_URL");
        config.ReqMethod = Environment.GetEnvironmentVariable("REQ_METHOD");
        config.ReqPayload = Environment.GetEnvironmentVariable("REQ_PAYLOAD");
        config.ReqSkipInit = GetBoolEnv("REQ_SKIP_INIT");
        config.ReqUsername = Environment.GetEnvironmentVariable("REQ_USERNAME");
        config.ReqPassword = Environment.GetEnvironmentVariable("REQ_PASSWORD");
        config.ReqBasicAuthEncoding = Environment.GetEnvironmentVariable("REQ_BASIC_AUTH_ENCODING") ?? "latin1";
        config.ReqSkipTlsVerify = GetBoolEnv("REQ_SKIP_TLS_VERIFY");

        config.ReqRetryTotal = GetIntEnv("REQ_RETRY_TOTAL", 5);
        config.ReqRetryConnect = GetIntEnv("REQ_RETRY_CONNECT", 10);
        config.ReqRetryRead = GetIntEnv("REQ_RETRY_READ", 5);
        config.ReqRetryBackoffFactor = GetDoubleEnv("REQ_RETRY_BACKOFF_FACTOR", 1.1);
        config.ReqTimeout = GetDoubleEnv("REQ_TIMEOUT", 10);

        config.Script = Environment.GetEnvironmentVariable("SCRIPT");
        config.UniqueFilenames = GetBoolEnv("UNIQUE_FILENAMES");
        config.Enable5xx = GetBoolEnv("ENABLE_5XX");
        config.IgnoreAlreadyProcessed = GetBoolEnv("IGNORE_ALREADY_PROCESSED");
        config.SkipTlsVerify = GetBoolEnv("SKIP_TLS_VERIFY");
        config.DefaultFileMode = Environment.GetEnvironmentVariable("DEFAULT_FILE_MODE");

        config.WatchServerTimeout = GetIntEnv("WATCH_SERVER_TIMEOUT", 60);
        config.WatchClientTimeout = GetIntEnv("WATCH_CLIENT_TIMEOUT", 66);
        config.SleepTime = GetIntEnv("SLEEP_TIME", 60);
        config.ErrorThrottleSleep = GetIntEnv("ERROR_THROTTLE_SLEEP", 5);

        config.LogLevel = Environment.GetEnvironmentVariable("LOG_LEVEL") ?? "Information";
        config.LogFormat = Environment.GetEnvironmentVariable("LOG_FORMAT") ?? "JSON";
        config.HealthPort = GetIntEnv("HEALTH_PORT", 8080);

        // Parse CLI args for --req-username-file and --req-password-file
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--req-username-file="))
                config.ReqUsernameFile = arg.Substring("--req-username-file=".Length);
            else if (arg.StartsWith("--req-password-file="))
                config.ReqPasswordFile = arg.Substring("--req-password-file=".Length);
            else if (arg == "--req-username-file" && i + 1 < args.Length)
                config.ReqUsernameFile = args[++i];
            else if (arg == "--req-password-file" && i + 1 < args.Length)
                config.ReqPasswordFile = args[++i];
        }

        return config;
    }

    private static bool GetBoolEnv(string name)
    {
        var val = Environment.GetEnvironmentVariable(name);
        return val != null && val.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetIntEnv(string name, int defaultValue)
    {
        var val = Environment.GetEnvironmentVariable(name);
        return val != null && int.TryParse(val, out var result) ? result : defaultValue;
    }

    private static double GetDoubleEnv(string name, double defaultValue)
    {
        var val = Environment.GetEnvironmentVariable(name);
        return val != null && double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
    }
}
