using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace K8sSidecar;

/// <summary>
/// File I/O helpers: write, remove, unique filename generation.
/// </summary>
public static class FileHelpers
{
    private static readonly ILogger Logger = Logging.CreateLogger("file_helpers");

    /// <summary>
    /// Write data to a file. Creates parent directories as needed.
    /// Compares SHA-256 hashes to avoid unnecessary writes.
    /// Returns true if the file was actually written (changed).
    /// </summary>
    public static bool WriteDataToFile(string folder, string filename, byte[] data, string dataType, string? defaultFileMode)
    {
        if (!Directory.Exists(folder))
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (UnauthorizedAccessException)
            {
                Logger.LogError("Error: insufficient privileges to create {Folder}. Skipping {Filename}.", folder, filename);
                return false;
            }
        }

        var absolutePath = Path.Combine(folder, filename);

        if (File.Exists(absolutePath))
        {
            // Compare hashes
            var newHash = SHA256.HashData(data);
            var existingHash = SHA256.HashData(File.ReadAllBytes(absolutePath));
            if (newHash.AsSpan().SequenceEqual(existingHash))
            {
                Logger.LogDebug("Contents of {Filename} haven't changed. Not overwriting existing file", filename);
                return false;
            }
        }

        Logger.LogInformation("Writing {AbsolutePath} ({DataType})", absolutePath, dataType);
        File.WriteAllBytes(absolutePath, data);

        if (!string.IsNullOrEmpty(defaultFileMode))
        {
            SetFileMode(absolutePath, defaultFileMode);
        }

        return true;
    }

    /// <summary>
    /// Write text data to a file.
    /// </summary>
    public static bool WriteDataToFile(string folder, string filename, string data, string dataType, string? defaultFileMode)
    {
        return WriteDataToFile(folder, filename, System.Text.Encoding.UTF8.GetBytes(data), dataType, defaultFileMode);
    }

    /// <summary>
    /// Remove a file. Returns true if the file was successfully removed.
    /// </summary>
    public static bool RemoveFile(string folder, string filename)
    {
        var completePath = Path.Combine(folder, filename);
        if (File.Exists(completePath))
        {
            Logger.LogInformation("Removing {CompletePath}", completePath);
            File.Delete(completePath);
            return true;
        }
        else
        {
            Logger.LogError("Unable to remove {CompletePath}, file not found", completePath);
            return false;
        }
    }

    /// <summary>
    /// Generate a unique filename: namespace_{ns}.{resource}_{resourceName}.{filename}
    /// </summary>
    public static string UniqueFilename(string filename, string ns, string resource, string resourceName)
    {
        return $"namespace_{ns}.{resource}_{resourceName}.{filename}";
    }

    /// <summary>
    /// Read text content from a file, trimmed.
    /// </summary>
    public static string? ReadFileContent(string filePath)
    {
        try
        {
            return File.ReadAllText(filePath).Trim();
        }
        catch (FileNotFoundException)
        {
            Logger.LogWarning("File not found at: {FilePath}", filePath);
        }
        catch (UnauthorizedAccessException)
        {
            Logger.LogError("No read permission for file: {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An unexpected error occurred reading {FilePath}", filePath);
        }
        return null;
    }

    private static void SetFileMode(string path, string mode)
    {
        // On Linux, set file permissions using chmod via process
        if (!OperatingSystem.IsLinux()) return;

        try
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"{mode} {path}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            process?.WaitForExit();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to set file mode {Mode} on {Path}", mode, path);
        }
    }
}
