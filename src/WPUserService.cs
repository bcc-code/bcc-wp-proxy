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
        public WPUserService(WPCacheService cache, WPProxySettings settings, WPApiClient wpApi)
        {
            Cache = cache;
            Settings = settings;
            WPApi = wpApi;
        }

        public WPCacheService Cache { get; }
        public WPProxySettings Settings { get; }
        public WPApiClient WPApi { get; }

        public Task<int> MapToWpUserAsync(ClaimsPrincipal user)
        {
            var userId = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? null;
            if (string.IsNullOrEmpty(userId))
            {
                return Task.FromResult(0);
            }
            return Cache.GetOrCreateAsync($"{Settings.DestinationAddress}|wp-user|{userId}", async () =>
            {
                var userEmail = user.FindFirst(ClaimTypes.Email)?.Value;
                var userLogin = user.FindFirst(Settings.UserLoginClaimType)?.Value;
                var userOrganization = user.FindFirst(Settings.UserOrganizationClaimType)?.Value;
                var isMember = Settings.SiteOrganizationName.Equals(userOrganization, StringComparison.OrdinalIgnoreCase);
                var isSubscriber = bool.Parse(user.FindFirst(Settings.IsSubscriberClaimType)?.Value?.ToLower() ?? "false");

                var users = await GetWPUsersAsync();
                var match = users.FirstOrDefault(u => u.IsEnabled && !string.IsNullOrEmpty(u.Email) && u.Email.Equals(userEmail, StringComparison.OrdinalIgnoreCase)) ??
                            users.FirstOrDefault(u => u.IsEnabled && !string.IsNullOrEmpty(u.Login) && u.Login.Equals(userLogin, StringComparison.OrdinalIgnoreCase)) ??
                            users.FirstOrDefault(u => u.IsEnabled && !string.IsNullOrEmpty(u.Login) && u.Login.Equals(userOrganization, StringComparison.OrdinalIgnoreCase)) ??
                            users.FirstOrDefault(u => u.IsEnabled && !string.IsNullOrEmpty(u.Login) && isMember && u.Login.Equals("member", StringComparison.OrdinalIgnoreCase)) ??
                            users.FirstOrDefault(u => u.IsEnabled && !string.IsNullOrEmpty(u.Login) && isSubscriber && u.Login.Equals("subscriber", StringComparison.OrdinalIgnoreCase));

                var wpId = match?.ID ?? 0;
                return wpId;
            }, Settings.CacheDefaultSlidingExpiration);
        }

        private Task<List<WPUser>> GetWPUsersAsync()
        {
            return Cache.GetOrCreateAsync($"{Settings.DestinationAddress}|wp-users", () => WPApi.GetAsync<List<WPUser>>("users"), TimeSpan.FromMinutes(15));
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
