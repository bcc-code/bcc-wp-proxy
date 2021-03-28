using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using System.Security.Cryptography;

namespace BCC.WPProxy
{
    public class WPMessageHandler : DelegatingHandler
    {
        public WPMessageHandler(IFileStore fileStore, IHttpContextAccessor httpContext, WPCacheService cache, WPUserService userService, WPProxySettings settings) : base(new SocketsHttpHandler()
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false
        })
        {
            FileStore = fileStore;
            HttpContext = httpContext;
            Cache = cache;
            UserService = userService;
            Settings = settings;
        }

        public IFileStore FileStore { get; }
        public IHttpContextAccessor HttpContext { get; }
        public WPCacheService Cache { get; }
        public WPUserService UserService { get; }
        public WPProxySettings Settings { get; }

        bool IsAccessTokenRequest(string requestUri) => requestUri.Contains("access-token.php");
        bool IsIdTokenRequest(string requestUri) => requestUri.Contains("id-token.php");
        bool IsLogoutRequest(string requestUri) => requestUri.Contains("action=logout");
        bool IsStaticContent(string requestUri) => 
                                    requestUri.IndexOf(".js") != -1 ||
                                    requestUri.IndexOf(".css") != -1 ||
                                    requestUri.IndexOf(".png") != -1 ||
                                    requestUri.IndexOf(".gif") != -1 ||
                                    requestUri.IndexOf(".svg") != -1 ||
                                    requestUri.IndexOf(".webp") != -1 ||
                                    requestUri.IndexOf(".jpg") != -1 ||
                                    requestUri.IndexOf(".jpeg") != -1 ||
                                    requestUri.IndexOf(".woff") != -1;
        bool CanCacheRequest(HttpRequestMessage request) => request.Method == HttpMethod.Get && request.RequestUri.ToString().IndexOf("wp-admin") == -1;
        bool IsTextMediaType(string mediaType) => 
                                    (mediaType.IndexOf("text") != -1 || 
                                     mediaType.IndexOf("json") != -1 || 
                                     mediaType.IndexOf("xml") != -1 || 
                                     mediaType.IndexOf("javascript") != -1) &&
                                     mediaType.IndexOf("image") == -1;

        bool IsMultimediaMediaType(string mediaType) =>
                          mediaType.IndexOf("image") != -1 ||
                          mediaType.IndexOf("font") != -1 ||
                          mediaType.IndexOf("audio") != -1 ||
                          mediaType.IndexOf("video") != -1 ||
                          mediaType.IndexOf("application") != -1;

        protected async override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri.ToString();

            // Reroute access token requests
            if (IsAccessTokenRequest(requestUri))
            {
                return new HttpResponseMessage { Content = new StringContent(await HttpContext.HttpContext.GetTokenAsync("access_token")) };
            }
            if (IsIdTokenRequest(requestUri))
            {
                return new HttpResponseMessage { Content = new StringContent(await HttpContext.HttpContext.GetTokenAsync("id_token")) };
            }
            // Reroute logut
            if (IsLogoutRequest(requestUri))
            {
                return Redirect($"/account/logout?returnUrl={WebUtility.UrlEncode(request.Headers.Referrer.ToString() ?? "/")}");
            }

            // Modify request headers (for authentication)
            var wpUserId = (await UserService.MapToWpUserAsync(HttpContext.HttpContext.User)).ToString();
            request.Headers.Add("X-Wp-Proxy-User-Id", wpUserId);
            request.Headers.Add("X-Wp-Proxy-Key", Settings.ProxyKey);
            request.Headers.Host = Settings.DestinationHost;

            // Determine if content can/should be cached
            var canCache = CanCacheRequest(request);
            var isStaticContent = IsStaticContent(requestUri);

            // Determine cache key
            var requestKey = canCache ? (request.RequestUri.ToString() + "|" + (isStaticContent ? 0 : wpUserId)) : null;            
            var cacheKey = canCache ? ComputeCacheKeyHash(requestKey) : null;

