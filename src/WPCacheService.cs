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
 
    /// <summary>
    /// The WPCacheService provides a memory cache (with fallback to a distributed memory cache) for text content.
    /// The cache service also regularly checks for content updates from WordPress which are used to invalidate the cache.
    /// </summary>
    public class WPCacheService
    {
        public WPCacheService(IMemoryCache memoryCache, IDistributedCache distributedCache, WPProxySettings settings, WPApiClient client)
        {
            MemoryCache = memoryCache;
            DistributedCache = distributedCache;
            Settings = settings;
            Wordpress = client;
        }

        public IMemoryCache MemoryCache { get; }
        public IDistributedCache DistributedCache { get; }
        public WPProxySettings Settings { get; }
        public WPApiClient Wordpress { get; }


        public async Task<T> GetAsync<T>(string key, bool refreshOnWPUpdate = true, CancellationToken cancellation = default)
        {
            var wpTimestamp = refreshOnWPUpdate ? await GetWPLastUpdatedAsync(cancellation) : 0;

            var value = default(CacheItem<T>);
            var hasValue = MemoryCache.TryGetValue(key, out value);
            if (!hasValue || value.WPTimestamp != wpTimestamp)
            {
                var semaphore = _cacheSemaphores.GetOrAdd(key, new SemaphoreSlim(1));
                await semaphore.WaitAsync();
                try
                {
                    var result = await DistributedCache.GetStringAsync(key, cancellation);
                    if (!string.IsNullOrEmpty(result))
                    {
                        value = ParseString<CacheItem<T>>(result);
                        if (value != null && value.WPTimestamp == wpTimestamp)
                        {
                            SetMemoryCacheValue(key, value);
                        }
                        else
                        {
                            return default(T);
                        }
                    }
                    return default(T);
                }
                finally
                {
                    semaphore.Release();
                }
            }
            return value.Item;
        }

        class CacheItem<T>
        {
            public long WPTimestamp { get; set; }
            public T Item { get; set; }
        }

        private static ConcurrentDictionary<string, SemaphoreSlim> _cacheSemaphores = new ConcurrentDictionary<string, SemaphoreSlim>();



        public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> createFn, TimeSpan? slidingExpiration = default, bool refreshOnWPUpdate = true, CancellationToken cancellation = default)
        {
            var wpTimestamp = refreshOnWPUpdate ? await GetWPLastUpdatedAsync(cancellation) : 0;

            // Attempt to get from cache before requesting semaphore
            var value = default(CacheItem<T>);
            if (MemoryCache.TryGetValue(key, out value))
            {
                if (value.WPTimestamp == wpTimestamp)
                {
                    return value.Item;
                }
            }

            // Request semaphore (to avoid two threads caching simultaneously)
            var semaphore = _cacheSemaphores.GetOrAdd(key, new SemaphoreSlim(1));
            await semaphore.WaitAsync();
            try
            {
                refresh:
                var cacheResult = await MemoryCache.GetOrCreateAsync<CacheItem<T>>(key, async (cache) =>
                {
                    // Cache settings
                    cache.SlidingExpiration = slidingExpiration ?? Settings.CacheDefaultSlidingExpiration;
                    cache.AbsoluteExpirationRelativeToNow = Settings.CacheDefaultAbsoluteExpiration;

                    // Attempt to retrieve from distributed cache first
                    var result = await DistributedCache.GetStringAsync(key);
                    if (!string.IsNullOrEmpty(result))
                    {
                        var value = ParseString<CacheItem<T>>(result);
                        if (value != null && value.WPTimestamp == wpTimestamp)
                        {
                            return value;
                        }
                    }

                    // Retrieve from source
                    var sourceValue = await createFn();
                    var cacheItem = new CacheItem<T>
                    {
                        Item = sourceValue,
                        WPTimestamp = wpTimestamp
                    };
                    if (sourceValue != null && sourceValue.ToString() != "")
                    {
                        // Update distributed cache
                        var strSourceValue = ConvertToString(cacheItem);
                        await DistributedCache.SetStringAsync(key, strSourceValue, new DistributedCacheEntryOptions
                        {
                            SlidingExpiration = slidingExpiration ?? Settings.CacheDefaultSlidingExpiration,
                            AbsoluteExpirationRelativeToNow = Settings.CacheDefaultAbsoluteExpiration
                            
                        }, cancellation);
                    }
                    return cacheItem;
                });

                // Refresh if content has been updated in wordpress
                if (cacheResult.WPTimestamp != wpTimestamp)
                {
                    MemoryCache.Remove(key);
                    goto refresh;
                }

                return cacheResult.Item;

            }
            finally
            {
                semaphore.Release();
            }

        }


        private static ConcurrentDictionary<string, SemaphoreSlim> _setCacheSemaphores = new ConcurrentDictionary<string, SemaphoreSlim>();


        public async Task SetAsync<T>(string key, T value, long wpTimestamp = 0, TimeSpan? slidingExpiration = null, CancellationToken cancellation = default)
        {
            // Request semaphore (to avoid two threads caching simultaneously)
            var semaphore = _setCacheSemaphores.GetOrAdd(key, new SemaphoreSlim(1));
            await semaphore.WaitAsync();
            try
            {
                var cacheItem = new CacheItem<T>
                {
                    Item = value,
                    WPTimestamp = wpTimestamp
                };
                MemoryCache.Set(key, cacheItem, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = Settings.CacheDefaultAbsoluteExpiration,
                    SlidingExpiration = Settings.CacheDefaultAbsoluteExpiration
                });

                // Update distributed cache
                var strSourceValue = ConvertToString(cacheItem);
                await DistributedCache.SetStringAsync(key, strSourceValue, new DistributedCacheEntryOptions
                {
                    SlidingExpiration = slidingExpiration ?? Settings.CacheDefaultSlidingExpiration,
                    AbsoluteExpirationRelativeToNow = Settings.CacheDefaultAbsoluteExpiration

                }, cancellation);
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
                SlidingExpiration = Settings.CacheDefaultSlidingExpiration,
                AbsoluteExpirationRelativeToNow = Settings.CacheDefaultAbsoluteExpiration
            });
        }

        /// <summary>
        /// Retrieves last updated timestamp from wordpress
        /// </summary>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<long> GetWPLastUpdatedAsync(CancellationToken cancellation)
        {
            // Check if content has changed max every 5 seconds
            var checkInterval = TimeSpan.FromSeconds(5);
            var cacheKey = $"{Settings.SourceAddress}|wp-timestamp";
            var wpUpdated = await GetOrCreateAsync(cacheKey, async () =>
            {
                var wpTimestamp = await Wordpress.GetAsync<long>("last-updated");
                return new WPContentUpdated
                {
                    LastCheck = DateTimeOffset.Now,
                    WPTimestamp = wpTimestamp
                };
            }, TimeSpan.FromMinutes(10), refreshOnWPUpdate: false, cancellation);

            if (wpUpdated == null || (DateTimeOffset.Now - wpUpdated.LastCheck) > checkInterval)
            {
                // Check timestamp again
                var newWpTimestamp = await Wordpress.GetAsync<long>("last-updated");

                // Update cache
                await SetAsync(cacheKey, new WPContentUpdated
                {
                    LastCheck = DateTimeOffset.Now,
                    WPTimestamp = newWpTimestamp
                }, 0, null, cancellation);

                return newWpTimestamp;
            }
            return wpUpdated.WPTimestamp;
        }

        class WPContentUpdated
        {
            public long WPTimestamp { get; set; }
            public DateTimeOffset LastCheck { get; set; }
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
