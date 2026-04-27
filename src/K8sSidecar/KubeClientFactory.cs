using System;
using System.IO;
using k8s;
using Microsoft.Extensions.Logging;

namespace K8sSidecar;

/// <summary>
/// Creates and configures Kubernetes clients.
/// </summary>
public static class KubeClientFactory
{
    private static readonly ILogger Logger = Logging.CreateLogger("kube_client");

    public static IKubernetes CreateClient(SidecarConfig config)
    {
        KubernetesClientConfiguration k8sConfig;

        var kubeConfigPath = Environment.GetEnvironmentVariable("KUBECONFIG")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kube", "config");

        if (File.Exists(kubeConfigPath))
        {
            Logger.LogInformation("Loading config from '{KubeConfig}'...", kubeConfigPath);
            k8sConfig = KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeConfigPath);
        }
        else
        {
            Logger.LogInformation("Loading incluster config...");
            k8sConfig = KubernetesClientConfiguration.InClusterConfig();
        }

        if (config.SkipTlsVerify)
        {
            k8sConfig.SkipTlsVerify = true;
        }

        Logger.LogDebug("Config for cluster api at '{Host}' loaded.", k8sConfig.Host);

        return new Kubernetes(k8sConfig);
    }
}
