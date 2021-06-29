using Microsoft.AspNetCore.Authorization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BCC.WPProxy
{
    public class WPProxyAuthorizationPolicy : IAuthorizationPolicyProvider
    {
        public WPProxyAuthorizationPolicy(WPProxySiteSettingsAccessor siteSettings)
        {
            SiteSettings = siteSettings;
        }

        protected WPProxySiteSettingsAccessor SiteSettings { get; }

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync()
        {
            return GetPolicyAsync(WPProxySettings.AuthorizationPolicy);
        }

        public Task<AuthorizationPolicy> GetFallbackPolicyAsync() => Task.FromResult<AuthorizationPolicy>(null);

        public Task<AuthorizationPolicy> GetPolicyAsync(string policyName)
        {
            if (policyName == WPProxySettings.AuthorizationPolicy)
            {
                if (SiteSettings.Current.OffloadAuthentication)
                {
                    return Task.FromResult(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
                }
                else
                {
                    return Task.FromResult(new AuthorizationPolicyBuilder().RequireAssertion(h => true).Build());
                }
            }
            return null;
        }


    }
}
