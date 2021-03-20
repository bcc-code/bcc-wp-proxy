using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BCC.WPProxy
{
    public class WPMessageHandler : DelegatingHandler
    {
        public WPMessageHandler(IMemoryCache cache, WPProxySettings settings) : base(new SocketsHttpHandler()
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false
        })
        {
            Cache = cache;
            Settings = settings;
        }

        public IMemoryCache Cache { get; }
        public WPProxySettings Settings { get; }

        protected async override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Determine if content can/should be cached
            var canCache = request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri.IndexOf("wp-admin") == -1;

            // Determine cache key
            var requestKey = canCache ? request.RequestUri.ToString() : null;

            // Load from cache
            if (canCache)
            {
                var requestCache = Cache.Get<(HttpResponseMessage Response, string Content)>(requestKey);
                if (requestCache.Response != null)
                {
                    requestCache.Response.Content = new StringContent(requestCache.Content, Encoding.UTF8, requestCache.Response.Content.Headers.ContentType.MediaType);
                    return requestCache.Response;
                }

                if (request.Headers.Contains("Cookie"))
                {
                   // request.Headers.Remove("Cookie");
                }
            }

            // Modify request headers
            request.Headers.Add("X-USERNAME", "admin");
            request.Headers.Host = Settings.DestinationHost;

            // Modify request location
            //request.RequestUri = new Uri(request.RequestUri.ToString()
            //        .Replace(WebUtility.UrlEncode(Settings.DestinationAddress), WebUtility.UrlEncode(Settings.ProxyAddress)));
            


            // Execute request
            var response = await base.SendAsync(request, cancellationToken);
            var mediaType = response.Content?.Headers?.ContentType?.MediaType ?? "";
            var isText = mediaType.IndexOf("text") != -1 || mediaType.IndexOf("json") != -1 || mediaType.IndexOf("xml") != -1;
            if (isText)
            {
                // Read content and transform content (replace destination address with proxy address)
                var content = await response.Content.ReadAsStringAsync();
                content = content
                    .Replace(Settings.DestinationAddress, Settings.ProxyAddress)
                    .Replace(Settings.DestinationAddress.Replace("/","\\/"), Settings.ProxyAddress.Replace("/", "\\/"))
                    .Replace("http://", "https://");
                response.Content = new StringContent(content, Encoding.UTF8, response.Content.Headers.ContentType.MediaType);

                // Transform response headers
                if (response.Headers.Location != null)
                {
                    response.Headers.Location = new Uri(response.Headers.Location.ToString()
                        .Replace(Settings.DestinationAddress, Settings.ProxyAddress)
                        .Replace(WebUtility.UrlEncode(Settings.DestinationAddress), WebUtility.UrlEncode(Settings.ProxyAddress)));
                }

                // Cache content
                if (response.StatusCode == HttpStatusCode.OK && canCache)
                {
                    Cache.Set(requestKey, (response, content), TimeSpan.FromSeconds(60));
                }
            }
            return response;

        }
    }
}
