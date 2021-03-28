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
        public string DestinationAddress { get; set; }

        public string WwwDestinationAddress => DestinationAddress.Replace("://", "://www.");

        public TimeSpan CacheDefaultSlidingExpiration { get; set; } = TimeSpan.FromMinutes(10);

        public TimeSpan CacheDefaultAbsoluteExpiration { get; set; } = TimeSpan.FromHours(6);

        public TimeSpan ImageMemoryCacheSlidingExpiration { get; set; } = TimeSpan.FromMinutes(60);

        public int ImageMemoryCacheMaxSizeInBytes { get; set; } = 250000; //250kb

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

        private string _destinationHost;


        public string DestinationHost
        {
            get
            {
                if (_destinationHost == null)
                {
                    _destinationHost = DestinationAddress.Replace("https://", "").Replace("http://", "").Trim('/');
                }
                return _destinationHost;
            }
        }

        private string _proxyHost;


        public string ProxyHost
        {
            get
            {
                if (_proxyHost == null)
                {
                    _proxyHost = ProxyAddress?.Replace("https://", "").Replace("http://", "").Trim('/');
                }
                return _proxyHost;
            }
        }


        public string ProxyAddress { get; set; }
    }
}
