using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Caladesi.LanguageServer.Protocol;

/// <summary>Reads <c>Content-Length</c> framed messages. Malformed header blocks are logged and skipped, never fatal.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The BufferedStream holds no unmanaged resources and the wrapped stream is owned (and disposed) by the caller.")]
internal sealed class MessageReader(Stream stream)
{
    private const int MaxMessageBytes = 256 * 1024 * 1024;
    private const int MaxHeaderLine = 16 * 1024;
    private readonly BufferedStream _stream = new(stream, 64 * 1024);

    /// <summary>The next message body, or null at end of stream.</summary>
    public byte[]? ReadMessage()
    {
        while (true)
        {
            int contentLength = -1;
            bool sawHeader = false;
            while (true)
            {
                string? line = ReadLine();
                if (line == null)
                {
                    return null;
                }

                if (line.Length == 0)
                {
                    if (!sawHeader)
                    {
                        continue;
                    }

                    break;
                }
                sawHeader = true;
                int colon = line.IndexOf(':');
                if (colon < 0)
                {
                    Log.Write("ignoring malformed header line");
                    continue;
                }
                ReadOnlySpan<char> name = line.AsSpan(0, colon).Trim();
                ReadOnlySpan<char> value = line.AsSpan(colon + 1).Trim();
                if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                {
                    contentLength = n;
                }
                else
                {
                    Log.Write("invalid Content-Length header");
                    contentLength = -1;
                }
            }

            if (contentLength < 0)
            {
                Log.Write("message without a valid Content-Length, skipping");
                continue;
            }
            if (contentLength > MaxMessageBytes)
            {
                Log.Write($"message of {contentLength} bytes is too large, skipping");
                if (!Skip(contentLength))
                {
                    return null;
                }

                continue;
            }
            byte[] body = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = _stream.Read(body, read, contentLength - read);
                if (n <= 0)
                {
                    return null;
                }

                read += n;
            }
            return body;
        }
    }

    private bool Skip(long count)
    {
        byte[] buffer = new byte[8192];
        while (count > 0)
        {
            int n = _stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n <= 0)
            {
                return false;
            }

            count -= n;
        }
        return true;
    }

    private string? ReadLine()
    {
        StringBuilder sb = new();
        while (true)
        {
            int b = _stream.ReadByte();
            if (b < 0)
            {
                return sb.Length == 0 ? null : sb.ToString();
            }

            if (b == '\n')
            {
                break;
            }

            if (b == '\r')
            {
                continue;
            }

            if (sb.Length < MaxHeaderLine)
            {
                _ = sb.Append((char)b);
            }
        }
        return sb.ToString();
    }
}

internal sealed class MessageWriter(Stream stream)
{
    private readonly Lock _gate = new();
    private bool _broken;

    public void Write(byte[] payload)
    {
        lock (_gate)
        {
            if (_broken)
            {
                return;
            }

            try
            {
                byte[] header = Encoding.ASCII.GetBytes("Content-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
                stream.Write(header, 0, header.Length);
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                _broken = true;
                Log.Write("output stream closed: " + ex.Message);
            }
        }
    }
}

internal static class Log
{
    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try { Console.Error.WriteLine("[cala-ls] " + message); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }
}
