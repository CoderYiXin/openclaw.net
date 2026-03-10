using System.Net.Http.Headers;
using System.Text;

namespace OpenClaw.Agent.Tools
{
    sealed class Utf8JsonContent : StreamContent
    {
        private readonly long _length;
        private static readonly MediaTypeHeaderValue _uft8_contentType = new("application/json", Encoding.UTF8.WebName);

        public Utf8JsonContent(MemoryStream content)
            : base(content)
        {
            _length = content.Length;
            Headers.ContentType = _uft8_contentType;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
