using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.Net.Http.Client
{
    public class ConnectionGroupStream : BufferedReadStream
    {
        private HttpConnection _httpConnection;

        public ConnectionGroupStream(HttpConnection httpConnection) : base(httpConnection.Transport)
        {
            _httpConnection = httpConnection;
        }

        protected override void Dispose(bool disposing)
        {
            _httpConnection.ReleaseConnection();
        }
    }
}
