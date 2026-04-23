using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Setup;

public sealed class UpgradeRollbackSnapshotStore
{
    private const string ManifestFileName = "snapshot.json";
    private const string PayloadDirectoryName = "payload";

    private readonly string _rootPath;
    private readonly string _manifestPath;
    private readonly string _payloadPath;

    public UpgradeRollbackSnapshotStore(string configPath)
    {
        var normalizedConfigPath = Path.GetFullPath(configPath);
        var key = BuildSnapshotKey(normalizedConfigPath);
        _rootPath = Path.Combine(GatewaySetupPaths.ResolveDefaultUpgradeSnapshotRootPath(), key);
        _manifestPath = Path.Combine(_rootPath, ManifestFileName);
        _payloadPath = Path.Combine(_rootPath, PayloadDirectoryName);
    }

    public string SnapshotDirectory => _rootPath;

    public string ResolvePayloadPath(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath, "payload");
        var fullPayloadPath = Path.GetFullPath(_payloadPath);
        var combined = Path.GetFullPath(Path.Combine(fullPayloadPath, normalized));
        if (!IsPathUnderRoot(combined, fullPayloadPath))
            throw new InvalidOperationException($"Rollback snapshot payload path '{relativePath}' escapes the snapshot payload directory.");

        return combined;
    }

    public UpgradeRollbackSnapshot? Load()
    {
        TryLoad(out var snapshot, out _);
        return snapshot;
    }

    public bool TryLoad(out UpgradeRollbackSnapshot? snapshot, out string? error)
    {
        snapshot = null;
        error = null;

        if (!File.Exists(_manifestPath))
            return false;

        try
        {
            snapshot = JsonSerializer.Deserialize(File.ReadAllText(_manifestPath), CoreJsonContext.Default.UpgradeRollbackSnapshot);
            if (snapshot is null)
            {
                error = $"Rollback snapshot manifest '{_manifestPath}' is empty or invalid.";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"Rollback snapshot manifest '{_manifestPath}' is corrupt or invalid JSON: {ex.Message}";
            return false;
        }
        catch (IOException ex)
        {
            error = $"Rollback snapshot manifest '{_manifestPath}' could not be read: {ex.Message}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"Rollback snapshot manifest '{_manifestPath}' could not be accessed: {ex.Message}";
            return false;
        }
        catch (NotSupportedException ex)
        {
            error = $"Rollback snapshot manifest '{_manifestPath}' uses an unsupported format: {ex.Message}";
            return false;
        }
        catch
        {
            error = $"Rollback snapshot manifest '{_manifestPath}' could not be loaded.";
            return false;
        }
    }

    public bool Save(UpgradeRollbackSnapshot snapshot, Action<string> populatePayload, out string? error)
    {
        var parentDirectory = Path.GetDirectoryName(_rootPath)
            ?? throw new InvalidOperationException("Snapshot root must contain a parent directory.");
        var tempRoot = _rootPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        error = null;

        try
        {
            Directory.CreateDirectory(parentDirectory);
            TryRestrictUnixDirectory(parentDirectory);
            Directory.CreateDirectory(tempRoot);
            TryRestrictUnixDirectory(tempRoot);
            var tempPayload = Path.Combine(tempRoot, PayloadDirectoryName);
            Directory.CreateDirectory(tempPayload);
            TryRestrictUnixDirectory(tempPayload);
            populatePayload(tempPayload);

            var manifestPath = Path.Combine(tempRoot, ManifestFileName);
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(snapshot, CoreJsonContext.Default.UpgradeRollbackSnapshot));
            TryRestrictUnixFile(manifestPath);

            ReplaceDirectory(tempRoot, _rootPath);
            TryRestrictUnixDirectory(_rootPath);
            HardenSnapshotTree(_rootPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static string BuildSnapshotKey(string configPath)
    {
        var stem = Path.GetFileNameWithoutExtension(configPath);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "config";

        var safeStem = new string(stem
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')
            .ToArray())
            .Trim('-');
        if (string.IsNullOrWhiteSpace(safeStem))
            safeStem = "config";

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configPath))).ToLowerInvariant();
        return $"{safeStem}-{hash[..12]}";
    }

    private static void ReplaceDirectory(string source, string destination)
    {
        if (!Directory.Exists(destination))
        {
            Directory.Move(source, destination);
            return;
        }

        var backup = destination + "." + Guid.NewGuid().ToString("N") + ".bak";
        Directory.Move(destination, backup);
        try
        {
            Directory.Move(source, destination);
            Directory.Delete(backup, recursive: true);
        }
        catch
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
            Directory.Move(backup, destination);
            throw;
        }
    }

    private static string NormalizeRelativePath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"Rollback snapshot {label} path is missing.");

        if (Path.IsPathRooted(path))
            throw new InvalidOperationException($"Rollback snapshot {label} path '{path}' must be relative.");

        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
            throw new InvalidOperationException($"Rollback snapshot {label} path '{path}' is invalid.");

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static bool IsPathUnderRoot(string candidatePath, string rootPath)
    {
        var normalizedRoot = EnsureTrailingSeparator(rootPath);
        var normalizedCandidate = Path.GetFullPath(candidatePath);
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.Ordinal);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        var normalized = Path.GetFullPath(path);
        return normalized.EndsWith(Path.DirectorySeparatorChar)
            ? normalized
            : normalized + Path.DirectorySeparatorChar;
    }

    private static void HardenSnapshotTree(string rootPath)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(rootPath))
            return;

        try
        {
            foreach (var directory in Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories))
                TryRestrictUnixDirectory(directory);

            foreach (var file in Directory.GetFiles(rootPath, "*", SearchOption.AllDirectories))
                TryRestrictUnixFile(file);

            TryRestrictUnixDirectory(rootPath);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void TryRestrictUnixDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void TryRestrictUnixFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            var sourceMode = File.GetUnixFileMode(path);
            var restrictedMode =
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                (HasAnyExecuteBit(sourceMode) ? UnixFileMode.UserExecute : 0);
            File.SetUnixFileMode(path, restrictedMode);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static bool HasAnyExecuteBit(UnixFileMode mode)
        => (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
}
