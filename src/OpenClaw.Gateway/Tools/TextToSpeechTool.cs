using System.Text.Json;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Gateway.Tools;

internal sealed class TextToSpeechTool : ITool
{
    private readonly GeminiMultimodalService _gemini;

    public TextToSpeechTool(GeminiMultimodalService gemini)
    {
        _gemini = gemini;
    }

    public string Name => "text_to_speech";
    public string Description => "Generate speech audio from text using the native Gemini text-to-speech path.";
    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "text":{"type":"string"},
        "voice_name":{"type":"string"},
        "model":{"type":"string"}
      },
      "required":["text"]
    }
    """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;
        var text = GetString(root, "text");
        if (string.IsNullOrWhiteSpace(text))
            return "Error: text is required.";

        var (asset, marker) = await _gemini.SynthesizeSpeechAsync(
            text,
            GetString(root, "voice_name"),
            GetString(root, "model"),
            ct);

        return $"asset_id: {asset.Id}\nmedia_type: {asset.MediaType}\n{marker}";
    }

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
