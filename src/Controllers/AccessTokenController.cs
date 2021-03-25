using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;

namespace BCC.WPProxy.Controllers
{
    [Authorize]
    [Route("access-token")]
    [ApiController]
    public class AccessTokenController : ControllerBase
    {
        public Task<string> GetAccessTokenAsync()
        {
            return HttpContext.GetTokenAsync("access_token");
        }
    }
}
