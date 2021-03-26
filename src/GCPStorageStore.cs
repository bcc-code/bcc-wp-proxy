using Google.Cloud.Storage.V1;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BCC.WPProxy
{
    public class GCPStorageStore : IFileStore
    {
        private StorageClient _client;

        public GCPStorageStore(WPProxySettings settings)
        {
            Settings = settings;
        }

        public WPProxySettings Settings { get; }

        public async Task<bool> FileExistsAsync(string name)
        {
            _client = _client ?? await StorageClient.CreateAsync();
            var obj = await _client.GetObjectAsync(Settings.GoogleStorageBucket, name);
            return obj?.Size > 0;
        }

        public async Task<bool> ReadFileAsync(string name, Stream destinationStream)
        {
            _client = _client ?? await StorageClient.CreateAsync();
            var obj = await _client.GetObjectAsync(Settings.GoogleStorageBucket, name);
            if (obj?.Size > 0) {
                await _client.DownloadObjectAsync(Settings.GoogleStorageBucket, name, destinationStream);
                return true;
            }
            return false;
        }

        public async Task WriteFileAsync(string name, Stream fileStream)
        {
            try
            {
                _client = _client ?? await StorageClient.CreateAsync();
                await _client.UploadObjectAsync(Settings.GoogleStorageBucket, name, null, fileStream);
            }
            catch
            {
                // File probably already exists
            }
        }
    }
}
