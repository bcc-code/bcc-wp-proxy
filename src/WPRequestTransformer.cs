using Microsoft.AspNetCore.Http;
using Microsoft.ReverseProxy.Service.Proxy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace BCC.WPProxy
{
    public class WPRequestTransformer : HttpTransformer
    {
        

        public override Task TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix)
        {
            return base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix);
        }

        public override Task TransformResponseAsync(HttpContext httpContext, HttpResponseMessage proxyResponse)
        {
            return base.TransformResponseAsync(httpContext, proxyResponse);
        }

        public override Task TransformResponseTrailersAsync(HttpContext httpContext, HttpResponseMessage proxyResponse)
        {
            return base.TransformResponseTrailersAsync(httpContext, proxyResponse);
        }
    }
}
