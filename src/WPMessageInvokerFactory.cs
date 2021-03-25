using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace BCC.WPProxy
{
    public class WPMessageInvokerFactory
    {
        public WPMessageInvokerFactory(WPMessageHandler handler)
        {
            Handler = handler;
        }

        public WPMessageHandler Handler { get; }

        public HttpMessageInvoker Create()
        {
            return new HttpMessageInvoker(Handler);
        }
    }
}
