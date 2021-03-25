using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;

namespace BCC.WPProxy.Controllers
{
    [Route("id-token")]
    [ApiController]
    public class IdTokenController : ControllerBase
    {
        public Task<string> GetAccessTokenAsync()
        {
            return HttpContext.GetTokenAsync("id_token");
        }
    }
}
