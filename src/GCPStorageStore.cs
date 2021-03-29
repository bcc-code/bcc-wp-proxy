using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Caching.Distributed;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BCC.WPProxy
{
    /// <summary>
    /// File store which reads from Google Cloud storage, but buffers content in a distributed in memory cache (i.e. redis)
    /// </summary>
    public class GCPStorageStore : IFileStore
    {
        private StorageClient _client;

        public GCPStorageStore(WPProxySettings settings, IDistributedCache distributedCache)
        {
            Settings = settings;
            DistributedCache = distributedCache;
        }

        public WPProxySettings Settings { get; }
        public IDistributedCache DistributedCache { get; }

        public async Task<bool> FileExistsAsync(string name)
        {
            var cachedBytes = await DistributedCache.GetAsync(name);
            if (cachedBytes != null && cachedBytes.Length > 0)
            {
                return true;
            }
            _client = _client ?? await StorageClient.CreateAsync();
            var obj = await _client.GetObjectAsync(Settings.GoogleStorageBucket, name);
            return obj?.Size > 0;
        }

        public async Task<Stream> ReadFileAsync(string name)
        {
            var cachedBytes = await DistributedCache.GetAsync(name);
            if (cachedBytes != null && cachedBytes.Length > 0)
            {
                var ms = new MemoryStream(cachedBytes);
                ms.Position = 0;
                return ms;
            }

            _client = _client ?? await StorageClient.CreateAsync();
            var obj = await _client.GetObjectAsync(Settings.GoogleStorageBucket, name);
            if (obj?.Size > 0) {
                var ms = new MemoryStream();
                await _client.DownloadObjectAsync(Settings.GoogleStorageBucket, name, ms);
                ms.Position = 0;

                if (ms.Length <= Settings.MultimediaMemoryCacheMaxSizeInBytes)
                {
                    // Save to memory cache
                    await DistributedCache.SetAsync(name, ms.ToArray(), new DistributedCacheEntryOptions
                    {
                        SlidingExpiration = Settings.MultimediaMemoryCacheSlidingExpiration
                    });
                    ms.Position = 0;
                }

                return ms;
            }
            return null;
        }

        public async Task WriteFileAsync(string name, Stream fileStream)
        {
            try
            {
                // Save to memory cache
                var ms = new MemoryStream();
                await fileStream.CopyToAsync(ms);
                ms.Position = 0;

                if (ms.Length <= Settings.MultimediaMemoryCacheMaxSizeInBytes)
                {
                    await DistributedCache.SetAsync(name, ms.ToArray(), new DistributedCacheEntryOptions
                    {
                        SlidingExpiration = Settings.MultimediaMemoryCacheSlidingExpiration
                    });
                    ms.Position = 0;
                }   

                // Upload file
                _client = _client ?? await StorageClient.CreateAsync();
                await _client.UploadObjectAsync(Settings.GoogleStorageBucket, name, null, ms);
            }
            catch
            {
                // File probably already exists
            }
        }
    }
}
