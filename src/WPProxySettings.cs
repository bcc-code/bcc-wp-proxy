using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace BCC.WPProxy
{

    public class WPProxySiteSettingsAccessor
    {
        public WPProxySiteSettingsAccessor(IHttpContextAccessor httpContext, WPProxySettings settings)
        {
            HttpContext = httpContext;
            Settings = settings;
        }

        protected IHttpContextAccessor HttpContext { get; }
        protected WPProxySettings Settings { get; }

        public static AsyncLocal<WPProxySiteSettings> _currentSettings = new AsyncLocal<WPProxySiteSettings>();

        public WPProxySiteSettings Current
        {
            get
            {
                if (_currentSettings.Value == null)
                {
                    if (HttpContext.HttpContext == null)
                    {
                        return null;
                    }                    
                    var host = HttpContext.HttpContext.Request.Host.ToString();
                    var settings = Settings.GetForHost(host);
                    _currentSettings.Value = settings;
                }
                return _currentSettings.Value;
            }
        }
    }

    public class WPProxySiteSettings
    {
        public string ProxyHost { get; set; }

        public string SourceAddress { get; set; }

        public bool UseProxyPlugin { get; set; } = true;

        public bool OffloadAuthentication { get; set; } = true;

        public string DefaultLanguage = "no";

        public bool AutoLanguageRedirect = true;

        public string OrganizationName { get; set; }

        private string _sourceHost;
        public string SourceHost
        {
            get
            {
                if (_sourceHost == null)
                {
                    _sourceHost = SourceAddress.Replace("https://", "").Replace("http://", "").Trim('/');
                }
                return _sourceHost;
            }
        }

    }

    public class WPProxySettings
    {
        public const string AuthorizationPolicy = "wp-proxy";

        public IDictionary<string, WPProxySiteSettings> Sites { get; set; }

        public WPProxySiteSettings GetForHost(string host)
        {
            foreach (var site in Sites)
            {
                if (site.Value.ProxyHost.Equals(host, StringComparison.OrdinalIgnoreCase))
                {
                    return site.Value;
                }
            }
            throw new Exception($"{host} could not be mapped to a source address.");

        }

        /// <summary>
        /// Sliding expiration for retreiving content from source
        /// </summary>
        public TimeSpan CacheDefaultSlidingExpiration { get; set; } = TimeSpan.FromMinutes(60);

        /// <summary>
        /// Absolute max time to cache content before retreiving from source again
        /// </summary>
        public TimeSpan CacheDefaultAbsoluteExpiration { get; set; } = TimeSpan.FromHours(6);

        /// <summary>
        /// Sliding expiration for retreiving image/multimedia content from source
        /// </summary>
        public TimeSpan MultimediaMemoryCacheSlidingExpiration { get; set; } = TimeSpan.FromMinutes(60);

        /// <summary>
        /// Max size of multimedia items to store in distributed memory cache (instead of just disk storage).
        /// This setting essentially affects use of Redis memory
        /// </summary>
        public int MultimediaMemoryCacheMaxSizeInBytes { get; set; } = 250000; //250kb

        /// <summary>
        /// How often to check for content updates in WordPress (used to invalidate cache)
        /// </summary>
        public TimeSpan CacheContentUpdateCheckInterval { get; set; } = TimeSpan.FromSeconds(15);

        public bool UseRedis { get; set; } = false;

        public string ProxyKey { get; set; }

        public string RedisInstanceName { get; set; } = "";

        public string RedisIpAddress { get; set; }

        public string RedisPort { get; set; }

        public string GoogleStorageBucket { get; set; }

        public string UserLoginClaimType { get; set; }

        public string UserOrganizationClaimType { get; set; }

        public string CountryClaimType { get; set; }

        public string IsSubscriberClaimType { get; set; }

    }
}