            // Load from cache
            if (canCache)
            {
                var cachedResponse = await Cache.GetAsync<ResponseCacheItem>(cacheKey, refreshOnWPUpdate: !isStaticContent);
                if (cachedResponse != null)
                {
                    return await cachedResponse.ToResponseMessage(FileStore);
                }
            }

            // Execute request
            request.Version = new Version(1,1); // Backwards compatiblity for old servers
            var response = await base.SendAsync(request, cancellationToken);

            // Handle depending on media type
            var mediaType = response.Content?.Headers?.ContentType?.MediaType ?? "";
            if (IsTextMediaType(mediaType))
            {
                // Read content and transform content (replace destination address with proxy address)
                var content = TransformResponseContent(await response.Content.ReadAsStringAsync());                
                response.Content = new StringContent(content, Encoding.UTF8, response.Content.Headers.ContentType.MediaType);
                
                // Cache content
                if (canCache &&
                    (response.StatusCode == HttpStatusCode.OK || (int)response.StatusCode >= 400)
                    && (int)response.StatusCode <= 500)
                {
                    // Cache Item (if not already cached)
                    await Cache.GetOrCreateAsync(cacheKey, () => Task.FromResult(ResponseCacheItem.ForTextContent(response, content)), refreshOnWPUpdate: true);
                }
            } 
            else if (IsMultimediaMediaType(mediaType))
            {
                // Save file to disk
                var contentHeaders = response.Content.Headers;
                var content = await response.Content.ReadAsStreamAsync();
                var ms = new MemoryStream();
                await content.CopyToAsync(ms);
                ms.Position = 0;
                var storageKey = Settings.DestinationHost.Replace(":","-") + "-" + cacheKey;
                await FileStore.WriteFileAsync(storageKey, ms);
                ms.Position = 0;
                response.Content = new StreamContent(ms);
                foreach (var header in contentHeaders)
                {
                    response.Content.Headers.Add(header.Key, header.Value);
                }

                // Create cache entry
                await Cache.GetOrCreateAsync(cacheKey, () => Task.FromResult(
                        ResponseCacheItem.ForStreamContent(response, storageKey)), 
                        TimeSpan.FromMinutes(15),
                        refreshOnWPUpdate: !isStaticContent
                );
            }


