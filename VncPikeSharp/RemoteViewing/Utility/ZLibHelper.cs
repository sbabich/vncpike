using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace RemoteViewing.Utility
{
    public class ZLibHelper
    {
        /*public static byte[] CompressData(byte[] uncompressedBytes)
        {
            // Compress it using Deflate (optimal)
            using (MemoryStream compressedStream = new MemoryStream())
            {
                // init
                DeflateStream deflateStream = new DeflateStream(compressedStream, CompressionLevel.Optimal, true);

                deflateStream.Write(uncompressedBytes, 0, uncompressedBytes.Length);
                deflateStream.Close();

                // Print some info
                long compressedFileSize = compressedStream.Length;

                return compressedStream.ToArray();
            }
        }

        public static byte[] DecompressData(byte[] contents, int size)
        {
            using (var output = new MemoryStream())
            {
                using (var compressStream = new MemoryStream(contents, 0, size))
                {
                    //compressStream.Position = 0;
                    using (var decompressor = new DeflateStream(compressStream, CompressionMode.Decompress))
                    {
                        decompressor.CopyTo(output);
                    }
                }

                output.Position = 0;
                return output.ToArray();
            }
        }*/

        public static byte[] CompressData(byte[] uncompressedBytes)
        {
            var compressed = Ionic.Zlib.ZlibStream.CompressBuffer(uncompressedBytes);
            return compressed;
        }

        public static byte[] DecompressData(byte[] input, int size)
        {
            byte[] arr = new byte[size];
            Array.Copy(input, arr, size);
            var decompressed = Ionic.Zlib.ZlibStream.UncompressBuffer(arr);
            return decompressed;
        }
    }
}
