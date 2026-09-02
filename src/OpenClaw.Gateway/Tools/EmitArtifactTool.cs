using System.Text.Json;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway.Tools;

/// <summary>
/// Unified artifact emitter. Handles two cases selected by the <c>kind</c> parameter:
///
/// <list type="bullet">
///   <item><term>kind = "file"</term>
///     <description>Read a file from the filesystem, store it in the media cache, and push
///     a WebSocket <c>artifact</c> envelope so the client receives it immediately (mid-turn)
///     as a named downloadable attachment.</description></item>
///   <item><term>kind = "data"</term>
///     <description>Emit a structured JSON payload (stage checkpoint, analysis result, progress
///     update, etc.) as a WebSocket <c>artifact</c> envelope with a display hint so the frontend
///     can render it using the appropriate generic component (table, tree, code, badge, …).</description></item>
/// </list>
///
/// Both kinds carry the same <see cref="SkillArtifact"/> structure so the client can handle
/// all artifacts through a single unified path.
/// </summary>
internal sealed class EmitArtifactTool : IToolWithContext
{
    private const long MaxArtifactFileBytes = 64L * 1024 * 1024;

    private readonly MediaCacheStore _mediaCache;
    private readonly WebSocketChannel _wsChannel;
    private readonly GatewayConfig _config;
    private readonly SkillArtifactRuntime _artifactRuntime;

    public EmitArtifactTool(MediaCacheStore mediaCache, WebSocketChannel wsChannel, GatewayConfig config, SkillArtifactRuntime artifactRuntime)
    {
        _mediaCache = mediaCache;
        _wsChannel = wsChannel;
        _config = config;
        _artifactRuntime = artifactRuntime;
    }

    public string Name => "emit_artifact";

    public string Description =>
        "Emit a typed artifact to the user immediately (mid-turn). " +
        "Use kind=\"file\" to publish a file (template package, skill bundle, export) as a downloadable attachment. " +
        "Use kind=\"data\" to push a structured JSON payload (analysis result, plan, progress, stage checkpoint) " +
        "that the frontend renders using a generic display component (table, tree, code block, badge, etc.). " +
        "Both kinds are delivered instantly via WebSocket without waiting for the turn to complete.";

