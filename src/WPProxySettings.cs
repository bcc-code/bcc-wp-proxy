using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace BCC.WPProxy
{
    public class WPProxySettings
    {
        public const string AuthorizationPolicy = "wp-proxy";

        public string SourceAddress { get; set; }

        public string DefaultLanguage = "no";

        public bool AutoLanguageRedirect = true;


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

        public string RedisInstanceName { get; set; } = "";

        public string RedisIpAddress { get; set; }

        public string RedisPort { get; set; }

        public string ProxyKey { get; set; }

        public string UserLoginClaimType { get; set; }

        public string UserOrganizationClaimType { get; set; }

        public string IsSubscriberClaimType { get; set; }

        public string SiteOrganizationName { get; set; }

        public string GoogleStorageBucket { get; set; }

        public string CountryClaimType { get; set; }

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
}
