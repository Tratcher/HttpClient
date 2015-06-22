using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Net.Http.Client
{
    public class ManagedHandler : HttpMessageHandler
    {
        // TODO: Idle group cleanup
        private ConcurrentDictionary<ConnectionGroup.Key, ConnectionGroup> _connectionGroups
            = new ConcurrentDictionary<ConnectionGroup.Key, ConnectionGroup>();

        public ManagedHandler()
        {
        }

        public Uri ProxyAddress
        {
            // TODO: Validate that only an absolute http address is specified. Path, query, and fragment are ignored
            get; set;
        }

        public int MaxAutomaticRedirects { get; set; } = 20;

        public RedirectMode RedirectMode { get; set; } = RedirectMode.NoDowngrade;

        public int MaxConnectionsPerEndpoint { get; set; } = 8;

        protected async override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            HttpResponseMessage response = null;
            int redirectCount = 0;
            bool retry;

            do
            {
                retry = false;
                response = await ProcessRequestAsync(request, cancellationToken);
                if (redirectCount < MaxAutomaticRedirects && IsAllowedRedirectResponse(request, response))
                {
                    redirectCount++;
                    retry = true;
                }

            } while (retry);

            return response;
        }

        private bool IsAllowedRedirectResponse(HttpRequestMessage request, HttpResponseMessage response)
        {
            // Are redirects enabled?
            if (RedirectMode == RedirectMode.None)
            {
                return false;
            }

            // Status codes 301 and 302
            if (response.StatusCode != HttpStatusCode.Redirect && response.StatusCode != HttpStatusCode.Moved)
            {
                return false;
            }

            Uri location = response.Headers.Location;

            if (location == null)
            {
                return false;
            }

            if (!location.IsAbsoluteUri)
            {
                request.RequestUri = location;
                request.SetPathAndQueryProperty(null);
                request.SetAddressLineProperty(null);
                request.Headers.Authorization = null;
                return true;
            }

            // Check if redirect from https to http is allowed
            if (request.IsHttps() && string.Equals("http", location.Scheme, StringComparison.OrdinalIgnoreCase)
                && RedirectMode == RedirectMode.NoDowngrade)
            {
                return false;
            }

            // Reset fields calculated from the URI.
            request.RequestUri = location;
            request.SetSchemeProperty(null);
            request.Headers.Host = null;
            request.Headers.Authorization = null;
            request.SetHostProperty(null);
            request.SetConnectionHostProperty(null);
            request.SetPortProperty(null);
            request.SetConnectionPortProperty(null);
            request.SetPathAndQueryProperty(null);
            request.SetAddressLineProperty(null);
            return true;
        }

        private async Task<HttpResponseMessage> ProcessRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ProcessUrl(request);
            ProcessHostHeader(request);

            if (request.Method != HttpMethod.Get)
            {
                throw new NotImplementedException(request.Method.Method); // TODO: POST
            }

            var proxyMode = DetermineProxyModeAndAddressLine(request);

            var connectionGroup = _connectionGroups.GetOrAdd(ConnectionGroup.CreateKey(request), key =>
            {
                // TODO: MaxValue connection limit  for localhost/loopback IP
                return new ConnectionGroup(key, proxyMode, MaxConnectionsPerEndpoint);
            });

            // TODO: If GetConnectionAsync or SendAsync fail before returning response headers, try again?
            var connection = await connectionGroup.GetConnectionAsync(request, cancellationToken);

            return await connection.SendAsync(request, cancellationToken);
        }

        // Data comes from either the request.RequestUri or from the request.Properties
        private void ProcessUrl(HttpRequestMessage request)
        {
            string scheme = request.GetSchemeProperty();
            if (string.IsNullOrWhiteSpace(scheme))
            {
                if (!request.RequestUri.IsAbsoluteUri)
                {
                    throw new InvalidOperationException("Missing URL Scheme");
                }
                scheme = request.RequestUri.Scheme;
                request.SetSchemeProperty(scheme);
            }
            if (!(request.IsHttp() || request.IsHttps()))
            {
                throw new InvalidOperationException("Only HTTP or HTTPS are supported, not: " + request.RequestUri.Scheme);
            }

            string host = request.GetHostProperty();
            if (string.IsNullOrWhiteSpace(host))
            {
                if (!request.RequestUri.IsAbsoluteUri)
                {
                    throw new InvalidOperationException("Missing URL Scheme");
                }
                host = request.RequestUri.DnsSafeHost;
                request.SetHostProperty(host);
            }
            string connectionHost = request.GetConnectionHostProperty();
            if (string.IsNullOrWhiteSpace(connectionHost))
            {
                request.SetConnectionHostProperty(host);
            }

            int? port = request.GetPortProperty();
            if (!port.HasValue)
            {
                if (!request.RequestUri.IsAbsoluteUri)
                {
                    throw new InvalidOperationException("Missing URL Scheme");
                }
                port = request.RequestUri.Port;
                request.SetPortProperty(port);
            }
            int? connectionPort = request.GetConnectionPortProperty();
            if (!connectionPort.HasValue)
            {
                request.SetConnectionPortProperty(port);
            }

            string pathAndQuery = request.GetPathAndQueryProperty();
            if (string.IsNullOrWhiteSpace(pathAndQuery))
            {
                if (request.RequestUri.IsAbsoluteUri)
                {
                    pathAndQuery = request.RequestUri.PathAndQuery;
                }
                else
                {
                    pathAndQuery = Uri.EscapeUriString(request.RequestUri.ToString());
                }
                request.SetPathAndQueryProperty(pathAndQuery);
            }
        }

        private void ProcessHostHeader(HttpRequestMessage request)
        {
            if (string.IsNullOrWhiteSpace(request.Headers.Host))
            {
                string host = request.GetHostProperty();
                int port = request.GetPortProperty().Value;
                if (host.Contains(':'))
                {
                    // IPv6
                    host = '[' + host + ']';
                }

                request.Headers.Host = host + ":" + port.ToString(CultureInfo.InvariantCulture);
            }
        }

        private ProxyMode DetermineProxyModeAndAddressLine(HttpRequestMessage request)
        {
            string scheme = request.GetSchemeProperty();
            string host = request.GetHostProperty();
            int? port = request.GetPortProperty();
            string pathAndQuery = request.GetPathAndQueryProperty();
            string addressLine = request.GetAddressLineProperty();

            if (string.IsNullOrEmpty(addressLine))
            {
                request.SetAddressLineProperty(pathAndQuery);
            }

            if (ProxyAddress == null)
            {
                return ProxyMode.None;
            }
            if (request.IsHttp())
            {
                if (string.IsNullOrEmpty(addressLine))
                {
                    addressLine = scheme + "://" + host + ":" + port.Value + pathAndQuery;
                    request.SetAddressLineProperty(addressLine);
                }
                request.SetConnectionHostProperty(ProxyAddress.DnsSafeHost);
                request.SetConnectionPortProperty(ProxyAddress.Port);
                return ProxyMode.Http;
            }
            // Tunneling generates a completely separate request, don't alter the original, just the connection address.
            request.SetConnectionHostProperty(ProxyAddress.DnsSafeHost);
            request.SetConnectionPortProperty(ProxyAddress.Port);
            return ProxyMode.Tunnel;
        }
    }
}
