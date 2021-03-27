using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BCC.WPProxy
{
    public class InMemorySessionStore : ITicketStore
    {
        private static ConcurrentDictionary<string, AuthenticationTicket> _store = new ConcurrentDictionary<string, AuthenticationTicket>();
        public Task RemoveAsync(string key)
        {
            _store.TryRemove(key, out AuthenticationTicket value);
            return Task.CompletedTask;
        }

        public Task RenewAsync(string key, AuthenticationTicket ticket)
        {
            if (_store.ContainsKey(key))
            {
                _store[key] = ticket;
            }
            return Task.CompletedTask;
        }

        public Task<AuthenticationTicket> RetrieveAsync(string key)
        {
            var value = default(AuthenticationTicket);
            _store.TryGetValue(key, out value);
            return Task.FromResult(value);
        }

        public Task<string> StoreAsync(AuthenticationTicket ticket)
        {
            var key = Guid.NewGuid().ToString();
            _store[key] = ticket;
            return Task.FromResult(key);
        }
    }
}
