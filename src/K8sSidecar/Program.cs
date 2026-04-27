using System;
using System.IO;
using System.Text.RegularExpressions;
using k8s;
using Microsoft.Extensions.Logging;

namespace K8sSidecar;

/// <summary>
/// Main entry point for the k8s-sidecar application.
/// Reads configuration, initializes K8s client, and orchestrates resource watching.
/// </summary>
public class Program
{
    public static int Main(string[] args)
    {
        var config = SidecarConfig.FromEnvironment(args);

        // Initialize logging first
        Logging.Initialize(config.LogLevel);
        var logger = Logging.CreateLogger("k8s-sidecar");

        logger.LogInformation("Starting collector");

        // Validate required config
        if (string.IsNullOrEmpty(config.Label))
        {
            logger.LogCritical("Should have added LABEL as environment variable! Exit");
            return -1;
        }

        if (string.IsNullOrEmpty(config.Folder))
        {
            logger.LogCritical("Should have added FOLDER as environment variable! Exit");
            return -1;
        }

        if (!string.IsNullOrEmpty(config.LabelValue))
            logger.LogDebug("Filter labels with value: {LabelValue}", config.LabelValue);

        logger.LogDebug("Selected resource type: {Resources}", string.Join(", ", config.Resources));
        logger.LogDebug("Selected resource name: {ResourceName}", config.ResourceName);

        if (config.UniqueFilenames)
            logger.LogInformation("Unique filenames will be enforced.");
        else
            logger.LogInformation("Unique filenames will not be enforced.");

        if (config.Enable5xx)
            logger.LogInformation("5xx response content will be enabled.");
        else
            logger.LogInformation("5xx response content will not be enabled.");

        // Start health server
        var healthServer = new HealthServer(config.HealthPort);
        healthServer.Start();

        // Initialize K8s client to verify connectivity
        try
        {
            var client = KubeClientFactory.CreateClient(config);

            // Check IGNORE_ALREADY_PROCESSED against K8s version
            if (config.IgnoreAlreadyProcessed)
            {
                try
                {
                    var version = client.Version.GetCode();
                    var vMajor = Regex.Replace(version.Major ?? "", @"\D", "");
                    var vMinor = Regex.Replace(version.Minor ?? "", @"\D", "");

                    if (vMajor.Length > 0 && vMinor.Length > 0 &&
                        (int.Parse(vMajor) > 1 || (int.Parse(vMajor) == 1 && int.Parse(vMinor) >= 19)))
                    {
                        logger.LogInformation("Ignore already processed resource version will be enabled.");
                    }
                    else
                    {
                        logger.LogInformation("Can't enable 'ignore already processed resource version', kubernetes api version ({Version}) is lower than v1.19 or unrecognized format.", version.GitVersion);
                        config.IgnoreAlreadyProcessed = false;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Exception when calling VersionApi");
                    config.IgnoreAlreadyProcessed = false;
                }
            }

            if (!config.IgnoreAlreadyProcessed)
                logger.LogDebug("Ignore already processed resource version will not be enabled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize Kubernetes client");
            return -1;
        }

        // Read namespace
        var namespaceFile = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";
        string currentNamespace;
        if (File.Exists(namespaceFile))
            currentNamespace = Environment.GetEnvironmentVariable("NAMESPACE") ?? File.ReadAllText(namespaceFile).Trim();
        else
            currentNamespace = Environment.GetEnvironmentVariable("NAMESPACE") ?? "default";

        config.Namespace = currentNamespace;

        // Prepare request payload
        var httpRequester = new HttpRequester(config);
        var requestPayload = ResourceService.PreparePayload(config.ReqPayload);
        var resourceService = new ResourceService(config, httpRequester, healthServer);

        if (config.Method == "LIST")
        {
            foreach (var res in config.Resources)
            {
                foreach (var ns in config.Namespace.Split(','))
                {
                    resourceService.ListResources(ns, res, config.ReqUrl, config.ReqMethod, requestPayload,
                        config.IgnoreAlreadyProcessed);
                }
            }
            healthServer.MarkReady();
        }
        else
        {
            // For watch/sleep: do initial list first
            logger.LogInformation("Performing initial list-based sync before starting watch.");
            var initRequestUrl = config.ReqUrl;
            if (config.ReqSkipInit)
            {
                initRequestUrl = null;
                logger.LogInformation("Skipping initial request to external endpoint.");
            }

            foreach (var res in config.Resources)
            {
                foreach (var ns in config.Namespace.Split(','))
                {
                    resourceService.ListResources(ns, res, initRequestUrl, config.ReqMethod, requestPayload, true);
                }
            }

            healthServer.MarkReady();
            logger.LogInformation("Initial sync complete, sidecar is ready.");
            resourceService.WatchForChanges(config.ReqUrl, config.ReqMethod, requestPayload);
        }

        return 0;
    }
}
