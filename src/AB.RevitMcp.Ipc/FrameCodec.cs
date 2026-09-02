using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AB.RevitMcp.Contracts.Protocol;

namespace AB.RevitMcp.Ipc
{
    /// <summary>
    /// Wire framing for the bridge: a 4-byte little-endian unsigned length followed by that many
    /// bytes of UTF-8 JSON. Length prefixing (rather than newline delimiting) keeps the reader
    /// O(1) and makes an embedded newline in a Revit element name a non-issue.
    /// </summary>
    public static class FrameCodec
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>Reads one frame. Returns null on a clean end-of-stream (peer disconnected).</summary>
        public static async Task<string> ReadFrameAsync(Stream stream, CancellationToken ct)
        {
            byte[] header = new byte[4];
            if (!await ReadExactAsync(stream, header, 0, 4, ct).ConfigureAwait(false)) return null;

            long length = (uint)(header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24));
            if (length == 0) return string.Empty;
            if (length > IpcConstants.MaxFrameBytes)
            {
                throw new IOException("Frame length " + length + " exceeds the " +
                                      IpcConstants.MaxFrameBytes + " byte limit; stream is out of sync.");
            }

            byte[] payload = new byte[length];
            if (!await ReadExactAsync(stream, payload, 0, (int)length, ct).ConfigureAwait(false))
                throw new IOException("Stream ended mid-frame after " + length + " bytes were promised.");

            return Utf8.GetString(payload);
        }

        /// <summary>Writes one frame and flushes it.</summary>
        public static async Task WriteFrameAsync(Stream stream, string payload, CancellationToken ct)
        {
            byte[] body = Utf8.GetBytes(payload ?? string.Empty);
            if (body.Length > IpcConstants.MaxFrameBytes)
            {
                throw new IOException("Response of " + body.Length + " bytes exceeds the " +
                                      IpcConstants.MaxFrameBytes + " byte frame limit. " +
                                      "Reduce the page size (limit argument) and retry.");
            }

            byte[] buffer = new byte[4 + body.Length];
            buffer[0] = (byte)(body.Length & 0xFF);
            buffer[1] = (byte)((body.Length >> 8) & 0xFF);
            buffer[2] = (byte)((body.Length >> 16) & 0xFF);
            buffer[3] = (byte)((body.Length >> 24) & 0xFF);
            Buffer.BlockCopy(body, 0, buffer, 4, body.Length);

            await stream.WriteAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer, offset + read, count - read, ct).ConfigureAwait(false);
                if (n <= 0) return read == 0 ? false : throw new IOException("Unexpected end of stream mid-frame.");
                read += n;
            }
            return true;
        }
    }
}
