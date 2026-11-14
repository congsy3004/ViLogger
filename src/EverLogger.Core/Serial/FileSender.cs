using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EverLogger.Core.Serial;

/// <summary>
/// Specifies how a selected file is interpreted when transmitting over a serial port.
/// </summary>
public enum FileSendMode
{
    Raw,
    Text,
    Hex
}

/// <summary>
/// Specifies line-ending handling when sending a file in <see cref="FileSendMode.Text"/> mode.
/// </summary>
public enum FileLineEnding
{
    AsIs,
    CRLF,
    LF,
    CR
}

/// <summary>
/// Options controlling a file transmission via <see cref="FileSender"/>.
/// </summary>
public sealed record FileSendOptions
{
    public string FilePath { get; init; } = string.Empty;
    public FileSendMode Mode { get; init; } = FileSendMode.Raw;
    public FileLineEnding LineEnding { get; init; } = FileLineEnding.AsIs;
    public int ChunkSize { get; init; } = 256;
    public int DelayMs { get; init; } = 0;
    public int BaudRate { get; init; } = 115200;
}

/// <summary>
/// Summary returned when a file transmission finishes.
/// </summary>
public readonly record struct FileSendResult(long WireBytesSent, long ProgressTotal, TimeSpan Elapsed);

/// <summary>
/// Streams a file to a serial write callback in <see cref="FileSendMode.Raw"/>,
/// <see cref="FileSendMode.Text"/>, or <see cref="FileSendMode.Hex"/> mode.
/// </summary>
public static class FileSender
{
    public const int MinChunkSize = 1;
    public const int MaxChunkSize = 4096;
    public const int MinDelayMs = 0;
    public const int MaxDelayMs = 60_000;

    private const int ReadBufferSize = 8192;
    private const int TextFlushBufferSize = 4096;

    /// <summary>
    /// Validates and streams the file configured in <paramref name="options"/> on a background thread.
    /// </summary>
    public static Task<FileSendResult> SendAsync(
        FileSendOptions options,
        Action<byte[], int, int> writeBytes,
        Action<int, long, long>? onProgress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writeBytes);

        if (string.IsNullOrWhiteSpace(options.FilePath))
            throw new ArgumentException("File path must not be empty.", nameof(options));
        if (options.ChunkSize < MinChunkSize || options.ChunkSize > MaxChunkSize)
            throw new ArgumentOutOfRangeException(nameof(options), $"Chunk size must be between {MinChunkSize} and {MaxChunkSize} bytes.");
        if (options.DelayMs < MinDelayMs || options.DelayMs > MaxDelayMs)
            throw new ArgumentOutOfRangeException(nameof(options), $"Delay must be between {MinDelayMs} and {MaxDelayMs} ms.");

        return Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();

            using var fs = new FileStream(
                options.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 65536,
                FileOptions.SequentialScan);

            if (fs.Length <= 0)
                throw new InvalidDataException("The selected file is empty (0 bytes).");

            // SerialPortConnection uses a 500 ms WriteTimeout and a 4 KB write buffer.
            // Keep each individual write small enough to drain within ~200 ms at the configured baud rate
            // so slow links (e.g. 9600 or 300 baud) never trigger false write timeouts or slow cancellation.
            int effectiveBaud = Math.Max(options.BaudRate, 50);
            int maxWireSlice = Math.Clamp((int)(effectiveBaud / 10.0 * 0.20), 1, MaxChunkSize);

            var (wireSent, progressTotal) = options.Mode switch
            {
                FileSendMode.Raw => SendRaw(fs, options, maxWireSlice, writeBytes, onProgress, ct),
                FileSendMode.Text => SendText(fs, options, maxWireSlice, writeBytes, onProgress, ct),
                FileSendMode.Hex => SendHex(fs, options, maxWireSlice, writeBytes, onProgress, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(options), $"Unsupported send mode: {options.Mode}")
            };

