namespace OpenClaw.Core.Models;

public enum MediaMarkerKind : byte
{
    ImageUrl,
    ImagePath,
    FileUrl,
    FilePath,
    TelegramImageFileId,
    TelegramVideoFileId,
    TelegramAudioFileId,
    TelegramDocumentFileId,
    TelegramStickerFileId,
    VideoUrl,
    AudioUrl,
    DocumentUrl,
    StickerUrl
}

public sealed record MediaMarker(MediaMarkerKind Kind, string Value);

public static class MediaMarkerProtocol
{
    public static (List<MediaMarker> Markers, string RemainingText) Extract(string text)
    {
        if (string.IsNullOrEmpty(text))
            return ([], "");

        var markers = new List<MediaMarker>();
        var remainingLines = new List<string>();

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (TryParseMarker(trimmed, out var marker))
            {
                markers.Add(marker);
                continue;
            }

            remainingLines.Add(line);
        }

        var remaining = string.Join("\n", remainingLines).Trim();
        return (markers, remaining);
    }

    public static bool TryParseMarker(string line, out MediaMarker marker)
    {
        marker = default!;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        if (TryParseBracketValue(line, "IMAGE_URL:", out var imageUrl))
        {
            marker = new MediaMarker(MediaMarkerKind.ImageUrl, imageUrl);
            return true;
        }

        if (TryParseBracketValue(line, "IMAGE_PATH:", out var imagePath))
        {
            marker = new MediaMarker(MediaMarkerKind.ImagePath, imagePath);
            return true;
        }

        if (TryParseBracketValue(line, "FILE_URL:", out var fileUrl))
        {
            marker = new MediaMarker(MediaMarkerKind.FileUrl, fileUrl);
            return true;
        }

        if (TryParseBracketValue(line, "FILE_PATH:", out var filePath))
        {
            marker = new MediaMarker(MediaMarkerKind.FilePath, filePath);
            return true;
        }

        if (TryParseBracketValue(line, "VIDEO_URL:", out var videoUrl))
        {
            marker = new MediaMarker(MediaMarkerKind.VideoUrl, videoUrl);
            return true;
        }

        if (TryParseBracketValue(line, "AUDIO_URL:", out var audioUrl))
        {
            marker = new MediaMarker(MediaMarkerKind.AudioUrl, audioUrl);
            return true;
        }

        if (TryParseBracketValue(line, "DOCUMENT_URL:", out var documentUrl))
        {
            marker = new MediaMarker(MediaMarkerKind.DocumentUrl, documentUrl);
            return true;
        }

        if (TryParseBracketValue(line, "STICKER_URL:", out var stickerUrl))
        {
            marker = new MediaMarker(MediaMarkerKind.StickerUrl, stickerUrl);
            return true;
        }

        if (TryParseTelegramFileId(line, "IMAGE", out var imageFileId))
        {
            marker = new MediaMarker(MediaMarkerKind.TelegramImageFileId, imageFileId);
            return true;
        }

        if (TryParseTelegramFileId(line, "VIDEO", out var videoFileId))
        {
            marker = new MediaMarker(MediaMarkerKind.TelegramVideoFileId, videoFileId);
            return true;
        }

        if (TryParseTelegramFileId(line, "AUDIO", out var audioFileId))
        {
            marker = new MediaMarker(MediaMarkerKind.TelegramAudioFileId, audioFileId);
            return true;
        }

        if (TryParseTelegramFileId(line, "DOCUMENT", out var documentFileId))
        {
            marker = new MediaMarker(MediaMarkerKind.TelegramDocumentFileId, documentFileId);
            return true;
        }

        if (TryParseTelegramFileId(line, "STICKER", out var stickerFileId))
        {
            marker = new MediaMarker(MediaMarkerKind.TelegramStickerFileId, stickerFileId);
            return true;
        }

        return false;
    }

    private static bool TryParseBracketValue(string line, string prefix, out string value)
    {
        value = "";
        if (line.Length < prefix.Length + 2)
            return false;

        if (!line.StartsWith('[') || !line.EndsWith(']'))
            return false;

        var inner = line[1..^1];
        if (!inner.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        value = inner[prefix.Length..].Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryParseTelegramFileId(string line, string mediaType, out string value)
    {
        value = "";
        var prefix = $"[{mediaType}:telegram:file_id=";
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith(']'))
            return false;

        value = line.Substring(prefix.Length, line.Length - prefix.Length - 1).Trim();
        return !string.IsNullOrWhiteSpace(value);
    }
}
