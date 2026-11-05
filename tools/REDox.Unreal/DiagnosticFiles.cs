// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace REDox.Unreal;

public static class DiagnosticFiles
{
    public static bool IsGzip(string path) => path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

    public static Stream OpenRead(string path)
    {
        var file = File.OpenRead(path);
        return IsGzip(path) ? new GZipStream(file, CompressionMode.Decompress) : file;
    }

    // The logical destination determines compression, even when writing a temporary file.
    public static Stream Create(string path, string destination)
    {
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        return IsGzip(destination) ? new GZipStream(file, CompressionLevel.Fastest) : file;
    }

    // Preserve the source and publish only after the gzip footer has been written.
    public static async Task CompressAsync(string source, string destination, CancellationToken cancellationToken,
        Action? progress = null)
    {
        if (!IsGzip(destination))
        {
            throw new ArgumentException("Compressed output must end in .gz.");
        }

        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = Create(temporary, destination))
            {
                var buffer = new byte[65536];
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    progress?.Invoke();
                }
            }

            File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}