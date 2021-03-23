using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BCC.WPProxy
{
    public class CacheService
    {
        public CacheService(IMemoryCache memoryCache, IDistributedCache distributedCache, WPProxySettings settings)
        {
            MemoryCache = memoryCache;
            DistributedCache = distributedCache;
            Settings = settings;
        }

        public IMemoryCache MemoryCache { get; }
        public IDistributedCache DistributedCache { get; }
        public WPProxySettings Settings { get; }

        public async Task<T> GetAsync<T>(string key, CancellationToken cancellation = default)
        {
            var value = default(T);
            if (!MemoryCache.TryGetValue(key, out value))
            {
                var semaphore = _cacheSemaphores.GetOrAdd(key, new SemaphoreSlim(1));
                await semaphore.WaitAsync();
                try
                {
                    var result = await DistributedCache.GetStringAsync(key, cancellation);
                    if (!string.IsNullOrEmpty(result))
                    {
                        value = ParseString<T>(result);
                        if (value != null)
                        {
                            SetMemoryCacheValue(key, value);
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }
            return value;
        }

        private static ConcurrentDictionary<string, SemaphoreSlim> _cacheSemaphores = new ConcurrentDictionary<string, SemaphoreSlim>();


        public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> createFn, CancellationToken cancellation = default)
        {
            // Attempt to get from cache before requesting semaphore
            var value = default(T);
            if (MemoryCache.TryGetValue(key, out value))
            {
                return value;
            }

            // Request semaphore (to avoid two threads caching simultaneously)
            var semaphore = _cacheSemaphores.GetOrAdd(key, new SemaphoreSlim(1));
            await semaphore.WaitAsync();
            try
            {
                return await MemoryCache.GetOrCreateAsync(key, async (cache) =>
                {
                    // Cache settings
                    cache.SlidingExpiration = Settings.CacheLifetime;

                    // Attempt to retrieve from distributed cache first
                    var result = await DistributedCache.GetStringAsync(key);
                    if (!string.IsNullOrEmpty(result))
                    {
                        var value = ParseString<T>(result);
                        if (value != null)
                        {
                            return value;
                        }
                    }

                    // Retrieve from source
                    var sourceValue = await createFn();
                    if (sourceValue != null && sourceValue.ToString() != "")
                    {
                        // Update distributed cache
                        var strSourceValue = ConvertToString(sourceValue);
                        await DistributedCache.SetStringAsync(key, strSourceValue, new DistributedCacheEntryOptions
                        {
                            SlidingExpiration = Settings.CacheLifetime
                        }, cancellation);
                    }
                    return sourceValue;

                });
            }
            finally
            {
                semaphore.Release();
            }

        }

        protected void SetMemoryCacheValue<T>(string key, T value)
        {
            MemoryCache.Set(key, value, new MemoryCacheEntryOptions
            {
                SlidingExpiration = Settings.CacheLifetime
            });
        }

        protected string ConvertToString<T>(T value)
        {
            if (value == null)
            {
                return null;
            }
            if (typeof(T) == typeof(string))
            {
                return value as string;
            }
            if (typeof(T).IsPrimitive)
            {
                return value.ToString();
            }

            return JsonConvert.SerializeObject(value);
        }

        protected T ParseString<T>(string result)
        {
            if (string.IsNullOrEmpty(result))
            {
                return default(T);
            }
            else
            {
                if (typeof(T) == typeof(string))
                {
                    return (T)(object)result;
                }
                else
                {
                    if (typeof(T).IsPrimitive)
                    {
                        return (T)Convert.ChangeType(result, typeof(T));
                    }
                    else
                    {
                        return JsonConvert.DeserializeObject<T>(result);
                    }
                }
            }
        }
    }
}
