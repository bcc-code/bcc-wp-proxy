using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BCC.WPProxy
{
    public interface IFileStore
    {
        Task WriteFileAsync(string name, Stream fileStream);

        Task<Stream> ReadFileAsync(string name);

        Task<bool> FileExistsAsync(string name);


    }
}
