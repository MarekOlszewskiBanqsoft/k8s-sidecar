using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace K8sSidecar;

/// <summary>
/// Lightweight HTTP health check server (readiness + liveness) on a configurable port.
/// </summary>
public sealed class HealthServer
{
    private static readonly ILogger Logger = Logging.CreateLogger("health_server");

    private volatile bool _isReady;
    private DateTime _lastK8sContact = DateTime.UtcNow;
    private readonly List<Thread> _watcherThreads = new();
    private readonly int _port;
    private readonly TimeSpan _k8sContactThreshold = TimeSpan.FromSeconds(60);

    public HealthServer(int port)
    {
        _port = port;
    }

    public void MarkReady()
    {
        _isReady = true;
    }

    public void UpdateK8sContact()
    {
        _lastK8sContact = DateTime.UtcNow;
    }

    public void RegisterWatcherThreads(List<Thread> threads)
    {
        lock (_watcherThreads)
        {
            _watcherThreads.Clear();
            _watcherThreads.AddRange(threads);
        }
    }

    public void Start()
    {
        var thread = new Thread(() => RunServer())
        {
            IsBackground = true,
            Name = "HealthServer"
        };
        thread.Start();
    }

    private void RunServer()
    {
        var listener = new HttpListener();
        // Listen on all interfaces (IPv4 and IPv6)
        listener.Prefixes.Add($"http://+:{_port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException)
        {
            // Fallback: try localhost only
            listener = new HttpListener();
            listener.Prefixes.Add($"http://*:{_port}/");
            listener.Start();
        }

        Logger.LogInformation("Starting health server on port {Port}", _port);

        while (true)
        {
            try
            {
                var context = listener.GetContext();
                HandleRequest(context);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Health server error");
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        if (request.Url?.AbsolutePath != "/healthz")
        {
            WriteResponse(response, 404, "Not Found");
            return;
        }

        // Readiness check
        if (!_isReady)
        {
            WriteResponse(response, 503, "NOT READY");
            return;
        }

        // Liveness: k8s contact
        if (DateTime.UtcNow - _lastK8sContact > _k8sContactThreshold)
        {
            WriteResponse(response, 503, "NOT LIVE (K8s contact lost)");
            return;
        }

        // Liveness: watcher threads
        lock (_watcherThreads)
        {
            foreach (var t in _watcherThreads)
            {
                if (!t.IsAlive)
                {
                    WriteResponse(response, 503, "NOT LIVE (watcher thread died)");
                    return;
                }
            }
        }

        WriteResponse(response, 200, "OK");
    }

    private static void WriteResponse(HttpListenerResponse response, int statusCode, string body)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/plain; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }
}
