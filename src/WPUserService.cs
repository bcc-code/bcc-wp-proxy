using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace BCC.WPProxy
{
    public class WPUserService
    {
        public WPUserService(WPCacheService cache, WPProxySettings settings, WPProxySiteSettingsAccessor siteSettings, WPApiClient wpApi)
        {
            Cache = cache;
            Settings = settings;
            SiteSettings = siteSettings;
            WPApi = wpApi;
        }

        public WPCacheService Cache { get; }
        public WPProxySiteSettingsAccessor SiteSettings { get; }
        public WPProxySettings Settings { get; }
        public WPApiClient WPApi { get; }

        public Task<int> MapToWpUserAsync(ClaimsPrincipal user)
        {
            var siteSettings = SiteSettings.Current;
            var userId = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? null;
            if (string.IsNullOrEmpty(userId))
            {
                return Task.FromResult(0);
            }
            return Cache.GetOrCreateAsync($"{siteSettings.SourceAddress}|wp-user|{userId}", async () =>
            {
                var userEmail = user.FindFirst(ClaimTypes.Email)?.Value;
                var userLogin = user.FindFirst(Settings.UserLoginClaimType)?.Value;
                var userOrganization = user.FindFirst(Settings.UserOrganizationClaimType)?.Value;
                var isMember = !string.IsNullOrEmpty(siteSettings.OrganizationName) && siteSettings.OrganizationName.Equals(userOrganization, StringComparison.OrdinalIgnoreCase);
                var isSubscriber = bool.Parse(user.FindFirst(Settings.IsSubscriberClaimType)?.Value?.ToLower() ?? "false");

                var users = await GetWPUsersAsync();
                if (users != null)
                {
                    var match = users.FirstOrDefault(u => u.IsEnabled && !string.IsNullOrEmpty(u.Email) && u.Email.Equals(userEmail, StringComparison.OrdinalIgnoreCase)) ??
                                users.FirstOrDefault(u => u.IsEnabled && !string.IsNullOrEmpty(u.Login) && u.Login.Equals(userLogin, StringComparison.OrdinalIgnoreCase)) ??
                                users.FirstOrDefault(u => u.IsEnabled && !string.IsNullOrEmpty(u.Login) && u.Login.Equals(userOrganization, StringComparison.OrdinalIgnoreCase)) ??
                                users.FirstOrDefault(u => u.IsEnabled && !string.IsNullOrEmpty(u.Login) && isMember && u.Login.Equals("member", StringComparison.OrdinalIgnoreCase)) ??
                                users.FirstOrDefault(u => u.IsEnabled && !string.IsNullOrEmpty(u.Login) && isSubscriber && u.Login.Equals("subscriber", StringComparison.OrdinalIgnoreCase));

                    var wpId = match?.ID ?? 0;
                    return wpId;
                }
                return 0;
            }, Settings.CacheDefaultSlidingExpiration);
        }

        private Task<List<WPUser>> GetWPUsersAsync()
        {
            return Cache.GetOrCreateAsync($"{SiteSettings.Current.SourceAddress}|wp-users", () => WPApi.GetAsync<List<WPUser>>("users"), TimeSpan.FromMinutes(5));
        }        
    }

    public class WPUser
    {
        public int ID { get; set; } 
        public string Login { get; set; }
        public string Email { get; set; }
        public int Status { get; set; }
        public JToken Roles { get; set; } // Sometimes a string array, sometimes a dictionary... 

        public bool IsEnabled => Status == 0;
    }
}
