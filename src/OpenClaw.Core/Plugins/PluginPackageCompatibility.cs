namespace OpenClaw.Core.Plugins;

/// <summary>
/// Validates package-declared bridge API and host version floors before plugin code runs.
/// </summary>
public static class PluginPackageCompatibility
{
    // This host implements the upstream bridge surface exercised by the 2026.5.4
    // public compatibility fixtures. Raise deliberately as newer SDK contracts land.
    public static readonly Version SupportedPluginApiVersion = new(2026, 5, 4);
    public static readonly Version HostCompatibilityVersion = new(2026, 5, 4);

    public static IReadOnlyList<PluginCompatibilityDiagnostic> Validate(DiscoveredPlugin plugin)
    {
        var diagnostics = Validate(
            plugin.PluginApiRange,
            plugin.MinHostVersion,
            plugin.Manifest.Id,
            plugin.RootPath).ToList();
        if (!string.IsNullOrWhiteSpace(plugin.ExpectedIntegrity))
        {
            diagnostics.Add(new PluginCompatibilityDiagnostic
            {
                Severity = "error",
                Code = "package_integrity_unverified",
                Message = $"Plugin '{plugin.Manifest.Id}' declares package integrity, but extracted plugin directories cannot currently be verified against package-manager integrity metadata.",
                Surface = "package_metadata",
                Path = plugin.RootPath
            });
        }

        return diagnostics;
    }

    public static IReadOnlyList<PluginCompatibilityDiagnostic> Validate(
        string? pluginApiRange,
        string? minHostVersion,
        string pluginId,
        string path)
    {
        var diagnostics = new List<PluginCompatibilityDiagnostic>();
        ValidateFloor(
            pluginApiRange,
            SupportedPluginApiVersion,
            "plugin_api_version_unsupported",
            "plugin API",
            pluginId,
            path,
            diagnostics);
        ValidateFloor(
            minHostVersion,
            HostCompatibilityVersion,
            "host_version_unsupported",
            "host",
            pluginId,
            path,
            diagnostics);
        return diagnostics;
    }

    private static void ValidateFloor(
        string? declaredRange,
        Version supportedVersion,
        string diagnosticCode,
        string label,
        string pluginId,
        string path,
        ICollection<PluginCompatibilityDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(declaredRange))
            return;

        var normalized = declaredRange.Trim();
        foreach (var rangeOperator in new[] { ">=", "<=", "==", ">", "=", "^", "~" })
        {
            if (!normalized.StartsWith(rangeOperator, StringComparison.Ordinal))
                continue;

            normalized = normalized[rangeOperator.Length..].Trim();
            break;
        }

        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];

        var suffixIndex = normalized.IndexOfAny(['-', '+', ' ', '<', '>', '|']);
        if (suffixIndex >= 0)
            normalized = normalized[..suffixIndex];

        if (!normalized.Contains('.', StringComparison.Ordinal) && normalized.Length > 0)
            normalized += ".0";

        if (!Version.TryParse(normalized, out var requiredVersion))
        {
            diagnostics.Add(new PluginCompatibilityDiagnostic
            {
                Severity = "error",
                Code = "invalid_plugin_version_range",
                Message = $"Plugin '{pluginId}' declares an unsupported {label} version range '{declaredRange}'.",
                Surface = "package_metadata",
                Path = path
            });
            return;
        }

        if (requiredVersion <= supportedVersion)
            return;

        diagnostics.Add(new PluginCompatibilityDiagnostic
        {
            Severity = "error",
            Code = diagnosticCode,
            Message = $"Plugin '{pluginId}' requires {label} version {requiredVersion}, but this bridge supports {supportedVersion}.",
            Surface = "package_metadata",
            Path = path
        });
    }
}
