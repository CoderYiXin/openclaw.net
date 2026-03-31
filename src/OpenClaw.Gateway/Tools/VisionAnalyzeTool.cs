using System.Text.Json;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Gateway.Tools;

internal sealed class VisionAnalyzeTool : ITool
{
    private readonly GeminiMultimodalService _gemini;

    public VisionAnalyzeTool(GeminiMultimodalService gemini)
    {
        _gemini = gemini;
    }

    public string Name => "vision_analyze";
    public string Description => "Analyze an image using the native Gemini multimodal path.";
    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "prompt":{"type":"string"},
        "image_path":{"type":"string"},
        "image_url":{"type":"string"},
        "mime_type":{"type":"string"},
        "model":{"type":"string"}
      },
      "required":["prompt"]
    }
    """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;
        var prompt = GetString(root, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return "Error: prompt is required.";

        return await _gemini.AnalyzeVisionAsync(
            prompt,
            GetString(root, "image_path"),
            GetString(root, "image_url"),
            GetString(root, "mime_type"),
            GetString(root, "model"),
            ct);
    }

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
