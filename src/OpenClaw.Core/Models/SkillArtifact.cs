using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Core.Models;

/// <summary>
/// Unified artifact model delivered to the client via WebSocket (envelope type = "artifact").
/// Covers both file-based artifacts (kind = "file") and structured JSON data artifacts
/// (kind = "data") such as stage checkpoints, analysis results, or progress payloads.
/// </summary>
public sealed record SkillArtifact
{
    /// <summary>Discriminator: "file" or "data".</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Semantic artifact type. Well-known values: "template_package", "skill_package",
    /// "ontology", "analysis", "plan", "progress", "generic".
    /// </summary>
    public string ArtifactType { get; init; } = "generic";

    /// <summary>Human-readable label shown in the UI.</summary>
    public string? Label { get; init; }

    /// <summary>Skill that produced this artifact (optional).</summary>
    public string? SkillName { get; init; }

    /// <summary>Stage identifier within a multi-stage skill workflow (optional).</summary>
    public string? Stage { get; init; }

    /// <summary>True when this artifact marks the current stage as complete.</summary>
    public bool IsTerminal { get; init; }

    // ── File artifact fields (kind = "file") ──────────────────────────────

    /// <summary>URL of the stored file (e.g. /media/{id}). Present when kind = "file".</summary>
    public string? FileUrl { get; init; }

    /// <summary>Filename shown to the user. Present when kind = "file".</summary>
    public string? FileName { get; init; }

    /// <summary>MIME type of the file. Present when kind = "file".</summary>
    public string? MimeType { get; init; }

    /// <summary>File size in bytes. Present when kind = "file".</summary>
    public long? FileSizeBytes { get; init; }

    // ── Data artifact fields (kind = "data") ──────────────────────────────

    /// <summary>
    /// Structured JSON payload. Present when kind = "data".
    /// Omitted from serialization when null to keep the envelope compact.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; init; }

    /// <summary>
    /// Rendering hint for data artifacts.
    /// Values: "tree" | "table" | "code" | "badge" | "progress" | "text".
    /// </summary>
    public string? DisplayHint { get; init; }
}