            return response;

        }

      
        private HttpResponseMessage Redirect(string url)
        {
            var response = new HttpResponseMessage();
            if (!url.StartsWith("http"))
            {
                url = Settings.ProxyAddress + "/" + url.TrimStart('/');
            }
            response.StatusCode = HttpStatusCode.Redirect;
            response.Headers.Location = new Uri(url);
            return response;
        }

        static string ComputeCacheKeyHash(string rawData)
        {   
            using (var sha = SHA256.Create())
            {
                // ComputeHash - returns byte array  
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(rawData));

                // Convert byte array to a string   
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }
                return builder.ToString();
            }
        }

        /// <summary>
        /// Transform content by replacing source addresses with proxy addresses etc.
        /// </summary>
        /// <param name="sourceContent"></param>
        /// <returns></returns>
        protected string TransformResponseContent(string sourceContent)
        {
            var result = sourceContent
                    .Replace(Settings.DestinationAddress, Settings.ProxyAddress)
                    .Replace(Settings.DestinationAddress.Replace("/", "\\/"), Settings.ProxyAddress.Replace("/", "\\/"))
                    .Replace("http://", "https://");

            if (Settings.WwwDestinationAddress != Settings.ProxyAddress)
            {
                result = result
                    .Replace(Settings.WwwDestinationAddress, Settings.ProxyAddress)
                    .Replace(Settings.WwwDestinationAddress.Replace("/", "\\/"), Settings.ProxyAddress.Replace("/", "\\/"))
                    .Replace("http://", "https://");
            }
            return result;
        }

        /// <summary>
        /// Transform request headers so that they are compatible with the proxy
        /// </summary>
        /// <param name="request"></param>
        protected void TransformResponseHeaders(HttpResponseMessage response)
        {
            // Transform response headers
            if (response.Headers.Location != null)
            {
                response.Headers.Location = new Uri(response.Headers.Location.ToString()
                    .Replace(Settings.DestinationAddress, Settings.ProxyAddress)
                    .Replace(WebUtility.UrlEncode(Settings.DestinationAddress), WebUtility.UrlEncode(Settings.ProxyAddress)));

                if (Settings.WwwDestinationAddress != Settings.ProxyAddress)
                {
                    response.Headers.Location = new Uri(response.Headers.Location.ToString()
                         .Replace(Settings.WwwDestinationAddress, Settings.ProxyAddress)
                         .Replace(WebUtility.UrlEncode(Settings.WwwDestinationAddress), WebUtility.UrlEncode(Settings.ProxyAddress)));
                }
            }

        }


        public class ResponseCacheItem
        {
            public string Content { get; set; }
            public string Url { get; set; }
            public int Status { get; set; }
            public string MediaType { get; set; }
            public Version Version { get; set; }
            public string StorageKey { get; set; }

            public Dictionary<string, string[]> Headers { get; set; }
            public Dictionary<string, string[]> ContentHeaders { get; set; }

            public static ResponseCacheItem ForTextContent(HttpResponseMessage response, string content)
            {
                var itm = new ResponseCacheItem
                {
                    StorageKey = null,
                    Content = content,
                    Status = (int)response.StatusCode,
                    Headers = new Dictionary<string, string[]>(),
                    ContentHeaders = new Dictionary<string, string[]>(),
                    MediaType = response.Content.Headers.ContentType.MediaType,
                    Url = response.RequestMessage.RequestUri.ToString(),
                    Version = response.Version
                };
                foreach (var header in response.Headers)
                {
                    if (!header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        itm.Headers[header.Key] = header.Value.ToArray();
                    }
                }
                foreach (var header in response.Content.Headers)
                {
                    itm.ContentHeaders[header.Key] = header.Value.ToArray();
                }
                return itm;
            }

            public static ResponseCacheItem ForStreamContent(HttpResponseMessage response, string storageKey)
            {
                var itm = new ResponseCacheItem
                {
                    StorageKey = storageKey,
                    Content = null,
                    Status = (int)response.StatusCode,
                    Headers = new Dictionary<string, string[]>(),
                    ContentHeaders = new Dictionary<string, string[]>(),
                    MediaType = response.Content.Headers.ContentType.MediaType,
                    Url = response.RequestMessage.RequestUri.ToString(),
                    Version = response.Version
                };
                foreach (var header in response.Headers)
                {
                    if (!header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        itm.Headers[header.Key] = header.Value.ToArray();
                    }
                }
                foreach (var header in response.Content.Headers)
                {
                    itm.ContentHeaders[header.Key] = header.Value.ToArray();                    
                }
                return itm;
            }

            public async Task<HttpResponseMessage> ToResponseMessage(IFileStore fileStore)
            {
                var msg = new HttpResponseMessage
                {
                    StatusCode = (HttpStatusCode)this.Status,
                    Version = this.Version
                };

                if (!string.IsNullOrEmpty(StorageKey))
                {
                    var stream = await fileStore.ReadFileAsync(StorageKey);
                    msg.Content = new StreamContent(stream);
                }
                else
                {
                    msg.Content = new StringContent(this.Content.ToString(), Encoding.UTF8, this.MediaType);
                }
                foreach (var header in this.Headers)
                {
                    msg.Headers.Add(header.Key, header.Value);
                }
                foreach (var header in this.ContentHeaders)
                {
                    if (header.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase))
                    {
                        msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(MediaType);
                    }
                    else
                    {
                        msg.Content.Headers.Add(header.Key, header.Value);
                    }
                    
                }
                return msg;
            }
        }

    }
}
