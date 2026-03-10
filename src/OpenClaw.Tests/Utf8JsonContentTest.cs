using System.Text;
using Xunit;

namespace OpenClaw.Tests
{
    public sealed class Utf8JsonContentTest
    {
        [Fact]
        public async Task CopyToAsyncTest()
        {
            var jsonString = """
                {"key":"value"}
                """;
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonString))
            {
                Position = 0L
            };

            using var jsonContent = new Agent.Tools.Utf8JsonContent(stream);
            var actualStream = new MemoryStream();
            await jsonContent.CopyToAsync(actualStream);
            var actual = Encoding.UTF8.GetString(actualStream.ToArray());

            Assert.Equal(jsonString, actual);
        }
    }
}
