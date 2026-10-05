using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using EverLogger.Core.Data;
using RJCP.IO.Ports;

namespace EverLogger.Core.Serial;

/// <summary>
/// Manages a single serial port connection and reading loop.
/// </summary>
/// <remarks>
/// A connection object is opened once and closed once; to reconnect, create a new instance.
/// </remarks>
public class SerialPortConnection : IDisposable
{
    private SerialPortStream? _port;
    private Thread? _readThread;
    private volatile bool _running;
    private volatile bool _closeRequested;
    private readonly byte[] _buffer = new byte[8192];
    private bool _disposed;

    /// <summary>
    /// Gets the configuration for this connection.
    /// </summary>
    public SerialPortConfig Config { get; }

    /// <summary>
    /// Gets a value indicating whether the serial port is currently open.
    /// </summary>
    public bool IsOpen => _port?.IsOpen ?? false;

    /// <summary>
    /// Event fired when data is received from the serial port.
    /// Raised on the read thread; handlers must be fast and must not block.
    /// </summary>
    public event Action<DataPacket>? DataReceived;

    /// <summary>
    /// Event fired when the connection fails unexpectedly (device removed, driver error, ...).
    /// Never raised as a result of <see cref="Close"/>.
    /// </summary>
    public event Action<string, Exception>? ErrorOccurred;

    /// <summary>
    /// Event fired when the connection state changes (opened or closed).
    /// </summary>
    public event Action<string>? ConnectionStateChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="SerialPortConnection"/> class.
    /// </summary>
    /// <param name="config">The configuration for the serial port.</param>
    public SerialPortConnection(SerialPortConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Opens the serial port and starts the read loop.
    /// </summary>
    public void Open()
    {
        if (IsOpen) return;

        try
        {
            _closeRequested = false;
            _port = new SerialPortStream(Config.PortName, Config.BaudRate, Config.DataBits, Config.Parity, Config.StopBits)
            {
                Handshake = Config.Handshake,
                ReadBufferSize = Config.ReadBufferSize,
                WriteBufferSize = Config.WriteBufferSize,
                ReadTimeout = 100,
                WriteTimeout = 500
            };

            _port.Open();
            ConnectionStateChanged?.Invoke(Config.PortName);

            _running = true;
            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = $"SerialRead_{Config.PortName}"
            };
            _readThread.Start();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            ErrorOccurred?.Invoke(Config.PortName, ex);
        }
    }

    private void ReadLoop()
    {
        Exception? failure = null;

        while (_running)
        {
            int bytesRead;
            try
            {
                var port = _port;
                if (port == null || !port.IsOpen)
                {
                    // The port was closed underneath us (e.g. by the driver after a USB unplug)
                    // without Read() throwing. Treat as a failure instead of spinning silently.
                    failure = new IOException($"Port {Config.PortName} was closed.");
                    break;
                }

                bytesRead = port.Read(_buffer, 0, _buffer.Length);
            }
            catch (TimeoutException)
            {
                // Expected when no data arrives within ReadTimeout; lets the loop re-check _running.
                continue;
            }
            catch (Exception ex)
            {
                // Any other read error is fatal for this connection.
                failure = ex;
                break;
            }

            if (bytesRead <= 0) continue;

            byte[] data = new byte[bytesRead];
            Array.Copy(_buffer, data, bytesRead);
            var packet = new DataPacket(Stopwatch.GetTimestamp(), Config.PortName, data);

            try
            {
                DataReceived?.Invoke(packet);
            }
            catch (Exception ex)
            {
                // A faulty subscriber must never stop data reception (and therefore logging).
                Debug.WriteLine($"DataReceived handler error ({Config.PortName}): {ex}");
            }
        }

        _running = false;

        // Report only unexpected failures. If Close() was requested, the exception is just the
        // consequence of our own close and must not be reported (it could otherwise tear down a
        // newer connection to the same port).
        if (failure != null && !_closeRequested)
        {
            ErrorOccurred?.Invoke(Config.PortName, failure);
        }
    }

    /// <summary>
    /// Writes data to the serial port.
    /// </summary>
    /// <param name="data">The bytes to write.</param>
    /// <exception cref="InvalidOperationException">Thrown if the port is not open.</exception>
    /// <exception cref="TimeoutException">The write did not complete within the write timeout.</exception>
    /// <exception cref="IOException">The write failed.</exception>
    /// <remarks>
    /// Write failures are thrown to the caller rather than raised as <see cref="ErrorOccurred"/>:
    /// a failed transmit (e.g. flow control holding off) must not close the port and stop logging
    /// of received data. A real port failure is detected by the read loop.
    /// </remarks>
    public void Write(byte[] data)
    {
        var port = _port;
        if (port == null || !port.IsOpen)
            throw new InvalidOperationException($"Port {Config.PortName} is not open.");

        port.Write(data, 0, data.Length);
    }

    /// <summary>
    /// Closes the serial port and stops the read loop.
    /// </summary>
    public void Close()
    {
        // Set before closing so the read thread knows any resulting exception is expected.
        // Never reset: this connection instance is finished once closed.
        _closeRequested = true;
        _running = false;

        var port = _port;
        if (port != null && port.IsOpen)
        {
            try
            {
                // Close the port first — this immediately interrupts any blocking Read()
                // in the read thread, causing it to exit quickly.
                port.Close();
            }
            catch { }
        }

        // The read thread exits within one ReadTimeout (100 ms) at the latest.
        var thread = _readThread;
        if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
        {
            thread.Join(250);
        }

        ConnectionStateChanged?.Invoke(Config.PortName);
    }

    /// <summary>
    /// Disposes of the connection and its resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        
        Close();
        _port?.Dispose();
        _disposed = true;
    }
}
