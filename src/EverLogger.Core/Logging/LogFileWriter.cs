using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using EverLogger.Core.Data;

namespace EverLogger.Core.Logging;

/// <summary>
/// High-performance file writer that consumes data from a queue and writes it to disk.
/// </summary>
/// <remarks>
/// Accuracy rules:
/// <list type="bullet">
/// <item>Every packet put in <see cref="Queue"/> before <see cref="Stop"/> (unless stopped by a
///       configured file-size limit) is written — nothing is dropped.</item>
/// <item>Binary and ASCII logs contain the received bytes exactly as received.
///       Hex logs contain every received byte as two hex digits, 16 bytes per line.</item>
/// <item>The file is opened in <see cref="Start"/>, so an unusable path fails immediately.
///       A write failure later on is exposed through <see cref="Fault"/>.</item>
/// </list>
/// </remarks>
public class LogFileWriter
{
    /// <summary>
    /// Minimum non-zero file size limit in bytes (ensures any format can write at least one line/byte).
    /// </summary>
    public const long MinFileSizeBytes = 16;

    private const int FlushIntervalMs = 500;
    private const int MaxPacketsPerBatch = 4096;
    private const int BytesPerHexLine = 16;
    private static readonly byte[] HexDigits = "0123456789ABCDEF"u8.ToArray();

    private readonly string _outputDirectory;
    private readonly LogFormat _format;
    private readonly LogFileNameTemplate _nameTemplate;
    private readonly string _portName;
    private readonly long _maxFileSizeBytes;
    private readonly LogLimitAction _limitAction;
    private readonly ConcurrentQueue<LogWriterEvent> _events = new();
    private Thread? _writerThread;
    private FileStream? _fileStream;
    private string? _currentFilePath;
    private string _baseDirectory = string.Empty;
    private string _baseFileNameWithoutExtension = string.Empty;
    private string _baseExtension = string.Empty;
    private int _partNumber = 1;
    private long _currentFileBytes;
    private volatile bool _stopRequested;
    private volatile bool _stoppedByLimit;
    private volatile bool _isRunning;
    private bool _started;
    private long _bytesWritten;
    private Exception? _fault;

    // Writer-thread-only state for Hex formatting
    private int _hexColumn;
    private byte[] _hexScratch = Array.Empty<byte>();

    /// <summary>
    /// The thread-safe queue used to ingest data packets.
    /// </summary>
    public ConcurrentDataQueue Queue { get; } = new();

    /// <summary>
    /// Gets the total number of received data bytes written across all log parts so far.
    /// </summary>
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>
    /// Gets the configured maximum size per log file in bytes (0 = unlimited).
    /// </summary>
    public long MaxFileSizeBytes => _maxFileSizeBytes;

    /// <summary>
    /// Gets the action performed when a log file reaches <see cref="MaxFileSizeBytes"/>.
    /// </summary>
    public LogLimitAction LimitAction => _limitAction;

    /// <summary>
    /// Gets the number of bytes written in the active log file part.
    /// </summary>
    public long CurrentFileBytes => Interlocked.Read(ref _currentFileBytes);

    /// <summary>
    /// Gets the 1-based part number of the current log file.
    /// </summary>
    public int CurrentPartNumber => Volatile.Read(ref _partNumber);

    /// <summary>
    /// Gets a value indicating whether the writer stopped automatically due to reaching the size limit.
    /// </summary>
    public bool StoppedByLimit => _stoppedByLimit;

    /// <summary>
    /// Gets a value indicating whether the writer thread is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets the error that stopped the writer, or null if it is healthy.
    /// Once set, nothing more is written to the file.
    /// </summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>
    /// Gets the absolute path of the current log file.
    /// </summary>
    public string? CurrentFilePath => Volatile.Read(ref _currentFilePath);

