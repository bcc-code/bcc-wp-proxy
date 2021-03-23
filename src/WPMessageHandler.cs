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
        public WPMessageHandler(CacheService cache, WPProxySettings settings) : base(new SocketsHttpHandler()
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

        public CacheService Cache { get; }
        public WPProxySettings Settings { get; }

        public class ResponseCacheItem
        {
            public string Content { get; set; }
            public string Url { get; set; }
            public int Status { get; set; }
            public string MediaType { get; set; }
            public Version Version { get; set; }

            public Dictionary<string,string[]> Headers { get; set; }

            public static ResponseCacheItem FromResponse(HttpResponseMessage response, string content)
            {
                var itm = new ResponseCacheItem
                {
                    Content = content,
                    Status = (int)response.StatusCode,
                    Headers = new Dictionary<string, string[]>(),
                    MediaType = response.Content.Headers.ContentType.MediaType,
                    Url = response.RequestMessage.RequestUri.ToString(),
                    Version = response.Version
                };
                foreach (var header in response.Headers)
                {
                    itm.Headers[header.Key] = header.Value.ToArray();
                }
                return itm;
            }

            public HttpResponseMessage ToResponseMessage()
            {
                var msg = new HttpResponseMessage
                {
                    Content = new StringContent(this.Content.ToString(), Encoding.UTF8, this.MediaType),
                    StatusCode = (HttpStatusCode)this.Status,
                    Version = this.Version
                };
                foreach (var header in this.Headers)
                {
                    msg.Headers.Add(header.Key, header.Value);
                }
                return msg;
            }
        }

        protected async override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Determine if content can/should be cached
            var canCache = request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri.IndexOf("wp-admin") == -1;

            // Determine cache key
            var requestKey = canCache ? request.RequestUri.ToString() : null;

            // Load from cache
            if (canCache)
            {
                var cachedResponse = await Cache.GetAsync<ResponseCacheItem>(requestKey);
                if (cachedResponse != null)
                {
                    return cachedResponse.ToResponseMessage();
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
                if (response.StatusCode == HttpStatusCode.OK || (int)response.StatusCode >= 400 && (int)response.StatusCode <= 500 && canCache)
                {
                    // Cache Item (if not already cached)
                    await Cache.GetOrCreateAsync(requestKey, () => Task.FromResult(ResponseCacheItem.FromResponse(response, content)));
                }
            }
            return response;

        }
    }
}
