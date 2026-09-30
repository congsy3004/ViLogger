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
public class SerialPortConnection : IDisposable
{
    private SerialPortStream? _port;
    private Thread? _readThread;
    private volatile bool _running;
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
    /// </summary>
    public event Action<DataPacket>? DataReceived;

    /// <summary>
    /// Event fired when an error occurs during reading or connection.
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
            _port = new SerialPortStream(Config.PortName, Config.BaudRate, Config.DataBits, Config.Parity, Config.StopBits)
            {
                Handshake = Config.Handshake,
                ReadBufferSize = Config.ReadBufferSize,
                WriteBufferSize = Config.WriteBufferSize,
                ReadTimeout = 100
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
        while (_running)
        {
            try
            {
                if (_port != null && _port.IsOpen)
                {
                    int bytesRead = _port.Read(_buffer, 0, _buffer.Length);
                    if (bytesRead > 0)
                    {
                        long timestamp = Stopwatch.GetTimestamp();
                        byte[] data = new byte[bytesRead];
                        Array.Copy(_buffer, data, bytesRead);

                        DataReceived?.Invoke(new DataPacket(timestamp, Config.PortName, data));
                    }
                }
            }
            catch (TimeoutException)
            {
                // Timeout is expected, allows the loop to check the _running flag
                continue;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                _running = false;
                ErrorOccurred?.Invoke(Config.PortName, ex);
                ConnectionStateChanged?.Invoke(Config.PortName);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(Config.PortName, ex);
            }
        }
    }

    /// <summary>
    /// Writes data to the serial port.
    /// </summary>
    /// <param name="data">The bytes to write.</param>
    /// <exception cref="InvalidOperationException">Thrown if the port is not open.</exception>
    public void Write(byte[] data)
    {
        if (_port == null || !_port.IsOpen)
            throw new InvalidOperationException($"Port {Config.PortName} is not open.");

        try
        {
            _port.Write(data, 0, data.Length);
        }
        catch (Exception ex) when (ex is IOException || ex is TimeoutException)
        {
            ErrorOccurred?.Invoke(Config.PortName, ex);
        }
    }

    /// <summary>
    /// Closes the serial port and stops the read loop.
    /// </summary>
    public void Close()
    {
        _running = false;

        if (_readThread != null && _readThread.IsAlive)
        {
            _readThread.Join(500);
        }

        if (_port != null && _port.IsOpen)
        {
            try
            {
                _port.Close();
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(Config.PortName, ex);
            }
            finally
            {
                ConnectionStateChanged?.Invoke(Config.PortName);
            }
        }
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