            return new FileSendResult(wireSent, progressTotal, sw.Elapsed);
        }, ct);
    }

    private static (long WireSent, long ProgressTotal) SendRaw(
        FileStream fs,
        FileSendOptions options,
        int maxWireSlice,
        Action<byte[], int, int> writeBytes,
        Action<int, long, long>? onProgress,
        CancellationToken ct)
    {
        long total = fs.Length;
        long wireSent = 0;
        byte[] buffer = new byte[options.ChunkSize];
        bool sentAny = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int bytesRead = ReadUpTo(fs, buffer, buffer.Length);
            if (bytesRead <= 0) break;

            if (sentAny)
            {
                WaitDelay(options.DelayMs, ct);
            }

            WriteSliced(buffer, 0, bytesRead, maxWireSlice, writeBytes, total, ref wireSent, useWireSentAsProgress: true, progressCurrentOverride: 0, onProgress, ct);
            sentAny = true;
        }

        return (wireSent, total);
    }

    private static (long WireSent, long ProgressTotal) SendText(
        FileStream fs,
        FileSendOptions options,
        int maxWireSlice,
        Action<byte[], int, int> writeBytes,
        Action<int, long, long>? onProgress,
        CancellationToken ct)
    {
        long fileLength = fs.Length;
        long wireSent = 0;
        byte[] readBuf = new byte[ReadBufferSize];
        byte[] outBuf = new byte[TextFlushBufferSize + 2];
        int outLen = 0;
        long sourceBytesConsumed = 0;

        bool pendingCr = false;
        bool hasLineContent = false;
        bool needInterLineDelay = false;

        void FlushOut(long currentSourceBytes)
        {
            if (outLen <= 0) return;

            if (needInterLineDelay)
            {
                WaitDelay(options.DelayMs, ct);
                needInterLineDelay = false;
            }

            WriteSliced(
                outBuf,
                0,
                outLen,
                maxWireSlice,
                writeBytes,
                fileLength,
                ref wireSent,
                useWireSentAsProgress: false,
                progressCurrentOverride: Math.Min(currentSourceBytes, fileLength),
                onProgress,
                ct);
            outLen = 0;
        }

        void AppendByte(byte b, long currentSourceBytes)
        {
            outBuf[outLen++] = b;
            hasLineContent = true;
            if (outLen >= TextFlushBufferSize)
            {
                FlushOut(currentSourceBytes);
            }
        }

        void AppendEnding(FileLineEnding chosen, ReadOnlySpan<byte> originalEol)
        {
            ReadOnlySpan<byte> eol = chosen switch
            {
                FileLineEnding.AsIs => originalEol,
                FileLineEnding.CRLF => "\r\n"u8,
                FileLineEnding.LF => "\n"u8,
                FileLineEnding.CR => "\r"u8,
                _ => originalEol
            };

            for (int i = 0; i < eol.Length; i++)
            {
                outBuf[outLen++] = eol[i];
            }
        }

        void FinishLine(ReadOnlySpan<byte> originalEol, long currentSourceBytes)
        {
            AppendEnding(options.LineEnding, originalEol);
            FlushOut(currentSourceBytes);
            hasLineContent = false;
            needInterLineDelay = true;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int bytesRead = fs.Read(readBuf, 0, readBuf.Length);
            if (bytesRead <= 0) break;

            for (int i = 0; i < bytesRead; i++)
            {
                byte b = readBuf[i];
                sourceBytesConsumed++;

                if (pendingCr)
                {
                    pendingCr = false;
                    if (b == (byte)'\n')
                    {
                        FinishLine("\r\n"u8, sourceBytesConsumed);
                        continue;
                    }

                    // The previous CR stood on its own; finish that line before handling the current byte.
                    FinishLine("\r"u8, sourceBytesConsumed - 1);
                }

                if (b == (byte)'\r')
                {
                    // Defer decision until the next byte (even across read buffer boundaries).
                    pendingCr = true;
                }
                else if (b == (byte)'\n')
                {
                    FinishLine("\n"u8, sourceBytesConsumed);
                }
                else
                {
                    AppendByte(b, sourceBytesConsumed);
                }
            }
        }

        if (pendingCr)
        {
            FinishLine("\r"u8, fileLength);
        }
        else if (hasLineContent)
        {
            if (options.LineEnding != FileLineEnding.AsIs)
            {
                AppendEnding(options.LineEnding, ReadOnlySpan<byte>.Empty);
            }
            FlushOut(fileLength);
        }

        return (wireSent, fileLength);
    }

    private static (long WireSent, long ProgressTotal) SendHex(
        FileStream fs,
        FileSendOptions options,
        int maxWireSlice,
        Action<byte[], int, int> writeBytes,
        Action<int, long, long>? onProgress,
        CancellationToken ct)
    {
        // Pass 1: validate syntax and count decoded bytes so invalid files send 0 bytes.
        long totalDecoded = ParseHexStream(fs, emitByte: null, ct);
        if (totalDecoded <= 0)
            throw new FormatException("Hex file contains no hexadecimal byte values.");

        // Pass 2: stream decoded bytes in ChunkSize packets with inter-chunk pacing.
        fs.Position = 0;
        byte[] chunk = new byte[options.ChunkSize];
        int chunkCount = 0;
        bool sentAny = false;
        long wireSent = 0;

        void Emit(byte value)
        {
            chunk[chunkCount++] = value;
            if (chunkCount == chunk.Length)
            {
                if (sentAny)
                {
                    WaitDelay(options.DelayMs, ct);
                }

                WriteSliced(chunk, 0, chunkCount, maxWireSlice, writeBytes, totalDecoded, ref wireSent, useWireSentAsProgress: true, progressCurrentOverride: 0, onProgress, ct);
                chunkCount = 0;
                sentAny = true;
            }
        }

        ParseHexStream(fs, Emit, ct);

        if (chunkCount > 0)
        {
            if (sentAny)
            {
                WaitDelay(options.DelayMs, ct);
            }

            WriteSliced(chunk, 0, chunkCount, maxWireSlice, writeBytes, totalDecoded, ref wireSent, useWireSentAsProgress: true, progressCurrentOverride: 0, onProgress, ct);
        }

        return (wireSent, totalDecoded);
    }

    private static long ParseHexStream(FileStream fs, Action<byte>? emitByte, CancellationToken ct)
    {
        byte[] readBuf = new byte[ReadBufferSize];
        long decodedCount = 0;
        int lineNumber = 1;

        bool seenAnyNonWhitespace = false;
        bool inComment = false;
        bool pendingSlash = false;

        bool inToken = false;
        bool tokenJustZero = false;
        bool requireHexAfterPrefix = false;
        int nibbleCount = 0;
        byte pendingNibble = 0;

        long filePos = 0;
        int bomMatched = 0;

        void EndToken()
        {
            if (requireHexAfterPrefix)
                throw new FormatException($"Incomplete '0x' hex token on line {lineNumber}.");
            if (nibbleCount != 0)
                throw new FormatException($"Incomplete hex byte (odd number of hex digits) on line {lineNumber}.");

            inToken = false;
            tokenJustZero = false;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int bytesRead = fs.Read(readBuf, 0, readBuf.Length);
            if (bytesRead <= 0) break;

            for (int i = 0; i < bytesRead; i++)
            {
                byte b = readBuf[i];
                long currentIndex = filePos++;

                // Skip UTF-8 BOM (EF BB BF) only at the very start of the stream.
                if (currentIndex == 0 && b == 0xEF) { bomMatched = 1; continue; }
                if (bomMatched == 1)
                {
                    if (b == 0xBB) { bomMatched = 2; continue; }
                    throw new FormatException("Invalid non-ASCII byte at byte offset 0.");
                }
                if (bomMatched == 2)
                {
                    bomMatched = 0;
                    if (b == 0xBF) continue;
                    throw new FormatException("Invalid non-ASCII byte at byte offset 1.");
                }

                if (inComment)
                {
                    if (b == '\n')
                    {
                        inComment = false;
                        lineNumber++;
                    }
                    continue;
                }

                if (pendingSlash)
                {
                    pendingSlash = false;
                    if (b == '/')
                    {
                        EndToken();
                        inComment = true;
                        continue;
                    }

                    throw new FormatException($"Unexpected '/' on line {lineNumber}. Use '//' or '#' for comments.");
                }

                if (b == '\n')
                {
                    EndToken();
                    lineNumber++;
                    continue;
                }

                if (b == ' ' || b == '\t' || b == '\r' || b == ',' || b == ';' || b == '-')
                {
                    EndToken();
                    continue;
                }

                seenAnyNonWhitespace = true;

                if (b == '#')
                {
                    EndToken();
                    inComment = true;
                    continue;
                }

                if (b == '/')
                {
                    pendingSlash = true;
                    continue;
                }

                if ((b == 'x' || b == 'X') && tokenJustZero)
                {
                    tokenJustZero = false;
                    nibbleCount = 0;
                    requireHexAfterPrefix = true;
                    continue;
                }

                int nibble = HexVal(b);
                if (nibble >= 0)
                {
                    if (!inToken)
                    {
                        inToken = true;
                        tokenJustZero = b == '0';
                    }
                    else
                    {
                        tokenJustZero = false;
                    }

                    requireHexAfterPrefix = false;

                    if (nibbleCount == 0)
                    {
                        pendingNibble = (byte)nibble;
                        nibbleCount = 1;
                    }
                    else
                    {
                        byte combined = (byte)((pendingNibble << 4) | nibble);
                        nibbleCount = 0;
                        decodedCount++;
                        emitByte?.Invoke(combined);
                    }
                    continue;
                }

                if (lineNumber == 1 && b == ':')
                {
                    throw new FormatException("Line 1 starts with ':' (Intel HEX record). Use Text mode to send ASCII Intel HEX lines, or convert to .bin for Raw mode.");
                }

                if (lineNumber == 1 && b == 'S')
                {
                    throw new FormatException("Line 1 contains 'S' (possible Motorola S-Record). Use Text mode to send ASCII S-Records, or convert to .bin for Raw mode.");
                }

                string chDisplay = b >= 0x20 && b <= 0x7E ? $"'{(char)b}'" : $"0x{b:X2}";
                throw new FormatException($"Invalid hex character {chDisplay} on line {lineNumber}.");
            }
        }

        if (bomMatched != 0)
            throw new FormatException("Invalid non-ASCII byte at byte offset 0.");
        if (pendingSlash)
            throw new FormatException($"Unexpected '/' on line {lineNumber}. Use '//' or '#' for comments.");

        EndToken();

        if (!seenAnyNonWhitespace && decodedCount == 0)
            throw new FormatException("Hex file contains no hexadecimal byte values.");

        return decodedCount;
    }

    private static int HexVal(byte b)
    {
        if (b >= '0' && b <= '9') return b - '0';
        if (b >= 'A' && b <= 'F') return b - 'A' + 10;
        if (b >= 'a' && b <= 'f') return b - 'a' + 10;
        return -1;
    }

    private static int ReadUpTo(FileStream fs, byte[] buffer, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int n = fs.Read(buffer, totalRead, count - totalRead);
            if (n <= 0) break;
            totalRead += n;
        }
        return totalRead;
    }

    private static void WriteSliced(
        byte[] buffer,
        int offset,
        int count,
        int maxWireSlice,
        Action<byte[], int, int> writeBytes,
        long progressTotal,
        ref long wireSent,
        bool useWireSentAsProgress,
        long progressCurrentOverride,
        Action<int, long, long>? onProgress,
        CancellationToken ct)
    {
        int remaining = count;
        int pos = offset;

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int slice = Math.Min(remaining, maxWireSlice);
            writeBytes(buffer, pos, slice);
            pos += slice;
            remaining -= slice;
            wireSent += slice;

            long progressCurrent = useWireSentAsProgress ? wireSent : progressCurrentOverride;
            onProgress?.Invoke(slice, progressCurrent, progressTotal);
        }
    }

    private static void WaitDelay(int delayMs, CancellationToken ct)
    {
        if (delayMs <= 0) return;
        if (ct.WaitHandle.WaitOne(delayMs))
        {
            ct.ThrowIfCancellationRequested();
        }
    }
}
