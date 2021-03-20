using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BCC.WPProxy
{
    public class WPProxySettings
    {
        public const string AuthorizationPolicy = "wp-proxy";
        public string DestinationAddress { get; set; }

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

        public string ProxyAddress { get; set; }
    }
}
