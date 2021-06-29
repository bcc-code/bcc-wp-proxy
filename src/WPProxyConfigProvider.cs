using BCC.WPProxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Microsoft.ReverseProxy.Abstractions;
using Microsoft.ReverseProxy.Service;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class InMemoryConfigProviderExtensions
    {
        public static IReverseProxyBuilder AddConfig(this IReverseProxyBuilder builder)
        {
            builder.Services.AddSingleton<IProxyConfigProvider, WPProxyConfigProvider>();
            return builder;
        }
    }
}

namespace BCC.WPProxy
{


    public class WPProxyConfigProvider : IProxyConfigProvider
    {
        WPProxyConfig _config;

        public WPProxyConfigProvider(WPProxySettings settings)
        {
            _config = new WPProxyConfig(settings);
        }

        public IProxyConfig GetConfig()
        {
            return _config;
        }

        public class WPProxyConfig : IProxyConfig
        {
            private WPProxySettings Settungs { get; }

            public WPProxyConfig(WPProxySettings settings)
            {
                Settungs = settings;
            }
            public IReadOnlyList<ProxyRoute> Routes => new[]
            {
                
                new ProxyRoute
                {
                    RouteId = "wp-route",
                    ClusterId = "wp-cluster",
                    Match = new ProxyMatch
                    {
                        Path = "{**catch-all}"
                    },
                    AuthorizationPolicy = WPProxySettings.AuthorizationPolicy
                }
            };

            public IReadOnlyList<Cluster> Clusters => new[]
            {
                new Cluster
                {
                    Id = "wp-cluster",
                    Destinations = Settungs.Sites.ToDictionary(s => s.Key, s => new Destination() { Address = s.Value.SourceAddress })                    
                }
            };

            public IChangeToken ChangeToken { get; } = new CancellationChangeToken(default);

        }
    }
}
