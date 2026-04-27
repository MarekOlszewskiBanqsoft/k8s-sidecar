using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace K8sSidecar;

/// <summary>
/// Core resource watching and listing service. Handles ConfigMaps and Secrets,
/// extracts data keys as files, supports pagination, .url downloads, and change tracking.
/// </summary>
public sealed class ResourceService
{
    private readonly ILogger _logger = Logging.CreateLogger("resource_service");
    private readonly SidecarConfig _config;
    private readonly HttpRequester _httpRequester;
    private readonly HealthServer _healthServer;

    // Track resource versions and objects for change detection
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _resourcesVersionMap = new()
    {
        ["secret"] = new(),
        ["configmap"] = new()
    };

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (object item, string destFolder)>> _resourcesObjectMap = new()
    {
        ["secret"] = new(),
        ["configmap"] = new()
    };

    public ResourceService(SidecarConfig config, HttpRequester httpRequester, HealthServer healthServer)
    {
        _config = config;
        _httpRequester = httpRequester;
        _healthServer = healthServer;
    }

    /// <summary>
    /// Prepare a payload string as a dictionary for POST requests.
    /// </summary>
    public static object? PreparePayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(payload);
        }
        catch
        {
            return payload; // Return as quoted string if not valid JSON
        }
    }

    /// <summary>
    /// List resources matching label selector and process them.
    /// </summary>
    public void ListResources(string ns, string resource, string? requestUrl, string? requestMethod,
        object? requestPayload, bool ignoreAlreadyProcessed)
    {
        var client = KubeClientFactory.CreateClient(_config);

        _logger.LogInformation("Performing list-based sync on {Resource} resources: namespace={Namespace}", resource, ns);

        var items = new List<object>();
        var resourceNames = GetFilteredResourceNames(ns, resource);

        if (ns != "ALL" && resourceNames.Count > 0)
        {
            // Direct reads for specific resource names
            foreach (var rn in resourceNames)
            {
                try
                {
                    if (resource == "secret")
                    {
                        var item = client.CoreV1.ReadNamespacedSecret(rn, ns);
                        items.Add(item);
                    }
                    else
                    {
                        var item = client.CoreV1.ReadNamespacedConfigMap(rn, ns);
                        items.Add(item);
                    }
                }
                catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Resource not found, skip
                }
            }
        }
        else
        {
            // List with label selector, with pagination
            var labelSelector = !string.IsNullOrEmpty(_config.LabelValue)
                ? $"{_config.Label}={_config.LabelValue}"
                : _config.Label;

            string? continueToken = null;
            do
            {
                if (resource == "secret")
                {
                    V1SecretList list;
                    if (ns == "ALL")
                        list = client.CoreV1.ListSecretForAllNamespaces(labelSelector: labelSelector, limit: 5, continueParameter: continueToken);
                    else
                        list = client.CoreV1.ListNamespacedSecret(ns, labelSelector: labelSelector, limit: 5, continueParameter: continueToken);

                    items.AddRange(list.Items);
                    continueToken = list.Metadata?.ContinueProperty;
                }
                else
                {
                    V1ConfigMapList list;
                    if (ns == "ALL")
                        list = client.CoreV1.ListConfigMapForAllNamespaces(labelSelector: labelSelector, limit: 5, continueParameter: continueToken);
                    else
                        list = client.CoreV1.ListNamespacedConfigMap(ns, labelSelector: labelSelector, limit: 5, continueParameter: continueToken);

                    items.AddRange(list.Items);
                    continueToken = list.Metadata?.ContinueProperty;
                }
            } while (!string.IsNullOrEmpty(continueToken));
        }

        bool filesChanged = false;
        var existKeys = new HashSet<string>();

        foreach (var item in items)
        {
            var metadata = GetMetadata(item);
            var key = metadata.NamespaceProperty + metadata.Name;
            existKeys.Add(key);

            // Ignore already processed
            if (ignoreAlreadyProcessed)
            {
                var versionMap = _resourcesVersionMap[resource];
                if (versionMap.TryGetValue(key, out var cachedVersion) && cachedVersion == metadata.ResourceVersion)
                {
                    _logger.LogDebug("Ignoring {Resource} {Namespace}/{Name}", resource, metadata.NamespaceProperty, metadata.Name);
                    continue;
                }
                versionMap[key] = metadata.ResourceVersion;
            }

            _logger.LogDebug("Working on {Resource}: {Namespace}/{Name}", resource, metadata.NamespaceProperty, metadata.Name);

            var destFolder = GetDestinationFolder(metadata, _config.Folder, _config.FolderAnnotation);

            if (resource == "configmap")
                filesChanged |= ProcessConfigMap(destFolder, (V1ConfigMap)item, resource, false);
            else
                filesChanged |= ProcessSecret(destFolder, (V1Secret)item, resource, false);
        }

        // Remove resources that no longer exist
        var objMap = _resourcesObjectMap[resource];
        var keysToRemove = objMap.Keys.Except(existKeys).ToList();
        foreach (var key in keysToRemove)
        {
            if (objMap.TryRemove(key, out var cached))
            {
                var metadata = GetMetadata(cached.item);
                _logger.LogDebug("Removing {Resource}: {Namespace}/{Name}", resource, metadata.NamespaceProperty, metadata.Name);

                if (resource == "configmap")
                    filesChanged |= ProcessConfigMap(null, (V1ConfigMap)cached.item, resource, true);
                else
                    filesChanged |= ProcessSecret(null, (V1Secret)cached.item, resource, true);
            }
        }

        if (!string.IsNullOrEmpty(_config.Script) && filesChanged)
            ScriptExecutor.Execute(_config.Script);

        if (!string.IsNullOrEmpty(requestUrl) && filesChanged)
            _httpRequester.SendRequest(requestUrl, requestMethod, _config.Enable5xx, requestPayload);
    }

    /// <summary>
    /// Watch for resource changes continuously.
    /// </summary>
    public void WatchForChanges(string? requestUrl, string? requestMethod, object? requestPayload)
    {
        var shutdownEvent = new ManualResetEventSlim(false);
        var threads = new List<Thread>();
        var threadMetas = new List<(Thread thread, string ns, string resource)>();

        foreach (var resource in _config.Resources)
        {
            foreach (var ns in _config.Namespace.Split(','))
            {
                var r = resource;
                var n = ns;
                var thread = new Thread(() => WatchResourceLoop(shutdownEvent, r, n, requestUrl, requestMethod, requestPayload))
                {
                    IsBackground = true,
                    Name = $"Watcher-{n}-{r}"
                };
                thread.Start();
                threads.Add(thread);
                threadMetas.Add((thread, n, r));
            }
        }

        _healthServer.RegisterWatcherThreads(threads);

        while (true)
        {
            _healthServer.UpdateK8sContact();
            bool died = false;

            foreach (var (thread, ns, resource) in threadMetas)
            {
                if (!thread.IsAlive)
                {
                    _logger.LogError("Process for {Namespace}/{Resource} died", ns, resource);
                    died = true;
                }
            }

            if (died)
            {
                _logger.LogCritical("At least one process died. Stopping and exiting");
                shutdownEvent.Set();
                foreach (var (thread, _, _) in threadMetas)
                {
                    if (thread.IsAlive)
                        thread.Join(TimeSpan.FromSeconds(5));
                }
                Environment.Exit(1);
            }

            Thread.Sleep(5000);
        }
    }

    private void WatchResourceLoop(ManualResetEventSlim shutdownEvent, string resource, string ns,
        string? requestUrl, string? requestMethod, object? requestPayload)
    {
        while (!shutdownEvent.IsSet)
        {
            try
            {
                if (_config.Method == "SLEEP" || (ns != "ALL" && !string.IsNullOrEmpty(_config.ResourceName)))
                {
                    ListResources(ns, resource, requestUrl, requestMethod, requestPayload, _config.IgnoreAlreadyProcessed);
                    Thread.Sleep(_config.SleepTime * 1000);
                }
                else
                {
                    WatchResourceIterator(resource, ns, requestUrl, requestMethod, requestPayload);
                }
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
            {
                throw; // Re-throw 500 errors
            }
            catch (Exception ex)
            {
                _logger.LogError("Exception when watching kubernetes: {Error}", ex.Message);
                Thread.Sleep(_config.ErrorThrottleSleep * 1000);
            }
        }

        _logger.LogInformation("Shutdown event received, stopping watcher for {Namespace}/{Resource}.", ns, resource);
    }

    private void WatchResourceIterator(string resource, string ns, string? requestUrl, string? requestMethod, object? requestPayload)
    {
        var client = KubeClientFactory.CreateClient(_config);
        var labelSelector = !string.IsNullOrEmpty(_config.LabelValue)
            ? $"{_config.Label}={_config.LabelValue}"
            : _config.Label;

        _logger.LogDebug("Performing watch-based sync on {Resource} resources: namespace={Namespace}, labelSelector={LabelSelector}",
            resource, ns, labelSelector);

        bool firstEvent = true;
        using var watchDone = new ManualResetEventSlim(false);
        Exception? watchError = null;

        if (resource == "configmap")
        {
            var listResp = ns == "ALL"
                ? client.CoreV1.ListConfigMapForAllNamespacesWithHttpMessagesAsync(
                    labelSelector: labelSelector,
                    timeoutSeconds: _config.WatchServerTimeout,
                    watch: true).Result
                : client.CoreV1.ListNamespacedConfigMapWithHttpMessagesAsync(
                    ns,
                    labelSelector: labelSelector,
                    timeoutSeconds: _config.WatchServerTimeout,
                    watch: true).Result;

            using var watcher = listResp.Watch<V1ConfigMap, V1ConfigMapList>(
                onEvent: (type, item) =>
                {
                    if (firstEvent)
                    {
                        _healthServer.MarkReady();
                        firstEvent = false;
                    }
                    ProcessWatchEvent(type, item, resource, requestUrl, requestMethod, requestPayload);
                },
                onError: ex =>
                {
                    watchError = ex;
                    watchDone.Set();
                },
                onClosed: () => watchDone.Set()
            );

            watchDone.Wait();
            if (watchError != null)
                throw watchError;
        }
        else
        {
            var listResp = ns == "ALL"
                ? client.CoreV1.ListSecretForAllNamespacesWithHttpMessagesAsync(
                    labelSelector: labelSelector,
                    timeoutSeconds: _config.WatchServerTimeout,
                    watch: true).Result
                : client.CoreV1.ListNamespacedSecretWithHttpMessagesAsync(
                    ns,
                    labelSelector: labelSelector,
                    timeoutSeconds: _config.WatchServerTimeout,
                    watch: true).Result;

            using var watcher = listResp.Watch<V1Secret, V1SecretList>(
                onEvent: (type, item) =>
                {
                    if (firstEvent)
                    {
                        _healthServer.MarkReady();
                        firstEvent = false;
                    }
                    ProcessWatchEvent(type, item, resource, requestUrl, requestMethod, requestPayload);
                },
                onError: ex =>
                {
                    watchError = ex;
                    watchDone.Set();
                },
                onClosed: () => watchDone.Set()
            );

            watchDone.Wait();
            if (watchError != null)
                throw watchError;
        }
    }

    private void ProcessWatchEvent(WatchEventType eventType, object item, string resource,
        string? requestUrl, string? requestMethod, object? requestPayload)
    {
        var metadata = GetMetadata(item);
        var key = metadata.NamespaceProperty + metadata.Name;

        _healthServer.UpdateK8sContact();

        // Ignore already processed
        if (_config.IgnoreAlreadyProcessed)
        {
            var versionMap = _resourcesVersionMap[resource];
            if (versionMap.TryGetValue(key, out var cached) && cached == metadata.ResourceVersion)
            {
                if (eventType == WatchEventType.Added || eventType == WatchEventType.Modified)
                {
                    _logger.LogDebug("Ignoring {EventType} {Resource} {Namespace}/{Name}", eventType, resource, metadata.NamespaceProperty, metadata.Name);
                    return;
                }
                else if (eventType == WatchEventType.Deleted)
                {
                    versionMap.TryRemove(key, out _);
                }
            }

            if (eventType == WatchEventType.Added || eventType == WatchEventType.Modified)
            {
                versionMap[key] = metadata.ResourceVersion;
            }
        }

        _logger.LogDebug("Working on {EventType} {Resource} {Namespace}/{Name}", eventType, resource, metadata.NamespaceProperty, metadata.Name);

        var destFolder = GetDestinationFolder(metadata, _config.Folder, _config.FolderAnnotation);
        bool filesChanged = false;
        bool isRemoved = eventType == WatchEventType.Deleted;

        if (resource == "configmap")
            filesChanged |= ProcessConfigMap(destFolder, (V1ConfigMap)item, resource, isRemoved);
        else
            filesChanged |= ProcessSecret(destFolder, (V1Secret)item, resource, isRemoved);

        if (!string.IsNullOrEmpty(_config.Script) && filesChanged)
            ScriptExecutor.Execute(_config.Script);

        if (!string.IsNullOrEmpty(requestUrl) && filesChanged)
            _httpRequester.SendRequest(requestUrl, requestMethod, _config.Enable5xx, requestPayload);
    }

    private bool ProcessConfigMap(string? destFolder, V1ConfigMap configMap, string resource, bool isRemoved)
    {
        bool filesChanged = false;
        var key = configMap.Metadata.NamespaceProperty + configMap.Metadata.Name;
        var objMap = _resourcesObjectMap[resource];

        // Get old state
        V1ConfigMap? oldConfigMap = null;
        string? oldDestFolder = null;
        if (objMap.TryGetValue(key, out var cached))
        {
            oldConfigMap = DeepCloneConfigMap((V1ConfigMap)cached.item);
            oldDestFolder = cached.destFolder;
        }
        oldConfigMap ??= DeepCloneConfigMap(configMap);
        oldDestFolder ??= destFolder;

        if (isRemoved)
        {
            destFolder = oldDestFolder;
            objMap.TryRemove(key, out _);
        }
        else
        {
            objMap[key] = (DeepCloneConfigMap(configMap), destFolder!);
        }

        if (configMap.Data == null && configMap.BinaryData == null)
        {
            _logger.LogWarning("No data/binaryData field in {Resource}", resource);
        }

        if (configMap.Data != null && destFolder != null)
        {
            filesChanged |= IterateData(configMap.Data.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value),
                destFolder, configMap.Metadata, resource, "ascii", isRemoved);
        }

        // Remove old data keys that no longer exist
        if (oldConfigMap.Data != null && !isRemoved)
        {
            var oldData = new Dictionary<string, object>(oldConfigMap.Data.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value));
            if (oldDestFolder == destFolder)
            {
                foreach (var key2 in (configMap.Data?.Keys ?? Enumerable.Empty<string>()).Intersect(oldData.Keys).ToList())
                    oldData.Remove(key2);
            }
            if (oldDestFolder != null)
                filesChanged |= IterateData(oldData, oldDestFolder, oldConfigMap.Metadata, resource, "ascii", true);
        }

        if (configMap.BinaryData != null && destFolder != null)
        {
            filesChanged |= IterateData(configMap.BinaryData.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value),
                destFolder, configMap.Metadata, resource, "binary", isRemoved);
        }

        // Remove old binary data keys that no longer exist
        if (oldConfigMap.BinaryData != null && !isRemoved)
        {
            var oldBinaryData = new Dictionary<string, object>(oldConfigMap.BinaryData.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value));
            if (oldDestFolder == destFolder)
            {
                foreach (var key2 in (configMap.BinaryData?.Keys ?? Enumerable.Empty<string>()).Intersect(oldBinaryData.Keys).ToList())
                    oldBinaryData.Remove(key2);
            }
            if (oldDestFolder != null)
                filesChanged |= IterateData(oldBinaryData, oldDestFolder, oldConfigMap.Metadata, resource, "binary", true);
        }

        return filesChanged;
    }

    private bool ProcessSecret(string? destFolder, V1Secret secret, string resource, bool isRemoved)
    {
        bool filesChanged = false;
        var key = secret.Metadata.NamespaceProperty + secret.Metadata.Name;
        var objMap = _resourcesObjectMap[resource];

        // Get old state
        V1Secret? oldSecret = null;
        string? oldDestFolder = null;
        if (objMap.TryGetValue(key, out var cached))
        {
            oldSecret = DeepCloneSecret((V1Secret)cached.item);
            oldDestFolder = cached.destFolder;
        }
        oldSecret ??= DeepCloneSecret(secret);
        oldDestFolder ??= destFolder;

        if (isRemoved)
        {
            destFolder = oldDestFolder;
            objMap.TryRemove(key, out _);
        }
        else
        {
            objMap[key] = (DeepCloneSecret(secret), destFolder!);
        }

        if (secret.Data == null)
        {
            _logger.LogWarning("No data field in {Resource}", resource);
        }

        if (secret.Data != null && destFolder != null)
        {
            // Secret data values are already base64 decoded by the K8s client into byte arrays
            filesChanged |= IterateSecretData(secret.Data, destFolder, secret.Metadata, resource, isRemoved);
        }

        // Remove old data keys that no longer exist
        if (oldSecret.Data != null && !isRemoved)
        {
            var oldData = new Dictionary<string, byte[]>(oldSecret.Data);
            if (oldDestFolder == destFolder)
            {
                foreach (var key2 in (secret.Data?.Keys ?? Enumerable.Empty<string>()).Intersect(oldData.Keys).ToList())
                    oldData.Remove(key2);
            }
            if (oldDestFolder != null)
                filesChanged |= IterateSecretData(oldData, oldDestFolder, oldSecret.Metadata, resource, true);
        }

        return filesChanged;
    }

    private bool IterateData(Dictionary<string, object> data, string destFolder, V1ObjectMeta metadata,
        string resource, string contentType, bool removeFiles)
    {
        bool filesChanged = false;
        foreach (var (dataKey, dataContent) in data)
        {
            filesChanged |= UpdateFile(dataKey, dataContent, destFolder, metadata, resource, contentType, removeFiles);
        }
        return filesChanged;
    }

    private bool IterateSecretData(IDictionary<string, byte[]> data, string destFolder, V1ObjectMeta metadata,
        string resource, bool removeFiles)
    {
        bool filesChanged = false;
        foreach (var (dataKey, dataContent) in data)
        {
            // Secret data is already decoded by K8s client - treat as binary
            filesChanged |= UpdateFileFromBytes(dataKey, dataContent, destFolder, metadata, resource, removeFiles);
        }
        return filesChanged;
    }

    private bool UpdateFile(string dataKey, object dataContent, string destFolder, V1ObjectMeta metadata,
        string resource, string contentType, bool remove)
    {
        try
        {
            string filename;
            byte[] fileData;

            if (contentType == "binary" && dataContent is byte[] binaryContent)
            {
                // Binary data from ConfigMap binaryData - already base64 decoded by K8s client
                (filename, fileData) = GetFileDataAndName(dataKey, binaryContent, true);
            }
            else
            {
                // Text data
                var textContent = dataContent.ToString() ?? string.Empty;
                (filename, fileData) = GetFileDataAndName(dataKey, textContent);
            }

            if (_config.UniqueFilenames)
            {
                filename = FileHelpers.UniqueFilename(filename, metadata.NamespaceProperty, resource, metadata.Name);
            }

            if (!remove)
            {
                return FileHelpers.WriteDataToFile(destFolder, filename, fileData,
                    contentType == "binary" ? "binary" : "ascii", _config.DefaultFileMode);
            }
            else
            {
                return FileHelpers.RemoveFile(destFolder, filename);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error when updating from '{DataKey}' into '{DestFolder}'", dataKey, destFolder);
            return false;
        }
    }

    private bool UpdateFileFromBytes(string dataKey, byte[] dataContent, string destFolder, V1ObjectMeta metadata,
        string resource, bool remove)
    {
        try
        {
            var (filename, fileData) = GetFileDataAndName(dataKey, dataContent, true);

            if (_config.UniqueFilenames)
            {
                filename = FileHelpers.UniqueFilename(filename, metadata.NamespaceProperty, resource, metadata.Name);
            }

            if (!remove)
            {
                return FileHelpers.WriteDataToFile(destFolder, filename, fileData, "binary", _config.DefaultFileMode);
            }
            else
            {
                return FileHelpers.RemoveFile(destFolder, filename);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error when updating from '{DataKey}' into '{DestFolder}'", dataKey, destFolder);
            return false;
        }
    }

    private (string filename, byte[] data) GetFileDataAndName(string fullFilename, string content)
    {
        if (fullFilename.EndsWith(".url"))
        {
            var filename = fullFilename[..^4];
            var response = _httpRequester.SendRequest(content, "GET", _config.Enable5xx);
            var responseText = _httpRequester.ReadResponseText(response);
            return (filename, Encoding.UTF8.GetBytes(responseText));
        }

        return (fullFilename, Encoding.UTF8.GetBytes(content));
    }

    private (string filename, byte[] data) GetFileDataAndName(string fullFilename, byte[] content, bool isBinary)
    {
        if (fullFilename.EndsWith(".url"))
        {
            var filename = fullFilename[..^4];
            var url = Encoding.UTF8.GetString(content);
            var response = _httpRequester.SendRequest(url, "GET", _config.Enable5xx);
            var responseBytes = _httpRequester.ReadResponseBytes(response);
            return (filename, responseBytes);
        }

        return (fullFilename, content);
    }

    private List<string> GetFilteredResourceNames(string ns, string resource)
    {
        var result = new List<string>();
        if (ns == "ALL" || string.IsNullOrEmpty(_config.ResourceName))
            return result;

        foreach (var rn in _config.ResourceName.Split(','))
        {
            var parts = rn.Split('/').Reverse().ToList();
            if (parts.Count == 3 && parts[2] != ns)
                continue;
            if (parts.Count == 2 && parts[1] != resource)
                continue;
            result.Add(parts[0]);
        }

        return result;
    }

    private static string GetDestinationFolder(V1ObjectMeta metadata, string defaultFolder, string folderAnnotation)
    {
        if (metadata.Annotations != null && metadata.Annotations.TryGetValue(folderAnnotation, out var annotationValue))
        {
            string destFolder;
            if (Path.IsPathRooted(annotationValue))
                destFolder = annotationValue;
            else
                destFolder = Path.Combine(defaultFolder, annotationValue);

            return destFolder;
        }
        return defaultFolder;
    }

    private static V1ObjectMeta GetMetadata(object item)
    {
        return item switch
        {
            V1ConfigMap cm => cm.Metadata,
            V1Secret s => s.Metadata,
            _ => throw new InvalidOperationException($"Unknown item type: {item.GetType()}")
        };
    }

    private static V1ConfigMap DeepCloneConfigMap(V1ConfigMap cm)
    {
        var clone = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta
            {
                Name = cm.Metadata.Name,
                NamespaceProperty = cm.Metadata.NamespaceProperty,
                ResourceVersion = cm.Metadata.ResourceVersion,
                Annotations = cm.Metadata.Annotations != null ? new Dictionary<string, string>(cm.Metadata.Annotations) : null,
                Labels = cm.Metadata.Labels != null ? new Dictionary<string, string>(cm.Metadata.Labels) : null
            },
            Data = cm.Data != null ? new Dictionary<string, string>(cm.Data) : null,
            BinaryData = cm.BinaryData != null ? new Dictionary<string, byte[]>(cm.BinaryData.ToDictionary(kvp => kvp.Key, kvp => (byte[])kvp.Value.Clone())) : null
        };
        return clone;
    }

    private static V1Secret DeepCloneSecret(V1Secret s)
    {
        var clone = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = s.Metadata.Name,
                NamespaceProperty = s.Metadata.NamespaceProperty,
                ResourceVersion = s.Metadata.ResourceVersion,
                Annotations = s.Metadata.Annotations != null ? new Dictionary<string, string>(s.Metadata.Annotations) : null,
                Labels = s.Metadata.Labels != null ? new Dictionary<string, string>(s.Metadata.Labels) : null
            },
            Data = s.Data != null ? new Dictionary<string, byte[]>(s.Data.ToDictionary(kvp => kvp.Key, kvp => (byte[])kvp.Value.Clone())) : null
        };
        return clone;
    }
}
