using System;
using System.Diagnostics;
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
/// <item>Every packet put in <see cref="Queue"/> before <see cref="Stop"/> is written — nothing is dropped.</item>
/// <item>Binary and ASCII logs contain the received bytes exactly as received.
///       Hex logs contain every received byte as two hex digits, 16 bytes per line.</item>
/// <item>The file is opened in <see cref="Start"/>, so an unusable path fails immediately.
///       A write failure later on is exposed through <see cref="Fault"/>.</item>
/// </list>
/// </remarks>
public class LogFileWriter
{
    private const int FlushIntervalMs = 500;
    private const int MaxPacketsPerBatch = 4096;
    private const int BytesPerHexLine = 16;
    private static readonly byte[] HexDigits = "0123456789ABCDEF"u8.ToArray();

    private readonly string _outputDirectory;
    private readonly LogFormat _format;
    private readonly LogFileNameTemplate _nameTemplate;
    private readonly string _portName;
    private Thread? _writerThread;
    private FileStream? _fileStream;
    private volatile bool _stopRequested;
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
    /// Gets the total number of received data bytes written to the log file so far.
    /// </summary>
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

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
    public string? CurrentFilePath { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LogFileWriter"/> class.
    /// </summary>
    /// <param name="outputDirectory">The directory where log files should be written.</param>
    /// <param name="format">The log data format (Binary, Hex, Ascii).</param>
    /// <param name="nameTemplate">The template engine for naming files.</param>
    /// <param name="portName">The COM port associated with this log file.</param>
    public LogFileWriter(string outputDirectory, LogFormat format, LogFileNameTemplate nameTemplate, string portName)
    {
        _outputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));
        _format = format;
        _nameTemplate = nameTemplate ?? throw new ArgumentNullException(nameof(nameTemplate));
        _portName = portName ?? throw new ArgumentNullException(nameof(portName));
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
        string fileName = _nameTemplate.Generate(_portName, DateTime.UtcNow);
        string path = Path.Combine(_outputDirectory, fileName);

        // Open synchronously so a failure (locked file, no permission, bad path) is reported to
        // the caller right away instead of silently ending the writer thread.
        _fileStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None, bufferSize: 65536);
        CurrentFilePath = path;
        _started = true;

        Interlocked.Exchange(ref _bytesWritten, 0);
        Volatile.Write(ref _fault, null);
        _hexColumn = 0;
        _stopRequested = false;
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
        var stream = _fileStream!;
        try
        {
            var sinceFlush = Stopwatch.StartNew();
            bool unflushed = false;

            while (true)
            {
                // Read the stop flag BEFORE draining. Stop() closes the queue before setting the
                // flag, so once it is seen, draining until empty writes every accepted packet and
                // the loop still ends even if the port keeps delivering data.
                bool stopping = _stopRequested;

                int written = DrainQueue(stream, stopping ? int.MaxValue : MaxPacketsPerBatch);
                if (written > 0) unflushed = true;

                if (stopping) break;

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

            // Terminate an incomplete hex line so the file ends cleanly.
            if (_format == LogFormat.Hex && _hexColumn != 0)
            {
                stream.WriteByte((byte)'\n');
            }

            stream.Flush();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _fault, ex);
            Debug.WriteLine($"LogFileWriter error ({_portName}): {ex}");
        }
        finally
        {
            try { stream.Dispose(); } catch { }
            _isRunning = false;
        }
    }

    private int DrainQueue(FileStream stream, int maxPackets)
    {
        int count = 0;
        while (count < maxPackets && Queue.TryRead(out var packet))
        {
            WritePacket(stream, packet.Data);
            Interlocked.Add(ref _bytesWritten, packet.Data.Length);
            count++;
        }
        return count;
    }

    private void WritePacket(FileStream stream, byte[] data)
    {
        if (data.Length == 0) return;

        if (_format == LogFormat.Hex)
        {
            WriteHex(stream, data);
        }
        else
        {
            // Binary and ASCII: the exact received bytes, nothing added, removed or replaced.
            stream.Write(data, 0, data.Length);
        }
    }

    /// <summary>
    /// Writes bytes as "48 65 6C ..." with a fixed 16 bytes per line. Line breaks do not depend
    /// on how the driver happened to split the incoming data.
    /// </summary>
    private void WriteHex(FileStream stream, byte[] data)
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
    }
}
