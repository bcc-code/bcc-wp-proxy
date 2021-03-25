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
        public WPUserService(CacheService cache, WPProxySettings settings, WPApiClient wpApi)
        {
            Cache = cache;
            Settings = settings;
            WPApi = wpApi;
        }

        public CacheService Cache { get; }
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
                var organization = user.FindFirst(Settings.OrganisationClaimType)?.Value;
                var hasMembership = user.FindFirst(Settings.HasMembershipClaimType)?.Value?.ToLower() ?? "false";
                bool bHasMembership = hasMembership == "true" || hasMembership == "1";

                var users = await GetWPUsersAsync();
                var match = users.FirstOrDefault(u => u.IsEnabled && !string.IsNullOrEmpty(u.Email) && u.Email.Equals(userEmail, StringComparison.OrdinalIgnoreCase)) ??
                            users.FirstOrDefault(u => u.IsEnabled && !string.IsNullOrEmpty(u.Login) && u.Login.Equals(userLogin, StringComparison.OrdinalIgnoreCase)) ??
                            users.FirstOrDefault(u => u.IsEnabled && !string.IsNullOrEmpty(u.Login) && bHasMembership && u.Login.Equals(organization, StringComparison.OrdinalIgnoreCase)) ??
                            users.FirstOrDefault(u => u.IsEnabled && !string.IsNullOrEmpty(u.Login) && bHasMembership && u.Login.Equals("Member", StringComparison.OrdinalIgnoreCase));

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
