using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BCC.WPProxy
{
    public class RedisSessionStore : ITicketStore
    {
        public RedisSessionStore(IMemoryCache cache, IDistributedCache distributedCache)
        {
            Cache = cache;
            DistributedCache = distributedCache;
        }

        public IMemoryCache Cache { get; }
        public IDistributedCache DistributedCache { get; }

        public async Task RemoveAsync(string key)
        {
            Cache.Remove(key);
            await DistributedCache.RemoveAsync(key);
        }

        public async Task RenewAsync(string key, AuthenticationTicket ticket)
        {
            if (await RetrieveAsync(key) != null)
            {
                var expiry = ticket.Properties?.ExpiresUtc ?? DateTimeOffset.Now.AddDays(1);
                Cache.Set(key, ticket, expiry);
                var value = TicketSerializer.Default.Serialize(ticket);
                await DistributedCache.SetAsync(key, value, new DistributedCacheEntryOptions
                {
                    AbsoluteExpiration = expiry
                });
            }
        }

        public async Task<AuthenticationTicket> RetrieveAsync(string key)
        {
            if (Cache.TryGetValue(key, out AuthenticationTicket ticket))
            {
                return ticket;
            }
            var ticketBytes = await DistributedCache.GetAsync(key);
            if (ticketBytes != null && ticketBytes.Length > 0)
            {
                ticket = TicketSerializer.Default.Deserialize(ticketBytes);
                var expiry = ticket.Properties?.ExpiresUtc ?? DateTimeOffset.Now.AddDays(1);
                Cache.Set(key, ticket, expiry);
                return ticket;
            }
            return null;
        }

        public async Task<string> StoreAsync(AuthenticationTicket ticket)
        {
            var key = "ticket-" + Guid.NewGuid().ToString();
            var expiry = ticket.Properties?.ExpiresUtc ?? DateTimeOffset.Now.AddDays(1);
            Cache.Set(key, ticket, expiry);
            var value = TicketSerializer.Default.Serialize(ticket);
            await DistributedCache.SetAsync(key, value, new DistributedCacheEntryOptions
            {
                AbsoluteExpiration = expiry
            });
            return key;
        }
    }
}
