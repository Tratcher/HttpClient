using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Net.Http.Client
{
    public class HttpConnectionResponseContent : HttpContent
    {
        private readonly HttpConnection _connection;
        private Stream _responseStream;

        public HttpConnectionResponseContent(HttpConnection connection)
        {
            _connection = connection;
        }

        internal void SetResponseStream(Stream responseStream)
        {
            if (_responseStream != null)
            {
                throw new InvalidOperationException("Called multiple times");
            }
            _responseStream = responseStream;
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext context)
        {
            return _responseStream.CopyToAsync(stream);
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult(_responseStream);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _responseStream.Dispose();
            }
        }
    }
}
