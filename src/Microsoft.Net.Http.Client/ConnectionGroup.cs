using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace Microsoft.Net.Http.Client
{
    public class ConnectionGroup
    {
        private bool _isHttps;
        private string _host;
        private int _port;
        private ProxyMode _proxyMode;
        private int _maxConnections;
        private SemaphoreSlim _maxConnectionCount;
        // Use a stack to favor recently used connections. If we have more connections than we need let some go idle and get cleaned up.
        // TODO: Idle connection cleanup.
        private ConcurrentStack<HttpConnection> _availableConnections;

        public ConnectionGroup(Key key, ProxyMode proxyMode, int maxConnections)
        {
            _isHttps = key.IsHttps;
            _host = key.Host;
            _port = key.Port;
            _proxyMode = proxyMode;
            _maxConnections = maxConnections;
            _maxConnectionCount = new SemaphoreSlim(_maxConnections, _maxConnections);
            _availableConnections = new ConcurrentStack<HttpConnection>();
        }

        public class Key
        {
            public bool IsHttps { get; set; }

            public string Host { get; set; }

            public int Port { get; set; }

            public override bool Equals(object obj)
            {
                var otherKey = obj as Key;
                if (otherKey == null)
                {
                    return false;
                }

                return IsHttps == otherKey.IsHttps
                    && Host.Equals(otherKey.Host, StringComparison.OrdinalIgnoreCase)
                    && Port == otherKey.Port;
            }

            public override int GetHashCode()
            {
                return IsHttps.GetHashCode() ^ Host.GetHashCode() ^ Port.GetHashCode();
            }

            public override string ToString()
            {
                return (IsHttps ? "https//" : "http//")
                    + Host + ":" + Port.ToString(CultureInfo.InvariantCulture);
            }
        }

        public static Key CreateKey(HttpRequestMessage request)
        {
            return new Key()
            {
                IsHttps = request.IsHttps(),
                Host = request.GetHostProperty(),
                Port = request.GetPortProperty().Value
            };
        }

        internal async Task<HttpConnection> GetConnectionAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await _maxConnectionCount.WaitAsync(cancellationToken);

            HttpConnection connection;
            if (_availableConnections.TryPop(out connection))
            {
                // TODO: Detect and clean up idle connections
                return connection;
            }

            var transport = await ConnectAsync(cancellationToken);

            if (_proxyMode == ProxyMode.Tunnel)
            {
                await TunnelThroughProxyAsync(request, transport, cancellationToken);
            }

            System.Diagnostics.Debug.Assert(!(_proxyMode == ProxyMode.Http && request.IsHttps()));

            if (_isHttps)
            {
                var sslStream = new SslStream(transport);
                await sslStream.AuthenticateAsClientAsync(request.GetHostProperty());
                transport = new ApmStreamWrapper(sslStream);
            }

            var bufferedReadStream = new BufferedReadStream(transport);
            return new HttpConnection(bufferedReadStream, this);
        }

        private async Task<ApmStream> ConnectAsync(CancellationToken cancellationToken)
        {
            var client = new TcpClient();
            try
            {
                // TOOD: Cancellation
                // TODO: If this host resolves to a list of IPs, try them individually and save which one works.
                // IPv6 addresses are usually listed first and rarely respond, resulting in 5s timeouts per IP until
                // you get to the right address.
                // TODO: Round robin DNS.
                await client.ConnectAsync(_host, _port);
                return new ApmStreamWrapper(client.GetStream());
            }
            catch (SocketException sox)
            {
                ((IDisposable)client).Dispose();
                throw new HttpRequestException("Request failed", sox);
            }
        }

        private async Task TunnelThroughProxyAsync(HttpRequestMessage request, ApmStream transport, CancellationToken cancellationToken)
        {
            // Send a Connect request:
            // CONNECT server.example.com:80 HTTP / 1.1
            // Host: server.example.com:80
            var connectReqeuest = new HttpRequestMessage();
            connectReqeuest.Headers.ProxyAuthorization = request.Headers.ProxyAuthorization;
            connectReqeuest.Method = new HttpMethod("CONNECT");
            // TODO: IPv6 hosts
            var authority = request.GetHostProperty() + ":" + request.GetPortProperty().Value;
            connectReqeuest.SetAddressLineProperty(authority);
            connectReqeuest.Headers.Host = authority;

            var connection = new HttpConnection(new BufferedReadStream(transport), this);
            HttpResponseMessage connectResponse;
            try
            {
                connectResponse = await connection.SendAsync(connectReqeuest, cancellationToken);
                // TODO:? await connectResponse.Content.LoadIntoBufferAsync(); // Drain any body
                // There's no danger of accidentally consuming real response data because the real request hasn't been sent yet.
            }
            catch (Exception ex)
            {
                transport.Dispose();
                throw new HttpRequestException("SSL Tunnel failed to initialize", ex);
            }

            // Listen for a response. Any 2XX is considered success, anything else is considered a failure.
            if ((int)connectResponse.StatusCode < 200 || 300 <= (int)connectResponse.StatusCode)
            {
                transport.Dispose();
                throw new HttpRequestException("Failed to negotiate the proxy tunnel: " + connectResponse.ToString());
            }
        }

        internal void RemoveConnection()
        {
            _maxConnectionCount.Release();
        }

        internal void ReturnConnection(HttpConnection connection)
        {
            // TODO: queue a background read to detect connection drops
            _availableConnections.Push(connection);
            _maxConnectionCount.Release();
        }
    }
}