    /// <summary>
    /// Initializes a new instance of the <see cref="LogFileWriter"/> class.
    /// </summary>
    /// <param name="outputDirectory">The directory where log files should be written.</param>
    /// <param name="format">The log data format (Binary, Hex, Ascii).</param>
    /// <param name="nameTemplate">The template engine for naming files.</param>
    /// <param name="portName">The COM port associated with this log file.</param>
    /// <param name="maxFileSizeBytes">Maximum file size in bytes (0 = unlimited, or >= 16 bytes).</param>
    /// <param name="limitAction">Action taken when the size limit is reached.</param>
    public LogFileWriter(
        string outputDirectory,
        LogFormat format,
        LogFileNameTemplate nameTemplate,
        string portName,
        long maxFileSizeBytes = 0,
        LogLimitAction limitAction = LogLimitAction.Rotate)
    {
        _outputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));
        _format = format;
        _nameTemplate = nameTemplate ?? throw new ArgumentNullException(nameof(nameTemplate));
        _portName = portName ?? throw new ArgumentNullException(nameof(portName));
        if (maxFileSizeBytes < 0 || (maxFileSizeBytes > 0 && maxFileSizeBytes < MinFileSizeBytes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFileSizeBytes),
                $"maxFileSizeBytes must be 0 (unlimited) or at least {MinFileSizeBytes} bytes.");
        }

        _maxFileSizeBytes = maxFileSizeBytes;
        _limitAction = limitAction;
    }

    /// <summary>
    /// Attempts to dequeue the next lifecycle event (rotation or limit-reached stop).
    /// </summary>
    public bool TryDequeueEvent([MaybeNullWhen(false)] out LogWriterEvent writerEvent)
    {
        return _events.TryDequeue(out writerEvent);
    }

    /// <summary>
    /// Opens the log file and starts the background writer thread.
    /// A writer is single-use: after <see cref="Stop"/>, create a new instance to log again.
    /// </summary>
    /// <exception cref="IOException">The file could not be created or opened.</exception>
    /// <exception cref="UnauthorizedAccessException">No permission to write the file.</exception>
    /// <exception cref="InvalidOperationException">The writer was already started once.</exception>
    public void Start()
    {
        if (_started) throw new InvalidOperationException("A LogFileWriter can only be started once.");

        Directory.CreateDirectory(_outputDirectory);
        string fileName = _nameTemplate.Generate(_portName, DateTime.Now);
        string path = Path.Combine(_outputDirectory, fileName);

        // Open synchronously so a failure (locked file, no permission, bad path) is reported to
        // the caller right away instead of silently ending the writer thread.
        _fileStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None, bufferSize: 65536);
        _baseDirectory = Path.GetDirectoryName(path) ?? _outputDirectory;
        _baseFileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        _baseExtension = Path.GetExtension(path);
        Volatile.Write(ref _currentFilePath, path);
        Volatile.Write(ref _partNumber, 1);
        Interlocked.Exchange(ref _currentFileBytes, _fileStream.Length);
        _started = true;

        Interlocked.Exchange(ref _bytesWritten, 0);
        Volatile.Write(ref _fault, null);
        _hexColumn = 0;
        _stopRequested = false;
        _stoppedByLimit = false;
        _isRunning = true;

        _writerThread = new Thread(WriterLoop)
        {
            // Foreground thread: the process cannot exit until queued data is on disk.
            IsBackground = false,
            Name = $"LogWriter_{_portName}"
        };
        _writerThread.Start();
    }

    /// <summary>
    /// Stops the writer after everything already queued has been written, then closes the file.
    /// </summary>
    public void Stop()
    {
        var thread = _writerThread;
        if (thread == null) return;

        // Close the queue first: from now on nothing new is accepted, so "everything accepted"
        // is a fixed set that the writer drains completely before closing the file.
        Queue.Complete();
        _stopRequested = true;

        // Normally returns within milliseconds. If the disk is very slow the thread keeps
        // draining on its own; being a foreground thread, it still finishes before exit.
        thread.Join(TimeSpan.FromSeconds(10));
        _writerThread = null;
    }

    private void WriterLoop()
    {
        FileStream? stream = _fileStream;
        try
        {
            if (stream == null) return;

            if (_maxFileSizeBytes > 0 &&
                _limitAction == LogLimitAction.Stop &&
                Interlocked.Read(ref _currentFileBytes) >= _maxFileSizeBytes)
            {
                TriggerLimitStop(stream);
            }
            else
            {
                var sinceFlush = Stopwatch.StartNew();
                bool unflushed = false;

                while (!_stoppedByLimit)
                {
                    // Read the stop flag BEFORE draining. Stop() closes the queue before setting the
                    // flag, so once it is seen, draining until empty writes every accepted packet and
                    // the loop still ends even if the port keeps delivering data.
                    bool stopping = _stopRequested;

                    int written = DrainQueue(ref stream, stopping ? int.MaxValue : MaxPacketsPerBatch);
                    if (written > 0) unflushed = true;

                    if (_stoppedByLimit || stopping) break;

                    if (unflushed && sinceFlush.ElapsedMilliseconds >= FlushIntervalMs)
                    {
                        stream.Flush();
                        unflushed = false;
                        sinceFlush.Restart();
                    }

                    if (written == 0)
                    {
                        Thread.Sleep(10);
                    }
                }

                if (!_stoppedByLimit)
                {
                    // Terminate an incomplete hex line so the file ends cleanly.
                    if (_format == LogFormat.Hex && _hexColumn != 0)
                    {
                        stream.WriteByte((byte)'\n');
                        Interlocked.Increment(ref _currentFileBytes);
                    }

                    stream.Flush();
                }
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _fault, ex);
            Queue.Complete();
            Debug.WriteLine($"LogFileWriter error ({_portName}): {ex}");
        }
        finally
        {
            try { stream?.Dispose(); } catch { }
            _isRunning = false;
        }
    }

    private int DrainQueue(ref FileStream stream, int maxPackets)
    {
        int count = 0;
        while (count < maxPackets && !_stoppedByLimit && Queue.TryRead(out var packet))
        {
            WritePacket(ref stream, packet.Data);
            count++;
        }
        return count;
    }

    private void WritePacket(ref FileStream stream, byte[] data)
    {
        if (data.Length == 0) return;

        if (_maxFileSizeBytes == 0)
        {
            if (_format == LogFormat.Hex)
            {
                int hexBytes = WriteHex(stream, data);
                Interlocked.Add(ref _currentFileBytes, hexBytes);
            }
            else
            {
                // Binary and ASCII: the exact received bytes, nothing added, removed or replaced.
                stream.Write(data, 0, data.Length);
                Interlocked.Add(ref _currentFileBytes, data.Length);
            }

            Interlocked.Add(ref _bytesWritten, data.Length);
            return;
        }

        if (_format == LogFormat.Hex)
        {
            WriteHexWithLimit(ref stream, data);
        }
        else
        {
            WriteRawWithLimit(ref stream, data);
        }
    }

    private void WriteRawWithLimit(ref FileStream stream, byte[] data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            long currentBytes = Interlocked.Read(ref _currentFileBytes);
            long remaining = _maxFileSizeBytes - currentBytes;

            if (remaining <= 0)
            {
                if (_limitAction == LogLimitAction.Stop)
                {
                    TriggerLimitStop(stream);
                    return;
                }

                RotateStream(ref stream);
                continue;
            }

            int toWrite = (int)Math.Min(data.Length - offset, remaining);
            stream.Write(data, offset, toWrite);
            currentBytes = Interlocked.Add(ref _currentFileBytes, toWrite);
            Interlocked.Add(ref _bytesWritten, toWrite);
            offset += toWrite;

            if (currentBytes >= _maxFileSizeBytes && _limitAction == LogLimitAction.Stop)
            {
                TriggerLimitStop(stream);
                return;
            }
        }
    }

    private void WriteHexWithLimit(ref FileStream stream, byte[] data)
    {
        int desiredScratch = Math.Max(Math.Min(data.Length * 4, 32768), 4096);
        if (_hexScratch.Length < desiredScratch)
        {
            _hexScratch = new byte[desiredScratch];
        }

        int n = 0;
        int stagedRawBytes = 0;
        long currentBytes = Interlocked.Read(ref _currentFileBytes);

        foreach (byte b in data)
        {
            int sep = _hexColumn > 0 ? 1 : 0;
            bool endsLine = (_hexColumn + 1) == BytesPerHexLine;
            int outLen = sep + 2 + (endsLine ? 1 : 0);
            // Reserve 1 byte when mid-line so closing the file with '\n' never exceeds the limit.
            int trailingReserve = endsLine ? 0 : 1;

            if (currentBytes + outLen + trailingReserve > _maxFileSizeBytes)
            {
                FlushHexScratch(stream, ref n, currentBytes, ref stagedRawBytes);

                if (_limitAction == LogLimitAction.Stop)
                {
                    TriggerLimitStop(stream);
                    return;
                }

                RotateStream(ref stream);
                currentBytes = 0;
                sep = 0;
                endsLine = false;
                outLen = 2;
            }

            if (n + outLen > _hexScratch.Length)
            {
                FlushHexScratch(stream, ref n, currentBytes, ref stagedRawBytes);
            }

            if (sep > 0) _hexScratch[n++] = (byte)' ';
            _hexScratch[n++] = HexDigits[b >> 4];
            _hexScratch[n++] = HexDigits[b & 0x0F];

            if (++_hexColumn == BytesPerHexLine)
            {
                _hexScratch[n++] = (byte)'\n';
                _hexColumn = 0;
            }

            currentBytes += outLen;
            stagedRawBytes++;

            if (currentBytes >= _maxFileSizeBytes && _limitAction == LogLimitAction.Stop)
            {
                FlushHexScratch(stream, ref n, currentBytes, ref stagedRawBytes);
                TriggerLimitStop(stream);
                return;
            }
        }

        FlushHexScratch(stream, ref n, currentBytes, ref stagedRawBytes);
    }

    private void FlushHexScratch(FileStream stream, ref int scratchLen, long currentBytes, ref int stagedRawBytes)
    {
        if (scratchLen <= 0) return;

        stream.Write(_hexScratch, 0, scratchLen);
        scratchLen = 0;
        Interlocked.Exchange(ref _currentFileBytes, currentBytes);
        if (stagedRawBytes > 0)
        {
            Interlocked.Add(ref _bytesWritten, stagedRawBytes);
            stagedRawBytes = 0;
        }
    }

    private void RotateStream(ref FileStream stream)
    {
        if (_format == LogFormat.Hex && _hexColumn != 0)
        {
            stream.WriteByte((byte)'\n');
            Interlocked.Increment(ref _currentFileBytes);
            _hexColumn = 0;
        }

        stream.Flush();
        long completedBytes = Interlocked.Read(ref _currentFileBytes);
        string completedPath = Volatile.Read(ref _currentFilePath) ?? string.Empty;
        int completedPart = Volatile.Read(ref _partNumber);

        stream.Dispose();

        FileStream newStream = OpenNextPartStream(completedPart, out string newPath, out int newPart);
        stream = newStream;
        _fileStream = newStream;
        Volatile.Write(ref _currentFilePath, newPath);
        Volatile.Write(ref _partNumber, newPart);
        Interlocked.Exchange(ref _currentFileBytes, 0);
        _hexColumn = 0;

        _events.Enqueue(new LogWriterEvent(
            LogWriterEventKind.Rotated,
            _portName,
            completedPath,
            completedBytes,
            _maxFileSizeBytes,
            newPath,
            completedPart,
            newPart,
            DateTime.Now));
    }

    private FileStream OpenNextPartStream(int completedPart, out string newPath, out int newPart)
    {
        const int maxAttempts = 100_000;
        int candidatePart = completedPart + 1;

        for (int attempt = 0; attempt < maxAttempts; attempt++, candidatePart++)
        {
            string candidatePath = Path.Combine(
                _baseDirectory,
                $"{_baseFileNameWithoutExtension}_part{candidatePart}{_baseExtension}");

            if (File.Exists(candidatePath))
            {
                continue;
            }

            try
            {
                var newStream = new FileStream(
                    candidatePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 65536);

                newPath = candidatePath;
                newPart = candidatePart;
                return newStream;
            }
            catch (IOException) when (File.Exists(candidatePath))
            {
                // Skip part numbers created concurrently between Exists and CreateNew.
            }
        }

        throw new IOException($"Could not allocate a unique log part file after {maxAttempts} attempts.");
    }

    private void TriggerLimitStop(FileStream stream)
    {
        if (_format == LogFormat.Hex && _hexColumn != 0)
        {
            stream.WriteByte((byte)'\n');
            Interlocked.Increment(ref _currentFileBytes);
            _hexColumn = 0;
        }

        stream.Flush();
        long completedBytes = Interlocked.Read(ref _currentFileBytes);
        string completedPath = Volatile.Read(ref _currentFilePath) ?? string.Empty;
        int completedPart = Volatile.Read(ref _partNumber);

        Queue.Complete();
        _stoppedByLimit = true;

        _events.Enqueue(new LogWriterEvent(
            LogWriterEventKind.LimitReachedStopped,
            _portName,
            completedPath,
            completedBytes,
            _maxFileSizeBytes,
            null,
            completedPart,
            null,
            DateTime.Now));
    }

    /// <summary>
    /// Writes bytes as "48 65 6C ..." with a fixed 16 bytes per line. Line breaks do not depend
    /// on how the driver happened to split the incoming data.
    /// </summary>
    private int WriteHex(FileStream stream, byte[] data)
    {
        // Worst case per byte: separator + 2 digits + newline.
        int needed = data.Length * 4;
        if (_hexScratch.Length < needed)
        {
            _hexScratch = new byte[Math.Max(needed, 4096)];
        }

        int n = 0;
        foreach (byte b in data)
        {
            if (_hexColumn > 0) _hexScratch[n++] = (byte)' ';
            _hexScratch[n++] = HexDigits[b >> 4];
            _hexScratch[n++] = HexDigits[b & 0x0F];

            if (++_hexColumn == BytesPerHexLine)
            {
                _hexScratch[n++] = (byte)'\n';
                _hexColumn = 0;
            }
        }

        stream.Write(_hexScratch, 0, n);
        return n;
    }
}
