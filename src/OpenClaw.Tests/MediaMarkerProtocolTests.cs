using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MediaMarkerProtocolTests
{
    [Fact]
    public void Extract_ParsesMarkers_And_RemovesLines()
    {
        var text = """
            [IMAGE_URL:https://example.com/a.png]
            hello
            [FILE_URL:https://example.com/a.pdf]
            """;

        var (markers, remaining) = MediaMarkerProtocol.Extract(text);
        Assert.Equal(2, markers.Count);
        Assert.Equal(MediaMarkerKind.ImageUrl, markers[0].Kind);
        Assert.Equal("https://example.com/a.png", markers[0].Value);
        Assert.Equal(MediaMarkerKind.FileUrl, markers[1].Kind);
        Assert.Equal("https://example.com/a.pdf", markers[1].Value);
        Assert.Equal("hello", remaining);
    }

    [Fact]
    public void TryParseMarker_ParsesTelegramFileId()
    {
        Assert.True(MediaMarkerProtocol.TryParseMarker("[IMAGE:telegram:file_id=abc123]", out var marker));
        Assert.Equal(MediaMarkerKind.TelegramImageFileId, marker.Kind);
        Assert.Equal("abc123", marker.Value);
    }

    [Theory]
    [InlineData("[VIDEO:telegram:file_id=vid123]", MediaMarkerKind.TelegramVideoFileId, "vid123")]
    [InlineData("[AUDIO:telegram:file_id=aud123]", MediaMarkerKind.TelegramAudioFileId, "aud123")]
    [InlineData("[DOCUMENT:telegram:file_id=doc123]", MediaMarkerKind.TelegramDocumentFileId, "doc123")]
    [InlineData("[STICKER:telegram:file_id=stk123]", MediaMarkerKind.TelegramStickerFileId, "stk123")]
    public void TryParseMarker_ParsesTelegramMediaFileIds(string input, MediaMarkerKind expectedKind, string expectedValue)
    {
        Assert.True(MediaMarkerProtocol.TryParseMarker(input, out var marker));
        Assert.Equal(expectedKind, marker.Kind);
        Assert.Equal(expectedValue, marker.Value);
    }
}
