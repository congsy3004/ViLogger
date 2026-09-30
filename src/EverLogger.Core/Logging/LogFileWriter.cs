using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EverLogger.Core.Data;

namespace EverLogger.Core.Logging;

/// <summary>
/// High-performance file writer that consumes data from a queue and writes it to disk.
/// </summary>
public class LogFileWriter
{
    private readonly string _outputDirectory;
    private readonly LogFormat _format;
    private readonly LogFileNameTemplate _nameTemplate;
    private readonly string _portName;
    private Thread? _writerThread;
    private CancellationTokenSource? _cts;
    private volatile bool _isRunning;
    private long _bytesWritten;

    /// <summary>
    /// The thread-safe queue used to ingest data packets.
    /// </summary>
    public ConcurrentDataQueue Queue { get; } = new();

    /// <summary>
    /// Gets the total bytes written to the log file so far.
    /// </summary>
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>
    /// Gets a value indicating whether the writer thread is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

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
    /// Starts the background writer thread.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;

        Directory.CreateDirectory(_outputDirectory);
        string fileName = _nameTemplate.Generate(_portName, DateTime.UtcNow);
        CurrentFilePath = Path.Combine(_outputDirectory, fileName);
        
        Interlocked.Exchange(ref _bytesWritten, 0);

        _cts = new CancellationTokenSource();
        _isRunning = true;

        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = $"LogWriter_{_portName}"
        };
        _writerThread.Start();
    }

    /// <summary>
    /// Signals the background thread to stop and waits for it to finish gracefully.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();

        if (_writerThread != null && _writerThread.IsAlive)
        {
            _writerThread.Join(TimeSpan.FromSeconds(3));
        }

        _cts?.Dispose();
        _cts = null;
    }

    private void WriterLoop()
    {
        try
        {
            using var fileStream = new FileStream(CurrentFilePath!, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var bufferedStream = new BufferedStream(fileStream, 65536);

            long bytesSinceLastFlush = 0;
            var sw = Stopwatch.StartNew();

            while (_isRunning)
            {
                // Try to read with a timeout so we can flush periodically even without data
                if (Queue.TryRead(out var packet))
                {
                    WritePacket(bufferedStream, packet);
                    bytesSinceLastFlush += packet.Data.Length;
                    Interlocked.Add(ref _bytesWritten, packet.Data.Length);

                    // Batch: drain more items if available (up to 1000 per batch)
                    int batchCount = 0;
                    while (batchCount < 1000 && Queue.TryRead(out var next))
                    {
                        WritePacket(bufferedStream, next);
                        bytesSinceLastFlush += next.Data.Length;
                        Interlocked.Add(ref _bytesWritten, next.Data.Length);
                        batchCount++;
                    }
                }
                else
                {
                    // No data available, sleep briefly to avoid busy-wait
                    Thread.Sleep(10);
                }

                // Flush on size threshold or time threshold
                if (bytesSinceLastFlush > 0 && (bytesSinceLastFlush >= 65536 || sw.ElapsedMilliseconds >= 500))
                {
                    bufferedStream.Flush();
                    bytesSinceLastFlush = 0;
                    sw.Restart();
                }
            }

            // Drain remaining items after stop signal
            while (Queue.TryRead(out var remaining))
            {
                WritePacket(bufferedStream, remaining);
                Interlocked.Add(ref _bytesWritten, remaining.Data.Length);
            }

            bufferedStream.Flush();
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"LogFileWriter Error ({_portName}): {ex.Message}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LogFileWriter Unexpected Error ({_portName}): {ex.Message}");
        }
        finally
        {
            _isRunning = false;
        }
    }

    private void WritePacket(BufferedStream stream, DataPacket packet)
    {
        if (_format == LogFormat.Binary)
        {
            stream.Write(packet.Data, 0, packet.Data.Length);
            return;
        }

        string prefix = $"[{packet.TimestampUtc:yyyy-MM-dd HH:mm:ss.fff}] ";
        byte[] prefixBytes = Encoding.UTF8.GetBytes(prefix);

        if (_format == LogFormat.Ascii)
        {
            stream.Write(prefixBytes, 0, prefixBytes.Length);
            
            for (int i = 0; i < packet.Data.Length; i++)
            {
                byte b = packet.Data[i];
                if (b < 0x20 && b != '\r' && b != '\n' && b != '\t')
                {
                    stream.WriteByte((byte)'.');
                }
                else
                {
                    stream.WriteByte(b);
                }
            }
            stream.WriteByte((byte)'\n');
        }
        else if (_format == LogFormat.Hex)
        {
            for (int i = 0; i < packet.Data.Length; i += 16)
            {
                stream.Write(prefixBytes, 0, prefixBytes.Length);
                
                int chunkLength = Math.Min(16, packet.Data.Length - i);
                for (int j = 0; j < chunkLength; j++)
                {
                    byte b = packet.Data[i + j];
                    string hex = $"{b:X2} ";
                    byte[] hexBytes = Encoding.UTF8.GetBytes(hex);
                    stream.Write(hexBytes, 0, hexBytes.Length);
                }
                stream.WriteByte((byte)'\n');
            }
        }
    }
}
