using Microsoft.Extensions.FileProviders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BCC.WPProxy
{
    public class PhysicalFileStore : IFileStore
    {
        public PhysicalFileStore(IFileProvider fileProvider)
        {
            FileProvider = fileProvider;
        }

        public IFileProvider FileProvider { get; }

        public Task<bool> FileExistsAsync(string name)
        {
            return Task.FromResult(FileProvider.GetFileInfo(name).Exists);
        }

        public async Task<bool> ReadFileAsync(string name, Stream destinationStream)
        {
            var file = FileProvider.GetFileInfo(name);
            if (file.Exists)
            {
                var stream = file.CreateReadStream();
                await stream.CopyToAsync(destinationStream);
                return true;
            }
            return false;
        }

        public async Task WriteFileAsync(string name, Stream fileStream)
        {
            using (var writeStream = File.OpenWrite(FileProvider.GetFileInfo(name).PhysicalPath))
            {
                await fileStream.CopyToAsync(writeStream);
                await writeStream.FlushAsync();
                writeStream.Close();
            }
        }
    }
}
