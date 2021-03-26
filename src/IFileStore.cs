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

        Task<bool> ReadFileAsync(string name, Stream desinationStream);

        Task<bool> FileExistsAsync(string name);


    }
}
