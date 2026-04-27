using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Core.Setup;
using OpenClaw.Core.Skills;
using OpenClaw.Core.Validation;

namespace OpenClaw.Cli;

internal static class UpgradeCommands
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, string currentDirectory)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp(output);
            return 0;
        }

        var subcommand = args[0].Trim().ToLowerInvariant();
        var rest = args[1..];
        return subcommand switch
        {
            "check" => await RunCheckAsync(rest, output, error, currentDirectory),
            "rollback" => await RunRollbackAsync(rest, output, error),
            _ => UnknownSubcommand(subcommand, output, error)
        };
    }

    private static async Task<int> RunCheckAsync(string[] args, TextWriter output, TextWriter error, string currentDirectory)
    {
        var parsed = CliArgs.Parse(args);
        if (parsed.ShowHelp)
        {
            PrintHelp(output);
            return 0;
        }

        var configPath = ResolveConfigPath(parsed);
        GatewayConfig config;
        try
        {
            config = GatewayConfigFile.Load(configPath);
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }

        var workspacePath = ResolveWorkspacePath(config);
        var offline = parsed.HasFlag("--offline");
        var verification = await EvaluateVerificationAsync(config, workspacePath, offline);
        var plugins = EvaluatePlugins(config, workspacePath);
        var skills = EvaluateSkills(config, workspacePath);
        var migrationImpact = EvaluateMigrationImpact(configPath, config);
        var rollbackSnapshot = EvaluateRollbackSnapshot(configPath, workspacePath, offline, !offline, AggregateStatus(
            verification.Status,
            plugins.Status,
            skills.Status,
            migrationImpact.Status));

        var overall = AggregateStatus(
            verification.Status,
            plugins.Status,
            skills.Status,
            migrationImpact.Status,
            rollbackSnapshot.Status);

        output.WriteLine("OpenClaw upgrade preflight");
        output.WriteLine($"Config: {configPath}");
        output.WriteLine($"Workspace: {workspacePath ?? "not configured"}");
        output.WriteLine($"Overall result: {overall}");
        output.WriteLine();
        output.WriteLine("Checks:");
        WriteCheck(output, verification);
        WriteCheck(output, plugins);
        WriteCheck(output, skills);
        WriteCheck(output, migrationImpact);
        WriteCheck(output, rollbackSnapshot);

        var nextActions = verification.NextActions
            .Concat(plugins.NextActions)
            .Concat(skills.NextActions)
            .Concat(migrationImpact.NextActions)
            .Concat(rollbackSnapshot.NextActions)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (nextActions.Length > 0)
        {
            output.WriteLine();
            output.WriteLine("Recommended next actions:");
            foreach (var action in nextActions)
                output.WriteLine($"- {action}");
        }

        if (string.Equals(overall, SetupCheckStates.Fail, StringComparison.Ordinal))
        {
            output.WriteLine();
            output.WriteLine("Upgrade preflight failed. Resolve the blocking issues before upgrading.");
            return 1;
        }

        return 0;
    }

    private static async Task<int> RunRollbackAsync(string[] args, TextWriter output, TextWriter error)
    {
        var parsed = CliArgs.Parse(args);
        if (parsed.ShowHelp)
        {
            PrintHelp(output);
            return 0;
        }

        var configPath = ResolveConfigPath(parsed);
        var quotedConfigPath = GatewayConfigFile.QuoteIfNeeded(configPath);
        var store = new UpgradeRollbackSnapshotStore(configPath);
        if (!store.TryLoad(out var snapshot, out var loadError))
        {
            if (string.IsNullOrWhiteSpace(loadError))
                error.WriteLine($"No rollback snapshot was found for {quotedConfigPath}. Run 'openclaw upgrade check --config {quotedConfigPath}' before upgrading.");
            else
                error.WriteLine($"{loadError} Delete the broken snapshot and rerun 'openclaw upgrade check --config {quotedConfigPath}' before upgrading.");
            return 1;
        }

        var rollbackSnapshot = snapshot ?? throw new InvalidOperationException("Rollback snapshot loading succeeded without a snapshot payload.");

        try
        {
            RestoreSnapshot(rollbackSnapshot, store, configPath);
        }
        catch (Exception ex)
        {
            error.WriteLine($"Failed to restore rollback snapshot: {ex.Message}");
            return 1;
        }

        output.WriteLine("Restored last-known-good setup snapshot.");
        output.WriteLine($"Config: {configPath}");
        output.WriteLine($"Captured at: {rollbackSnapshot.CreatedAtUtc:O}");
        output.WriteLine($"Captured by: openclaw {rollbackSnapshot.CreatedByVersion}");
        output.WriteLine($"Snapshot directory: {store.SnapshotDirectory}");

        var currentVersion = GetCurrentVersion();
        if (!string.Equals(rollbackSnapshot.CreatedByVersion, currentVersion, StringComparison.Ordinal))
            output.WriteLine($"Current CLI version: openclaw {currentVersion}");

        output.WriteLine();
        output.WriteLine("Re-running setup verification on the restored files...");
        var verifyExitCode = await SetupLifecycleCommand.RunVerifyAsync(BuildRollbackVerifyArgs(configPath, parsed, rollbackSnapshot), output, error);
        if (verifyExitCode == 0)
        {
            output.WriteLine();
            output.WriteLine("Rollback completed successfully.");
            output.WriteLine($"Gateway doctor: dotnet run --project src/OpenClaw.Gateway -c Release -- --config {GatewayConfigFile.QuoteIfNeeded(configPath)} --doctor");
        }

        return verifyExitCode;
    }

    private static async Task<UpgradeCheckSummary> EvaluateVerificationAsync(GatewayConfig config, string? workspacePath, bool offline)
    {
        var localState = LocalSetupStateLoader.Load(config.Memory.StoragePath);
        var verification = await SetupVerificationService.VerifyAsync(new SetupVerificationRequest
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime),
            Policy = localState.Policy,
            OperatorAccountCount = localState.OperatorAccountCount,
            Offline = offline,
            RequireProvider = !offline,
            WorkspacePath = workspacePath,
            ModelDoctor = ModelDoctorEvaluator.Build(config)
        }, CancellationToken.None);

        var detail = new List<string>();
        foreach (var check in verification.Checks.Where(static item => !string.Equals(item.Status, SetupCheckStates.Pass, StringComparison.Ordinal)))
            detail.Add($"[{check.Status}] {check.Label}: {check.Summary}");

        var summary = $"Setup verification reported {verification.Checks.Count} checks with overall status '{verification.OverallStatus}'.";
        if (offline)
            summary += " Provider smoke was skipped because offline mode is enabled.";

        return new UpgradeCheckSummary(
            "Config and provider readiness",
            verification.OverallStatus,
            summary,
            detail,
            verification.RecommendedNextActions);
    }

    private static UpgradeCheckSummary EvaluatePlugins(GatewayConfig config, string? workspacePath)
    {
        var result = PluginDiscovery.DiscoverWithDiagnostics(config.Plugins, workspacePath);
        var details = new List<string>();
        var nextActions = new List<string>();
        var status = SetupCheckStates.Pass;

        foreach (var report in result.Reports)
        {
            foreach (var diagnostic in report.Diagnostics)
            {
                var severity = NormalizeSeverity(diagnostic.Severity);
                status = AggregateStatus(status, severity);
                details.Add($"[{severity}] {report.PluginId}: {diagnostic.Message}");
            }
        }

        foreach (var plugin in result.Plugins)
        {
            var manifestPath = Path.Combine(plugin.RootPath, "openclaw.plugin.json");
            var packageJsonPath = Path.Combine(plugin.RootPath, "package.json");
            if (File.Exists(manifestPath) || File.Exists(packageJsonPath))
            {
                var inspection = PluginCommands.InspectCandidate(plugin.RootPath, plugin.RootPath, sourceIsNpm: false);
                if (!inspection.Success)
                {
                    status = AggregateStatus(status, SetupCheckStates.Fail);
                    details.Add($"[{SetupCheckStates.Fail}] {plugin.Manifest.Id}: {inspection.ErrorMessage}");
                    continue;
                }

                if (!inspection.CanInstall)
                {
                    status = AggregateStatus(status, SetupCheckStates.Fail);
                    details.Add($"[{SetupCheckStates.Fail}] {inspection.PluginId}: compatibility inspection reported blocking errors.");
                    foreach (var diagnostic in inspection.Diagnostics.Where(static item => string.Equals(item.Severity, "error", StringComparison.OrdinalIgnoreCase)))
                        details.Add($"[{SetupCheckStates.Fail}] {inspection.PluginId}: [{diagnostic.Code}] {diagnostic.Message}");
                    nextActions.Add($"Review plugin '{inspection.PluginId}' and resolve its compatibility errors before upgrading.");
                    continue;
                }

                if (inspection.WarningCount > 0 || inspection.Warnings.Count > 0 || string.Equals(inspection.TrustLevel, "untrusted", StringComparison.Ordinal))
                {
                    status = AggregateStatus(status, SetupCheckStates.Warn);
                    details.Add($"[{SetupCheckStates.Warn}] {inspection.PluginId}: trust={inspection.TrustLevel}, warnings={inspection.WarningCount + inspection.Warnings.Count}.");
                }
            }
            else
            {
                status = AggregateStatus(status, SetupCheckStates.Warn);
                details.Add($"[{SetupCheckStates.Warn}] {plugin.Manifest.Id}: entry-only plugin without a structured manifest-backed compatibility surface.");
            }
        }

        if (details.Count == 0)
            details.Add("No blocking plugin compatibility issues were detected.");

        if (string.Equals(status, SetupCheckStates.Warn, StringComparison.Ordinal))
            nextActions.Add("Review entry-only or warning-level plugins before upgrading the gateway/runtime.");

        var summary = result.Plugins.Count == 0 && result.Reports.Count == 0
            ? "No workspace, global, or configured plugins were discovered."
            : $"Inspected {result.Plugins.Count} plugin(s) with {result.Reports.Count} discovery diagnostic report(s).";

        return new UpgradeCheckSummary("Plugin compatibility", status, summary, details, nextActions);
    }

    private static UpgradeCheckSummary EvaluateSkills(GatewayConfig config, string? workspacePath)
    {
        var details = new List<string>();
        var nextActions = new List<string>();
        var status = SetupCheckStates.Pass;
        var totalInspected = 0;

        foreach (var root in EnumerateSkillRoots(config, workspacePath))
        {
            foreach (var inspection in SkillInspector.InspectInstalledRoot(root.Path, root.Source))
            {
                totalInspected++;
                if (!inspection.Success || inspection.Definition is null)
                {
                    status = AggregateStatus(status, SetupCheckStates.Fail);
                    details.Add($"[{SetupCheckStates.Fail}] {root.Path}: {inspection.ErrorMessage ?? "Failed to inspect skill."}");
                    continue;
                }

                var requirementIssues = EvaluateSkillRequirements(config, inspection.Definition);
                if (requirementIssues.Count > 0)
                {
                    status = AggregateStatus(status, SetupCheckStates.Warn);
                    foreach (var issue in requirementIssues)
                        details.Add($"[{SetupCheckStates.Warn}] {inspection.Definition.Name}: {issue}");
                }
            }
        }

        if (details.Count == 0)
            details.Add("No blocking skill compatibility issues were detected.");

        if (string.Equals(status, SetupCheckStates.Warn, StringComparison.Ordinal))
            nextActions.Add("Resolve missing skill requirements before upgrading so post-upgrade validation stays green.");

        var summary = totalInspected == 0
            ? "No managed, workspace, or extra skill directories were discovered."
            : $"Inspected {totalInspected} skill package(s) across the configured roots.";

        return new UpgradeCheckSummary("Skill compatibility", status, summary, details, nextActions);
    }

    private static UpgradeCheckSummary EvaluateMigrationImpact(string configPath, GatewayConfig config)
    {
        var details = new List<string>();
        var nextActions = new List<string>();
        var status = SetupCheckStates.Pass;

        if (configPath.Contains($"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}OpenClaw.Gateway{Path.DirectorySeparatorChar}appsettings", StringComparison.OrdinalIgnoreCase))
        {
            status = AggregateStatus(status, SetupCheckStates.Warn);
            details.Add($"[{SetupCheckStates.Warn}] Config points at a checked-in gateway appsettings file. Upgrades are safer when you use an external config generated by setup.");
            nextActions.Add("Move to an external config file generated by 'openclaw setup' before upgrading.");
        }

        if (!string.Equals(RuntimeModeResolver.Normalize(config.Runtime.Mode), "auto", StringComparison.Ordinal))
        {
            status = AggregateStatus(status, SetupCheckStates.Warn);
            details.Add($"[{SetupCheckStates.Warn}] Runtime mode is pinned to '{RuntimeModeResolver.Normalize(config.Runtime.Mode)}', which narrows the supported upgrade lane.");
        }

        if (!string.Equals(RuntimeOrchestrator.Normalize(config.Runtime.Orchestrator), RuntimeOrchestrator.Native, StringComparison.Ordinal))
        {
            status = AggregateStatus(status, SetupCheckStates.Warn);
            details.Add($"[{SetupCheckStates.Warn}] Runtime orchestrator is '{RuntimeOrchestrator.Normalize(config.Runtime.Orchestrator)}' instead of the default native path.");
        }

        if (config.Plugins.DynamicNative.Enabled)
        {
            status = AggregateStatus(status, SetupCheckStates.Warn);
            details.Add($"[{SetupCheckStates.Warn}] Dynamic native plugins are enabled, which increases upgrade compatibility risk.");
        }

        if (config.Plugins.Mcp.Enabled)
        {
            status = AggregateStatus(status, SetupCheckStates.Warn);
            details.Add($"[{SetupCheckStates.Warn}] MCP plugin bridges are enabled. Re-check bridge compatibility after the upgrade.");
        }

        if (config.Plugins.Load.Paths.Length > 0)
        {
            status = AggregateStatus(status, SetupCheckStates.Warn);
            details.Add($"[{SetupCheckStates.Warn}] Custom plugin load paths are configured: {string.Join(", ", config.Plugins.Load.Paths)}");
        }

        if (config.Skills.Load.ExtraDirs.Length > 0)
        {
            status = AggregateStatus(status, SetupCheckStates.Warn);
            details.Add($"[{SetupCheckStates.Warn}] Extra skill directories are configured: {string.Join(", ", config.Skills.Load.ExtraDirs)}");
        }

        if (details.Count == 0)
            details.Add("Config uses the default native runtime/orchestrator lane with no extra compatibility-surface overrides.");
        else
            nextActions.Add("Treat this install as an elevated-risk upgrade lane and rerun setup verification immediately after upgrading.");

        return new UpgradeCheckSummary(
            "Migration impact",
            status,
            "Estimates how much the current install relies on non-default or expanded compatibility surfaces.",
            details,
            nextActions);
    }

    private static UpgradeCheckSummary EvaluateRollbackSnapshot(
        string configPath,
        string? workspacePath,
        bool offline,
        bool requireProvider,
        string preflightStatus)
    {
        if (string.Equals(preflightStatus, SetupCheckStates.Fail, StringComparison.Ordinal))
        {
            return new UpgradeCheckSummary(
                "Rollback snapshot",
                SetupCheckStates.Skip,
                "Snapshot not updated because the preflight already found blocking issues.",
                ["[skip] Resolve the blocking issues and rerun 'openclaw upgrade check' to refresh the last-known-good snapshot."],
                ["Resolve the blocking upgrade issues before relying on rollback protection."]);
        }

        var captureItems = BuildRollbackCaptureItems(configPath);
        var snapshot = new UpgradeRollbackSnapshot
        {
            CreatedByVersion = GetCurrentVersion(),
            ConfigPath = configPath,
            WorkspacePath = workspacePath,
            VerificationStatus = preflightStatus,
            Offline = offline,
            RequireProvider = requireProvider,
            Artifacts = captureItems
                .Select(static item => new UpgradeRollbackSnapshotArtifact
                {
                    Kind = item.Kind,
                    TargetPath = item.TargetPath,
                    Exists = item.Exists,
                    IsDirectory = item.IsDirectory,
                    SnapshotRelativePath = item.SnapshotRelativePath
                })
                .ToArray()
        };

        var store = new UpgradeRollbackSnapshotStore(configPath);
        if (!store.Save(snapshot, payloadRoot => CaptureSnapshotArtifacts(captureItems, payloadRoot), out var error))
        {
            return new UpgradeCheckSummary(
                "Rollback snapshot",
                SetupCheckStates.Fail,
                "Failed to save the last-known-good rollback snapshot.",
                [$"[{SetupCheckStates.Fail}] {error ?? "Unknown snapshot persistence error."}"],
                ["Fix snapshot storage permissions or disk space, then rerun 'openclaw upgrade check' before upgrading."]);
        }

        var artifactSummary = $"{snapshot.Artifacts.Count(static artifact => artifact.Exists)} artifact(s) captured; {snapshot.Artifacts.Count(static artifact => !artifact.Exists)} tracked as absent.";
        return new UpgradeCheckSummary(
            "Rollback snapshot",
            SetupCheckStates.Pass,
            $"Saved the last-known-good setup snapshot. {artifactSummary}",
            [
                $"[{SetupCheckStates.Pass}] Snapshot directory: {store.SnapshotDirectory}",
                $"[{SetupCheckStates.Pass}] Rollback command: openclaw upgrade rollback --config {GatewayConfigFile.QuoteIfNeeded(configPath)}"
            ],
            [$"Use 'openclaw upgrade rollback --config {GatewayConfigFile.QuoteIfNeeded(configPath)}' to restore this snapshot if the upgrade regresses your setup."]);
    }

    private static IEnumerable<(string Path, SkillSource Source)> EnumerateSkillRoots(GatewayConfig config, string? workspacePath)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (config.Skills.Load.IncludeManaged)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var managed = Path.Combine(home, ".openclaw", "skills");
            if (seen.Add(managed))
                yield return (managed, SkillSource.Managed);
        }

        if (config.Skills.Load.IncludeWorkspace && !string.IsNullOrWhiteSpace(workspacePath))
        {
            var workspaceSkills = Path.Combine(workspacePath, "skills");
            if (seen.Add(workspaceSkills))
                yield return (workspaceSkills, SkillSource.Workspace);
        }

        foreach (var extraDir in config.Skills.Load.ExtraDirs)
        {
            var resolved = ResolveConfiguredPath(extraDir);
            if (!string.IsNullOrWhiteSpace(resolved) && seen.Add(resolved))
                yield return (resolved, SkillSource.Extra);
        }
    }

    private static IReadOnlyList<string> EvaluateSkillRequirements(GatewayConfig config, SkillDefinition definition)
    {
        var issues = new List<string>();
        config.Skills.Entries.TryGetValue(definition.Metadata.SkillKey ?? definition.Name, out var entry);
        if (entry is null)
            config.Skills.Entries.TryGetValue(definition.Name, out entry);

        foreach (var env in definition.Metadata.RequireEnv)
        {
            var fromEntry = entry?.Env.ContainsKey(env) == true;
            var fromApiKey = string.Equals(definition.Metadata.PrimaryEnv, env, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry?.ApiKey);
            if (!fromEntry && !fromApiKey && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(env)))
                issues.Add($"required env '{env}' is not set.");
        }

        foreach (var binary in definition.Metadata.RequireBins)
        {
            if (!IsBinaryAvailable(binary))
                issues.Add($"required binary '{binary}' is not available on PATH.");
        }

        if (definition.Metadata.RequireAnyBins.Length > 0 &&
            !definition.Metadata.RequireAnyBins.Any(IsBinaryAvailable))
        {
            issues.Add($"none of the required fallback binaries are available on PATH: {string.Join(", ", definition.Metadata.RequireAnyBins)}");
        }

        foreach (var configKey in definition.Metadata.RequireConfig)
        {
            if (entry?.Config.TryGetValue(configKey, out var value) != true || string.IsNullOrWhiteSpace(value))
                issues.Add($"required skills.entries config '{configKey}' is not configured.");
        }

        return issues;
    }

    private static bool IsBinaryAvailable(string binaryName)
    {
        if (string.IsNullOrWhiteSpace(binaryName))
            return false;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var candidates = OperatingSystem.IsWindows()
            ? new[] { binaryName, binaryName + ".exe", binaryName + ".cmd", binaryName + ".bat" }
            : new[] { binaryName };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(directory, candidate);
                if (File.Exists(fullPath))
                    return true;
            }
        }

        return false;
    }

    private static void WriteCheck(TextWriter output, UpgradeCheckSummary check)
    {
        output.WriteLine($"- [{check.Status}] {check.Label}: {check.Summary}");
        foreach (var detail in check.Details)
            output.WriteLine($"  {detail}");
    }

    private static string AggregateStatus(params string[] statuses)
    {
        var normalized = statuses.Select(NormalizeStatus).ToArray();
        if (normalized.Contains(SetupCheckStates.Fail, StringComparer.Ordinal))
            return SetupCheckStates.Fail;
        if (normalized.Contains(SetupCheckStates.Warn, StringComparer.Ordinal) || normalized.Contains(SetupCheckStates.Skip, StringComparer.Ordinal))
            return SetupCheckStates.Warn;
        return SetupCheckStates.Pass;
    }

    private static string NormalizeSeverity(string severity)
        => string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase)
            ? SetupCheckStates.Fail
            : SetupCheckStates.Warn;

    private static string NormalizeStatus(string status)
        => status.Trim().ToLowerInvariant() switch
        {
            var value when value == SetupCheckStates.Fail => SetupCheckStates.Fail,
            var value when value == SetupCheckStates.Warn => SetupCheckStates.Warn,
            var value when value == SetupCheckStates.Skip => SetupCheckStates.Skip,
            _ => SetupCheckStates.Pass
        };

    private static string ResolveConfigPath(CliArgs parsed)
        => Path.GetFullPath(GatewaySetupPaths.ExpandPath(parsed.GetOption("--config") ?? GatewaySetupPaths.DefaultConfigPath));

    private static string[] BuildRollbackVerifyArgs(string configPath, CliArgs parsed, UpgradeRollbackSnapshot snapshot)
    {
        var args = new List<string>
        {
            "--config",
            configPath
        };

        if (parsed.HasFlag("--offline") || snapshot.Offline)
            args.Add("--offline");
        if (parsed.HasFlag("--require-provider") || snapshot.RequireProvider)
            args.Add("--require-provider");

        return [.. args];
    }

    private static IReadOnlyList<RollbackCaptureItem> BuildRollbackCaptureItems(string configPath)
    {
        var envExamplePath = GatewaySetupArtifacts.BuildEnvExamplePath(configPath);
        var deployDirectory = SetupLifecycleCommand.GetDeployDirectory(configPath);

        return
        [
            new RollbackCaptureItem("config", configPath, configPath, false, File.Exists(configPath), "config.json"),
            new RollbackCaptureItem("env_example", envExamplePath, envExamplePath, false, File.Exists(envExamplePath), "env.example"),
            new RollbackCaptureItem("deploy", deployDirectory, deployDirectory, true, Directory.Exists(deployDirectory), "deploy")
        ];
    }

    private static void CaptureSnapshotArtifacts(IReadOnlyList<RollbackCaptureItem> items, string payloadRoot)
    {
        foreach (var item in items.Where(static item => item.Exists && !string.IsNullOrWhiteSpace(item.SnapshotRelativePath)))
        {
            var destination = Path.Combine(payloadRoot, item.SnapshotRelativePath!);
            if (item.IsDirectory)
                CopyDirectory(item.SourcePath, destination, UnixModeCopyStrategy.OwnerOnlyPreserveExecute);
            else
                CopyFile(item.SourcePath, destination, UnixModeCopyStrategy.OwnerOnlyPreserveExecute);
        }
    }

    private static void RestoreSnapshot(UpgradeRollbackSnapshot snapshot, UpgradeRollbackSnapshotStore store, string configPath)
    {
        foreach (var artifact in ValidateRestoreArtifacts(snapshot, configPath))
        {
            if (artifact.Exists)
            {
                var payloadPath = store.ResolvePayloadPath(artifact.SnapshotRelativePath!);
                if (artifact.IsDirectory)
                    ReplaceDirectory(payloadPath, artifact.TargetPath);
                else
                    CopyFile(payloadPath, artifact.TargetPath);
                continue;
            }

            if (artifact.IsDirectory)
            {
                if (Directory.Exists(artifact.TargetPath))
                    Directory.Delete(artifact.TargetPath, recursive: true);
            }
            else if (File.Exists(artifact.TargetPath))
            {
                File.Delete(artifact.TargetPath);
            }
        }
    }

    private static IReadOnlyList<ValidatedRestoreArtifact> ValidateRestoreArtifacts(UpgradeRollbackSnapshot snapshot, string configPath)
    {
        var expectedByKind = BuildRollbackCaptureItems(configPath)
            .ToDictionary(static item => item.Kind, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var validated = new List<ValidatedRestoreArtifact>(snapshot.Artifacts.Count);

        foreach (var artifact in snapshot.Artifacts)
        {
            if (!expectedByKind.TryGetValue(artifact.Kind, out var expected))
                throw new InvalidOperationException($"Snapshot artifact '{artifact.Kind}' is not a supported rollback artifact kind.");

            if (!seen.Add(artifact.Kind))
                throw new InvalidOperationException($"Snapshot artifact '{artifact.Kind}' appears multiple times in the rollback manifest.");

            if (artifact.IsDirectory != expected.IsDirectory)
                throw new InvalidOperationException($"Snapshot artifact '{artifact.Kind}' has an unexpected directory/file shape.");

            var expectedTargetPath = Path.GetFullPath(expected.TargetPath);
            var manifestTargetPath = Path.GetFullPath(artifact.TargetPath);
            if (!PathsEqual(expectedTargetPath, manifestTargetPath))
            {
                throw new InvalidOperationException(
                    $"Snapshot artifact '{artifact.Kind}' has an unexpected restore target path '{artifact.TargetPath}'.");
            }

            if (!artifact.Exists)
            {
                if (!string.IsNullOrWhiteSpace(artifact.SnapshotRelativePath))
                {
                    throw new InvalidOperationException(
                        $"Snapshot artifact '{artifact.Kind}' is marked absent but still includes a payload path.");
                }

                validated.Add(new ValidatedRestoreArtifact(artifact.Kind, expectedTargetPath, artifact.IsDirectory, false, null));
                continue;
            }

            var snapshotRelativePath = ValidateSnapshotRelativePath(artifact.SnapshotRelativePath, artifact.Kind);
            if (!string.Equals(snapshotRelativePath, expected.SnapshotRelativePath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Snapshot artifact '{artifact.Kind}' has an unexpected payload path '{artifact.SnapshotRelativePath}'.");
            }

            validated.Add(new ValidatedRestoreArtifact(artifact.Kind, expectedTargetPath, artifact.IsDirectory, true, snapshotRelativePath));
        }

        var missingKinds = expectedByKind.Keys.Where(kind => !seen.Contains(kind)).ToArray();
        if (missingKinds.Length > 0)
        {
            throw new InvalidOperationException(
                $"Rollback snapshot manifest is missing required artifact(s): {string.Join(", ", missingKinds)}.");
        }

        return validated;
    }

    private static string ValidateSnapshotRelativePath(string? snapshotRelativePath, string artifactKind)
    {
        if (string.IsNullOrWhiteSpace(snapshotRelativePath))
            throw new InvalidOperationException($"Snapshot artifact '{artifactKind}' is missing its payload path.");

        if (Path.IsPathRooted(snapshotRelativePath))
            throw new InvalidOperationException($"Snapshot artifact '{artifactKind}' has an invalid rooted payload path '{snapshotRelativePath}'.");

        var normalizedPath = snapshotRelativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var segments = normalizedPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
            throw new InvalidOperationException($"Snapshot artifact '{artifactKind}' has an invalid payload path '{snapshotRelativePath}'.");

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void CopyFile(
        string sourcePath,
        string destinationPath,
        UnixModeCopyStrategy unixModeStrategy = UnixModeCopyStrategy.PreserveSource)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var stagedPath = CreateSiblingFilePath(destinationPath, "staged");
        try
        {
            File.Copy(sourcePath, stagedPath, overwrite: true);
            ApplyUnixMode(sourcePath, stagedPath, isDirectory: false, unixModeStrategy);
            File.Move(stagedPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(stagedPath))
                File.Delete(stagedPath);
        }
    }

    private static void CopyDirectory(
        string sourcePath,
        string destinationPath,
        UnixModeCopyStrategy unixModeStrategy = UnixModeCopyStrategy.PreserveSource)
    {
        Directory.CreateDirectory(destinationPath);
        ApplyUnixMode(sourcePath, destinationPath, isDirectory: true, unixModeStrategy);

        foreach (var directory in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            var destinationDirectory = Path.Combine(destinationPath, Path.GetRelativePath(sourcePath, directory));
            Directory.CreateDirectory(destinationDirectory);
            ApplyUnixMode(directory, destinationDirectory, isDirectory: true, unixModeStrategy);
        }

        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, file);
            CopyFile(file, Path.Combine(destinationPath, relative), unixModeStrategy);
        }
    }

    private static void ReplaceDirectory(string sourcePath, string destinationPath)
    {
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var destinationParent = Path.GetDirectoryName(fullDestinationPath);
        if (!string.IsNullOrWhiteSpace(destinationParent))
            Directory.CreateDirectory(destinationParent);

        var stagedPath = CreateSiblingDirectoryPath(fullDestinationPath, "staged");
        var backupPath = CreateSiblingDirectoryPath(fullDestinationPath, "backup");
        var backupCreated = false;
        var replaced = false;

        try
        {
            CopyDirectory(sourcePath, stagedPath, UnixModeCopyStrategy.PreserveSource);

            if (Directory.Exists(fullDestinationPath))
            {
                Directory.Move(fullDestinationPath, backupPath);
                backupCreated = true;
            }

            Directory.Move(stagedPath, fullDestinationPath);
            replaced = true;
        }
        catch
        {
            if (backupCreated && !Directory.Exists(fullDestinationPath) && Directory.Exists(backupPath))
                Directory.Move(backupPath, fullDestinationPath);

            throw;
        }
        finally
        {
            if (Directory.Exists(stagedPath))
                Directory.Delete(stagedPath, recursive: true);

            if (replaced && backupCreated && Directory.Exists(backupPath))
                Directory.Delete(backupPath, recursive: true);
        }
    }

    private static string CreateSiblingDirectoryPath(string path, string suffix)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent))
            parent = Directory.GetCurrentDirectory();

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
        return Path.Combine(parent, $"{name}.{suffix}.{Guid.NewGuid():N}");
    }

    private static string CreateSiblingFilePath(string path, string suffix)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent))
            parent = Directory.GetCurrentDirectory();

        return Path.Combine(parent, $"{Path.GetFileName(fullPath)}.{suffix}.{Guid.NewGuid():N}.tmp");
    }

    private static void ApplyUnixMode(
        string sourcePath,
        string destinationPath,
        bool isDirectory,
        UnixModeCopyStrategy unixModeStrategy)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            var sourceMode = File.GetUnixFileMode(sourcePath);
            var destinationMode = unixModeStrategy switch
            {
                UnixModeCopyStrategy.OwnerOnlyPreserveExecute => isDirectory
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    : BuildRestrictedFileMode(sourceMode),
                _ => sourceMode
            };
            File.SetUnixFileMode(destinationPath, destinationMode);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static UnixFileMode BuildRestrictedFileMode(UnixFileMode sourceMode)
        => UnixFileMode.UserRead |
           UnixFileMode.UserWrite |
           (HasAnyExecuteBit(sourceMode) ? UnixFileMode.UserExecute : 0);

    private static bool HasAnyExecuteBit(UnixFileMode mode)
        => (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    private static string? ResolveWorkspacePath(GatewayConfig config)
    {
        var workspace = config.Tooling.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(workspace))
            return null;

        var resolved = SecretResolver.Resolve(workspace) ?? workspace;
        return Path.IsPathRooted(resolved) ? resolved : Path.GetFullPath(resolved);
    }

    private static string? ResolveConfiguredPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var expanded = Environment.ExpandEnvironmentVariables(path);
        expanded = GatewaySetupPaths.ExpandPath(expanded);
        return Path.IsPathRooted(expanded) ? expanded : Path.GetFullPath(expanded);
    }

    private static int UnknownSubcommand(string subcommand, TextWriter output, TextWriter error)
    {
        error.WriteLine($"Unknown upgrade subcommand: {subcommand}");
        PrintHelp(output);
        return 2;
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine(
            """
            openclaw upgrade

            Usage:
              openclaw upgrade check [--config <path>] [--offline]
              openclaw upgrade rollback [--config <path>] [--offline] [--require-provider]

            Notes:
              - Runs preflight checks before an upgrade.
              - Combines setup verification, provider readiness, plugin compatibility,
                skill compatibility, and migration-risk heuristics into one report.
              - Captures a last-known-good config/env/deploy snapshot when preflight succeeds.
              - 'rollback' restores the saved snapshot and reruns setup verification.
              - Returns a non-zero exit code when blocking issues are found.
            """);
    }

    private static string GetCurrentVersion()
        => typeof(UpgradeCommands).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private sealed record UpgradeCheckSummary(
        string Label,
        string Status,
        string Summary,
        IReadOnlyList<string> Details,
        IReadOnlyList<string> NextActions);

    private sealed record RollbackCaptureItem(
        string Kind,
        string SourcePath,
        string TargetPath,
        bool IsDirectory,
        bool Exists,
        string? SnapshotRelativePath);

    private sealed record ValidatedRestoreArtifact(
        string Kind,
        string TargetPath,
        bool IsDirectory,
        bool Exists,
        string? SnapshotRelativePath);

    private enum UnixModeCopyStrategy
    {
        PreserveSource,
        OwnerOnlyPreserveExecute
    }
}