    public string ParameterSchema => """
    {
      "type": "object",
      "properties": {
        "kind": {
          "type": "string",
          "enum": ["file", "data"],
          "default": "file",
          "description": "Artifact kind. 'file': publish a filesystem file as a downloadable attachment. 'data': push a structured JSON payload as a stage checkpoint or inline display."
        },
        "artifact_type": {
          "type": "string",
          "description": "Semantic type. Well-known: template_package | skill_package | ontology | analysis | plan | progress | generic. Defaults to 'template_package' for file, 'generic' for data."
        },
        "label": {
          "type": "string",
          "description": "Human-readable title shown in the UI"
        },
        "skill_name": {
          "type": "string",
          "description": "Name of the skill emitting this artifact (optional)"
        },
        "stage": {
          "type": "string",
          "description": "Stage identifier in a multi-stage workflow (optional)"
        },
        "terminal": {
          "type": "boolean",
          "default": false,
          "description": "If true, marks the current stage as complete"
        },
        "path": {
          "type": "string",
          "description": "(kind=file) Absolute path of the file to publish — must already exist on the filesystem"
        },
        "display_name": {
          "type": "string",
          "description": "(kind=file) Override the filename shown to the user (defaults to the file's basename)"
        },
        "data": {
          "description": "(kind=data) Structured JSON payload — any object or array"
        },
        "display_hint": {
          "type": "string",
          "enum": ["tree", "table", "code", "badge", "progress", "text"],
          "description": "(kind=data) Rendering hint for the frontend component"
        }
      }
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: emit_artifact requires an execution context (session info).");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var kind = root.TryGetProperty("kind", out var kindEl) ? (kindEl.GetString() ?? "file") : "file";
        var label = root.TryGetProperty("label", out var labelEl) ? labelEl.GetString() : null;
        var skillName = root.TryGetProperty("skill_name", out var snEl) ? snEl.GetString() : null;
        var stage = root.TryGetProperty("stage", out var stageEl) ? stageEl.GetString() : null;
        var isTerminal = root.TryGetProperty("terminal", out var termEl) && termEl.ValueKind == JsonValueKind.True;

        return kind == "data"
            ? await ExecuteDataAsync(root, label, skillName, stage, isTerminal, context, ct)
            : await ExecuteFileAsync(root, label, skillName, stage, isTerminal, context, ct);
    }

    // ── kind = "file" ────────────────────────────────────────────────────────

    private async ValueTask<string> ExecuteFileAsync(
        JsonElement root, string? label, string? skillName, string? stage, bool isTerminal,
        ToolExecutionContext context, CancellationToken ct)
    {
        var path = root.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(path))
            return "Error: 'path' is required for kind=\"file\".";

        var artifactType = root.TryGetProperty("artifact_type", out var typeEl)
            ? (typeEl.GetString() ?? "template_package")
            : "template_package";

        var displayName = root.TryGetProperty("display_name", out var nameEl)
            ? nameEl.GetString()
            : null;

        var resolvedPath = ToolPathPolicy.ResolveRealPath(path);
        if (!ToolPathPolicy.IsReadAllowed(_config.Tooling, resolvedPath))
            return $"Error: Read access denied for path: {path}";
        if (!File.Exists(resolvedPath))
            return $"Error: File not found: {path}";

        var publishPath = ToolPathPolicy.IsWriteAllowed(_config.Tooling, resolvedPath)
            ? resolvedPath
            : await CopyToDownloadsFolderAsync(resolvedPath, ct);
        if (publishPath is null)
            return "Error: Could not copy artifact to a publishable location. Ensure WorkspaceRoot or an AllowedWriteRoot is configured.";

        var fileInfo = new FileInfo(publishPath);
        if (fileInfo.Length > MaxArtifactFileBytes)
            return $"Error: Artifact file is too large ({FormatSize(fileInfo.Length)}). Maximum allowed size is {FormatSize(MaxArtifactFileBytes)}.";

        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(publishPath, ct); }
        catch (Exception ex) { return $"Error: Could not read file: {ex.Message}"; }

        var fileName = !string.IsNullOrWhiteSpace(displayName) ? displayName : Path.GetFileName(publishPath);
        var mimeType = GuessMimeType(Path.GetExtension(publishPath));

        StoredMediaAsset asset;
        try { asset = await _mediaCache.SaveAsync(bytes.AsMemory(), mimeType, fileName, ct); }
        catch (Exception ex) { return $"Error: Could not save artifact to media store: {ex.Message}"; }

        var fileUrl = $"/media/{asset.Id}";
        var effectiveLabel = !string.IsNullOrWhiteSpace(label) ? label : fileName;

        var fileResult = _artifactRuntime.NormalizeAndRecord(context.Session.Id, new SkillArtifact
        {
            Kind = "file",
            ArtifactType = artifactType,
            Label = effectiveLabel,
            SkillName = skillName,
            Stage = stage,
            IsTerminal = isTerminal,
            FileUrl = fileUrl,
            FileName = fileName,
            MimeType = mimeType,
            FileSizeBytes = asset.SizeBytes,
        });
        if (!fileResult.Succeeded || fileResult.Artifact is null)
            return $"Error: {fileResult.Error}";

        if (string.Equals(context.Session.ChannelId, "websocket", StringComparison.Ordinal) &&
            _wsChannel.IsClientUsingEnvelopes(context.Session.SenderId))
        {
            await _wsChannel.SendEnvelopeAsync(context.Session.SenderId, new WsServerEnvelope
            {
                Type = "artifact",
                Text = fileResult.Artifact.Label ?? fileResult.Artifact.ArtifactType,
                Artifact = fileResult.Artifact,
            }, ct);

            if (fileResult.StageGate is not null)
            {
                await _wsChannel.SendEnvelopeAsync(context.Session.SenderId, new WsServerEnvelope
                {
                    Type = "skill_stage_gate",
                    Text = fileResult.StageGate.CanProceed ? fileResult.StageGate.NextStage : fileResult.StageGate.BlockedReason,
                    StageGate = fileResult.StageGate,
                }, ct);
            }
        }

        var sizeLabel = FormatSize(asset.SizeBytes);
        return $"Artifact published: {fileName} ({sizeLabel}) [kind=file type={fileResult.Artifact.ArtifactType}]\n[FILE_URL:{fileUrl}]";
    }

    // ── kind = "data" ────────────────────────────────────────────────────────

    private async ValueTask<string> ExecuteDataAsync(
        JsonElement root, string? label, string? skillName, string? stage, bool isTerminal,
        ToolExecutionContext context, CancellationToken ct)
    {
        if (!root.TryGetProperty("data", out var dataEl))
            return "Error: 'data' is required for kind=\"data\".";

        var artifactType = root.TryGetProperty("artifact_type", out var typeEl)
            ? (typeEl.GetString() ?? "generic")
            : "generic";

        var displayHint = root.TryGetProperty("display_hint", out var hintEl)
            ? hintEl.GetString()
            : null;

        var dataResult = _artifactRuntime.NormalizeAndRecord(context.Session.Id, new SkillArtifact
        {
            Kind = "data",
            ArtifactType = artifactType,
            Label = label,
            SkillName = skillName,
            Stage = stage,
            IsTerminal = isTerminal,
            Data = dataEl.Clone(),
            DisplayHint = displayHint,
        });
        if (!dataResult.Succeeded || dataResult.Artifact is null)
            return $"Error: {dataResult.Error}";

        if (string.Equals(context.Session.ChannelId, "websocket", StringComparison.Ordinal) &&
            _wsChannel.IsClientUsingEnvelopes(context.Session.SenderId))
        {
            await _wsChannel.SendEnvelopeAsync(context.Session.SenderId, new WsServerEnvelope
            {
                Type = "artifact",
                Text = dataResult.Artifact.Label ?? dataResult.Artifact.ArtifactType,
                Artifact = dataResult.Artifact,
            }, ct);

            if (dataResult.StageGate is not null)
            {
                await _wsChannel.SendEnvelopeAsync(context.Session.SenderId, new WsServerEnvelope
                {
                    Type = "skill_stage_gate",
                    Text = dataResult.StageGate.CanProceed ? dataResult.StageGate.NextStage : dataResult.StageGate.BlockedReason,
                    StageGate = dataResult.StageGate,
                }, ct);
            }
        }

        return $"Data artifact emitted: [kind=data type={dataResult.Artifact.ArtifactType} stage={dataResult.Artifact.Stage ?? "-"} terminal={dataResult.Artifact.IsTerminal}]";
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<string?> CopyToDownloadsFolderAsync(string sourcePath, CancellationToken ct)
    {
        var downloadsDir = ResolveDownloadsDirectory();
        if (downloadsDir is null)
            return null;

        try
        {
            Directory.CreateDirectory(downloadsDir);

            var fileName = Path.GetFileName(sourcePath);
            var destPath = Path.Combine(downloadsDir, fileName);

            if (File.Exists(destPath) &&
                !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
            {
                var stem = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                destPath = Path.Combine(downloadsDir, $"{stem}_{Guid.NewGuid().ToString("N")[..8]}{ext}");
            }

            await using var src = File.OpenRead(sourcePath);
            await using var dest = File.Create(destPath);
            await src.CopyToAsync(dest, ct);

            return destPath;
        }
        catch
        {
            return null;
        }
    }

    private string? ResolveDownloadsDirectory()
    {
        var workspaceRaw = SecretResolver.Resolve(_config.Tooling.WorkspaceRoot);
        var workspaceBase = !string.IsNullOrWhiteSpace(workspaceRaw)
            ? workspaceRaw
            : Directory.GetCurrentDirectory();

        var downloadsDir = Path.Combine(Path.GetFullPath(workspaceBase), ".downloads");
        return ToolPathPolicy.IsWriteAllowed(_config.Tooling, downloadsDir) ? downloadsDir : null;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1_048_576 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1_048_576.0:F1} MB"
    };

    private static string GuessMimeType(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".zip" => "application/zip",
            ".tar" => "application/x-tar",
            ".gz" or ".tgz" => "application/gzip",
            ".json" => "application/json",
            ".md" => "text/markdown",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };
}
