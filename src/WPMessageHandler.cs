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
using System.Globalization;

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
            Context = httpContext;
            Cache = cache;
            UserService = userService;
            Settings = settings;
        }

        public IFileStore FileStore { get; }
        public IHttpContextAccessor Context { get; }
        public WPCacheService Cache { get; }
        public WPUserService UserService { get; }
        public WPProxySettings Settings { get; }

        public HttpContext HttpContext => Context.HttpContext;

        bool IsAccessTokenRequest(string requestPath) => requestPath.Contains("access-token.php");
        bool IsIdTokenRequest(string requestPath) => requestPath.Contains("id-token.php");
        bool IsLogoutRequest(string requestPath) => requestPath.Contains("action=logout");

        bool IsRootUrl(string requestPath) => requestPath.Trim('/') == "";

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

        bool IsScript(string requestUri) =>
                                    requestUri.IndexOf(".js") != -1 ||
                                    requestUri.IndexOf(".css") != -1;


        bool CanCacheRequest(HttpRequestMessage request) => request.Method == HttpMethod.Get && request.RequestUri.ToString().IndexOf("wp-admin") == -1;
        
        bool ShouldCacheResponse(HttpResponseMessage response) => 
                                    (response.StatusCode == HttpStatusCode.OK || (int)response.StatusCode >= 400) &&
                                    ((int)response.StatusCode <= 500);
        
        bool IsTextMediaType(string mediaType) => 
                                    (mediaType.IndexOf("text") != -1 || 
                                     mediaType.IndexOf("json") != -1 ||
                                     mediaType.IndexOf("xml") != -1 // || 
                                     // mediaType.IndexOf("javascript") != -1 // creates problems for svgs loaded via js
                                    ) && mediaType.IndexOf("image") == -1;

        bool IsMultimediaMediaType(string mediaType) =>
                          mediaType.IndexOf("image") != -1 ||
                          mediaType.IndexOf("font") != -1 ||
                          mediaType.IndexOf("audio") != -1 ||
                          mediaType.IndexOf("video") != -1 ||
                          mediaType.IndexOf("application") != -1;


        protected async override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri.ToString();
            var requestPath = request.RequestUri.PathAndQuery;
            var proxyRequestHost = Context.HttpContext.Request.Host;
            var proxyAddress = $"{Context.HttpContext.Request.Scheme}://{proxyRequestHost}";
            var sourceAddress = Settings.SourceAddress;
            

            // Reroute access token requests
            if (IsAccessTokenRequest(requestPath))
            {
                return new HttpResponseMessage { Content = new StringContent(await HttpContext.GetTokenAsync("access_token")) };
            }
            if (IsIdTokenRequest(requestPath))
            {
                return new HttpResponseMessage { Content = new StringContent(await HttpContext.GetTokenAsync("id_token")) };
            }
            // Reroute logut
            if (IsLogoutRequest(requestPath))
            {
                return Redirect($"{proxyAddress}/account/logout?returnUrl={WebUtility.UrlEncode(request.Headers.Referrer.ToString() ?? "/")}");
            }

            // Redirect to previously selected language
            if (IsRootUrl(requestPath) && Settings.AutoLanguageRedirect && !(request.Headers.Referrer?.ToString() ?? "").StartsWith(proxyAddress))
            {
                var requestLanguageCookie = GetRequestLanguageCookie(request);
                if (!string.IsNullOrEmpty(requestLanguageCookie) && requestLanguageCookie != Settings.DefaultLanguage)
                {
                    return Redirect($"{proxyAddress}/{requestLanguageCookie}");
                }
            }

            // Modify request headers (for authentication)
            var wpUserId = await UserService.MapToWpUserAsync(HttpContext.User);
            request.Headers.Add("X-Wp-Proxy-User-Id", wpUserId.ToString());
            request.Headers.Add("X-Wp-Proxy-Key", Settings.ProxyKey);
            request.Headers.Host = Settings.SourceHost;

            // Determine if content can/should be cached
            var isDynamicContent = !IsStaticContent(requestUri);
            var canCache = CanCacheRequest(request) || !isDynamicContent;            
            var isScript = IsScript(requestUri);

            // Determine cache key
            var requestKey = canCache ? (request.RequestUri.ToString() + "|" + (isDynamicContent ? wpUserId: 0)) : null;            
            var cacheKey = canCache ? ComputeCacheKeyHash(requestKey) : null;

            // Load from cache
            if (canCache)
            {
                var responseCacheItem = await Cache.GetAsync<ResponseCacheItem>(cacheKey, refreshOnWPUpdate: isDynamicContent || isScript);
                if (responseCacheItem != null)
                {
                    var cachedResponse = await responseCacheItem.ToResponseMessage(FileStore);
                    if (canCache)
                    {
                        SetLanguageCookies(cachedResponse, request);
                    }
                    return cachedResponse;
                }
            }

            // Execute request
            if (canCache)
            {
                // Remove cookie headers
                request.Headers.Remove("cookie");
            }
            var response = await base.SendAsync(request, cancellationToken);
            if (canCache)
            {
                // Remove cookie headers
                response.Headers.Remove("cookie");
                response.Headers.Remove("set-cookie");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return RedirectToLogin();
            }

            // Handle depending on media type
            var mediaType = response.Content?.Headers?.ContentType?.MediaType ?? "";
            if (IsTextMediaType(mediaType))
            {
                // Text content (documents, json responses etc.) may contain references to 
                // the destination/source address and the content may need to be transformed to
                // replace these references with the proxy's address

                // Read content and transform content (replace source address with proxy address)
                var contentBuilder = new StringBuilder(await response.Content.ReadAsStringAsync());
                RewriteUrls(contentBuilder, sourceAddress, proxyAddress);
                var contentString = contentBuilder.ToString();
                response.Content = new StringContent(contentString, Encoding.UTF8, response.Content.Headers.ContentType.MediaType);
                
                // Cache content if response is valid for caching
                if (canCache && ShouldCacheResponse(response))
                {
                    // Cache Item (if not already cached)
                    await Cache.GetOrCreateAsync(cacheKey, () => Task.FromResult(ResponseCacheItem.ForTextContent(response, contentString)), refreshOnWPUpdate: true);
                }
            } 
            else if (IsMultimediaMediaType(mediaType))
            {
                // Check if response should be cached (e.g not a 500 response or similar)
                if (canCache && ShouldCacheResponse(response))
                {
                    // Read content stream to memory
                    var contentHeaders = response.Content.Headers;
                    var content = await response.Content.ReadAsStreamAsync();
                    var ms = new MemoryStream();
                    await content.CopyToAsync(ms);

                    // Save memory stream to disk/storage cache
                    ms.Position = 0;
                    var storageKey = $"{Settings.SourceHost}/{cacheKey}";
                    await FileStore.WriteFileAsync(storageKey, ms);

                    // Create cache entry which references disk/storage
                    await Cache.GetOrCreateAsync(cacheKey, () => Task.FromResult(
                            ResponseCacheItem.ForStreamContent(response, storageKey)),
                            TimeSpan.FromMinutes(15),
                            refreshOnWPUpdate: isDynamicContent
                    );

                    // Link response to memory stream
                    ms.Position = 0;
                    response.Content = new StreamContent(ms);
                    foreach (var header in contentHeaders)
                    {
                        response.Content.Headers.Add(header.Key, header.Value);
                    }
                }

            }

            RewriteRedirectUrls(response, sourceAddress, proxyAddress);
            if (canCache && isDynamicContent)
            {
                SetLanguageCookies(response, request);
            }
            return response;

        }

  

        private HttpResponseMessage SetLanguageCookies(HttpResponseMessage response, HttpRequestMessage request)
        {   
            // Add cookie headers for language (if language has changed)
            if (HttpContext.Request.Headers.ContainsKey("Referer"))
            {
                var previousLanuage = GetLanuageFromUri(new Uri(HttpContext.Request.Headers["Referer"].FirstOrDefault()));
                var currentLanguage = GetLanuageFromUri(request.RequestUri);
                if (currentLanguage != previousLanuage)
                {
                    var cookieExpiry = DateTime.Now.AddDays(7);
                    var secondsToExpiry = (cookieExpiry - DateTime.Now).Seconds;
                    response.Headers.Add("set-cookie", $"wp-wpml_current_language={currentLanguage}; Expires={cookieExpiry.ToString("R")}; Max-Age={secondsToExpiry}; Path=/; HttpOnly; SameSite=Lax;");
                    response.Headers.Add("set-cookie", $"lang={currentLanguage}; Expires={cookieExpiry.ToString("R")}; Max-Age={secondsToExpiry}; Path=/; HttpOnly; SameSite=Lax;");
                }
            }
            return response;
        }

        private string GetRequestLanguageCookie(HttpRequestMessage request)
        {
            // Get cookies sent by browse
            var cookiesHeader = request.Headers.FirstOrDefault(h => h.Key.Equals("cookie", StringComparison.InvariantCultureIgnoreCase));

            // Remove cookies (session cookies etc)
            request.Headers.Remove("cookie");
            var cookiesToKeep = new List<string>();

            // Add language cookies back
            if (cookiesHeader.Value != null)
            {
                foreach (var header in cookiesHeader.Value)
                {
                    var cookies = header.Split(';');
                    foreach (var cookie in cookies)
                    {
                        var kv = cookie.Split('=').Select(s => s.Trim()).ToArray();
                        if (kv.Length >= 2)
                        {
                            var k = kv[0];
                            if (k == "wp-wpml_current_language" || k == "lang")
                            {
                                return kv[1];
                            }
                        }
                    }
                }
            }
            return null;
        }

        private string GetLanuageFromUri(Uri uri)
        {
            var path = uri.PathAndQuery.TrimStart('/');
            if (path.Length > 2 && path[2] == '/')
            {
                return path.Substring(0, 2);
            }
            return Settings.DefaultLanguage;
        }


        private HttpResponseMessage RedirectToLogin()
        {
            return Redirect($"{HttpContext.Request.Scheme}://{HttpContext.Request.Host}/account/login?returnUrl={WebUtility.UrlEncode(HttpContext.Request.ToString())}");
        }


        private HttpResponseMessage Redirect(string url)
        {
            var response = new HttpResponseMessage();
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

        protected bool ShouldRedirectToNormalizedUrl(HttpRequestMessage request, string proxyAddress, out string normalizedUrl)
        {
            normalizedUrl = null;

            // Fix double slashes
            if (request.RequestUri.PathAndQuery.IndexOf("//") != -1)
            {
                var normalizedPathAndQuery = request.RequestUri.PathAndQuery.Replace("//", "/");
                normalizedUrl = $"{proxyAddress}{normalizedPathAndQuery}";
                return true;
            }
            return false;
        }

        /// <summary>
        /// Transform content by replacing source addresses with proxy addresses etc.
        /// </summary>
        /// <param name="sourceContent"></param>
        /// <returns></returns>
        protected StringBuilder RewriteUrls(StringBuilder sourceContent, string originalBaseAddress, string newBaseAddress)
        {
            var encodedOriginalBaseAddress = WebUtility.UrlEncode(originalBaseAddress);
            var encodedNewBaseAddress = WebUtility.UrlEncode(newBaseAddress);

            var result = sourceContent
                    .Replace(originalBaseAddress, newBaseAddress)
                    .Replace(encodedOriginalBaseAddress, encodedNewBaseAddress)
                    .Replace(originalBaseAddress.Replace("/", "\\/"), newBaseAddress.Replace("/", "\\/"))
                    .Replace("http://", "https://");

            var altOriginalBaseAddress = originalBaseAddress.Contains("://www.") ? originalBaseAddress.Replace("://www.", "://") : originalBaseAddress.Replace("://", "://www.");
            if (altOriginalBaseAddress != newBaseAddress)
            {
                var encodedAltDestinationAddress = WebUtility.UrlEncode(altOriginalBaseAddress);
                result = result
                    .Replace(altOriginalBaseAddress, newBaseAddress)
                    .Replace(encodedAltDestinationAddress, encodedNewBaseAddress)
                    .Replace(altOriginalBaseAddress.Replace("/", "\\/"), newBaseAddress.Replace("/", "\\/"))
                    .Replace("http://", "https://");
            }
            return sourceContent;
        }

        /// <summary>
        /// Transform request headers so that they are compatible with the proxy
        /// </summary>
        /// <param name="request"></param>
        protected void RewriteRedirectUrls(HttpResponseMessage response, string sourceAddress, string proxyAddress)
        {
            // Transform response headers (ensure that redirects go back to the proxy instead of the destination/source)
            if (response.Headers.Location != null)
            {
                response.Headers.Location = new Uri(RewriteUrls(new StringBuilder(response.Headers.Location.ToString()), sourceAddress, proxyAddress).ToString());
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
                SaveResponseHeaders(response, itm);
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
                SaveResponseHeaders(response, itm);
                return itm;
            }

            static void SaveResponseHeaders(HttpResponseMessage response, ResponseCacheItem itm)
            {
                foreach (var header in response.Headers)
                {
                    if (!header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals("cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        itm.Headers[header.Key] = header.Value.ToArray();
                    }

                }
                foreach (var header in response.Content.Headers)
                {
                    itm.ContentHeaders[header.Key] = header.Value.ToArray();
                }
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
