using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace K8sSidecar;

/// <summary>
/// Executes external scripts.
/// </summary>
public static class ScriptExecutor
{
    private static readonly ILogger Logger = Logging.CreateLogger("script_executor");

    public static void Execute(string scriptPath)
    {
        Logger.LogInformation("Executing script from {ScriptPath}", scriptPath);
        try
        {
            ProcessStartInfo psi;

            // Check if file is executable
            if (File.Exists(scriptPath))
            {
                // Try to run directly first, fallback to sh
                var fileInfo = new FileInfo(scriptPath);
                // On Linux, check executable permission
                if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        var testProc = Process.Start(new ProcessStartInfo
                        {
                            FileName = "test",
                            Arguments = $"-x {scriptPath}",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false
                        });
                        testProc?.WaitForExit();

                        if (testProc?.ExitCode == 0)
                        {
                            psi = new ProcessStartInfo
                            {
                                FileName = scriptPath,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false
                            };
                        }
                        else
                        {
                            psi = new ProcessStartInfo
                            {
                                FileName = "sh",
                                Arguments = scriptPath,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false
                            };
                        }
                    }
                    catch
                    {
                        psi = new ProcessStartInfo
                        {
                            FileName = "sh",
                            Arguments = scriptPath,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false
                        };
                    }
                }
                else
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = scriptPath,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                }
            }
            else
            {
                Logger.LogError("Script not found: {ScriptPath}", scriptPath);
                return;
            }

            var process = Process.Start(psi);
            if (process != null)
            {
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                Logger.LogDebug("Script stdout: {Stdout}", stdout);
                Logger.LogDebug("Script stderr: {Stderr}", stderr);
                Logger.LogDebug("Script exit code: {ExitCode}", process.ExitCode);

                if (process.ExitCode != 0)
                {
                    Logger.LogError("Script failed with exit code {ExitCode}. stderr: {Stderr}", process.ExitCode, stderr);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Script failed with error");
        }
    }
}
