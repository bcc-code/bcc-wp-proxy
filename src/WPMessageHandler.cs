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

      
        protected async override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri.ToString();

            // Reroute access token requests
            if (requestUri.Contains("access-token.php"))
            {
                return new HttpResponseMessage
                {
                    Content = new StringContent(await HttpContext.HttpContext.GetTokenAsync("access_token"))
                };
            }
            if (requestUri.Contains("id-token.php"))
            {
                return new HttpResponseMessage
                {
                    Content = new StringContent(await HttpContext.HttpContext.GetTokenAsync("id_token"))
                };
            }

            // Reroute logut
            if (requestUri.Contains("action=logout"))
            {
                var referrer = request.Headers.Referrer?.ToString() ?? "/";
                return Redirect($"/account/logout?returnUrl={WebUtility.UrlEncode(referrer)}");
            }

            // Modify request headers (for authentication)
            var wpUserId = (await UserService.MapToWpUserAsync(HttpContext.HttpContext.User)).ToString();
            request.Headers.Add("X-Wp-Proxy-User-Id", wpUserId);
            request.Headers.Add("X-Wp-Proxy-Key", Settings.ProxyKey);
            request.Headers.Host = Settings.DestinationHost;


            // Determine if content can/should be cached

            var canCache = request.Method == HttpMethod.Get && requestUri.IndexOf("wp-admin") == -1;
            var staticContent = requestUri.IndexOf(".js") != -1 ||
                                    requestUri.IndexOf(".css") != -1 ||
                                    requestUri.IndexOf(".png") != -1 ||
                                    requestUri.IndexOf(".gif") != -1 ||
                                    requestUri.IndexOf(".svg") != -1 ||
                                    requestUri.IndexOf(".webp") != -1 ||
                                    requestUri.IndexOf(".jpg") != -1 ||
                                    requestUri.IndexOf(".jpeg") != -1 ||
                                    requestUri.IndexOf(".woff") != -1;

            // Determine cache key
            var requestKey = canCache ? (request.RequestUri.ToString() + "|" + (staticContent ? 0 : wpUserId)) : null;
            var cacheKey = canCache ? ComputeSha256Hash(requestKey) : null;

            // Load from cache
            if (canCache)
            {
                var cachedResponse = await Cache.GetAsync<ResponseCacheItem>(cacheKey, refreshOnWPUpdate: !staticContent);
                if (cachedResponse != null)
                {
                    return await cachedResponse.ToResponseMessage(FileStore);
                }
            }


            // Execute request
            var response = await base.SendAsync(request, cancellationToken);
            var mediaType = response.Content?.Headers?.ContentType?.MediaType ?? "";
            var isText = mediaType.IndexOf("text") != -1 || 
                         mediaType.IndexOf("json") != -1 || 
                         mediaType.IndexOf("xml") != -1 || 
                         mediaType.IndexOf("javascript") != -1;

            if (isText)
            {
                // Read content and transform content (replace destination address with proxy address)
                var content = TransformResponseContent(await response.Content.ReadAsStringAsync());                
                response.Content = new StringContent(content, Encoding.UTF8, response.Content.Headers.ContentType.MediaType);

                
                // Cache content
                if ((response.StatusCode == HttpStatusCode.OK || (int)response.StatusCode >= 400)
                    && (int)response.StatusCode <= 500 
                    && canCache)
                {
                    // Cache Item (if not already cached)
                    await Cache.GetOrCreateAsync(cacheKey, () => Task.FromResult(ResponseCacheItem.ForTextContent(response, content)), refreshOnWPUpdate: true);
                }
            }


            var isImage = mediaType.IndexOf("jpg") != -1 ||
                          mediaType.IndexOf("jpeg") != -1 || 
                          mediaType.IndexOf("png") != -1 ||
                          mediaType.IndexOf("woff") != -1 ||
                          mediaType.IndexOf("gif") != -1;
            if (isImage)
            {
                
                // Save file to disk
                var content = await response.Content.ReadAsStreamAsync();
                var ms = new MemoryStream();
                await content.CopyToAsync(ms);
                ms.Position = 0;
                await FileStore.WriteFileAsync(cacheKey, ms);
                ms.Position = 0;
                response.Content = new StreamContent(ms);

                // Create cache entry
                await Cache.GetOrCreateAsync(cacheKey, () => Task.FromResult(
                        ResponseCacheItem.ForStreamContent(response, cacheKey)), 
                        TimeSpan.FromMinutes(15),
                        refreshOnWPUpdate: !staticContent
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

        static string ComputeSha256Hash(string rawData)
        {   
            using (var sha = SHA1.Create())
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
            return sourceContent
                    .Replace(Settings.DestinationAddress, Settings.ProxyAddress)
                    .Replace(Settings.DestinationAddress.Replace("/", "\\/"), Settings.ProxyAddress.Replace("/", "\\/"))
                    .Replace("http://", "https://");
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

            public static ResponseCacheItem ForTextContent(HttpResponseMessage response, string content)
            {
                var itm = new ResponseCacheItem
                {
                    StorageKey = null,
                    Content = content,
                    Status = (int)response.StatusCode,
                    Headers = new Dictionary<string, string[]>(),
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
                    MediaType = null,
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
                    var ms = new MemoryStream();
                    await fileStore.ReadFileAsync(StorageKey, ms);
                    ms.Position = 0;
                    msg.Content = new StreamContent(ms);
                }
                else
                {
                    msg.Content = new StringContent(this.Content.ToString(), Encoding.UTF8, this.MediaType);
                }
                foreach (var header in this.Headers)
                {
                    msg.Headers.Add(header.Key, header.Value);
                }
                return msg;
            }
        }

    }
}
