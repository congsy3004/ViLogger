using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using EverLogger.Core.Data;

namespace EverLogger.Core.Logging;

/// <summary>
/// Orchestrates logging for multiple COM ports simultaneously.
/// </summary>
public class LogSession : IDisposable
{
    private readonly string _outputDirectory;
    private readonly LogFormat _format;
    private readonly LogFileNameTemplate _nameTemplate;
    private readonly ConcurrentDictionary<string, LogFileWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Gets a value indicating whether the logging session is currently active.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LogSession"/> class.
    /// </summary>
    /// <param name="outputDirectory">The base directory where log files should be saved.</param>
    /// <param name="format">The chosen log data format.</param>
    /// <param name="nameTemplate">The file naming template.</param>
    public LogSession(string outputDirectory, LogFormat format, LogFileNameTemplate nameTemplate)
    {
        _outputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));
        _format = format;
        _nameTemplate = nameTemplate ?? throw new ArgumentNullException(nameof(nameTemplate));
    }

    /// <summary>
    /// Adds a writer for the specified port.
    /// </summary>
    /// <param name="portName">The COM port name.</param>
    public void AddPort(string portName)
    {
        var writer = new LogFileWriter(_outputDirectory, _format, _nameTemplate, portName);
        if (_writers.TryAdd(portName, writer))
        {
            if (IsRunning)
            {
                writer.Start();
            }
        }
    }

    /// <summary>
    /// Removes and stops the writer for the specified port.
    /// </summary>
    /// <param name="portName">The COM port name.</param>
    public void RemovePort(string portName)
    {
        if (_writers.TryRemove(portName, out var writer))
        {
            writer.Stop();
        }
    }

    /// <summary>
    /// Starts logging for all managed ports.
    /// </summary>
    public void StartAll()
    {
        if (IsRunning) return;

        IsRunning = true;
        foreach (var writer in _writers.Values)
        {
            writer.Start();
        }
    }

    /// <summary>
    /// Stops logging for all managed ports.
    /// </summary>
    public void StopAll()
    {
        if (!IsRunning) return;

        IsRunning = false;
        foreach (var writer in _writers.Values)
        {
            writer.Stop();
        }
    }

    /// <summary>
    /// Enqueues data to the appropriate port's log writer.
    /// </summary>
    /// <param name="packet">The data packet received from the serial port.</param>
    public void EnqueueData(DataPacket packet)
    {
        if (!IsRunning || _disposed) return;

        if (_writers.TryGetValue(packet.PortName, out var writer))
        {
            writer.Queue.TryWrite(packet);
        }
    }

    /// <summary>
    /// Stops all logging and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        
        StopAll();
        _writers.Clear();
        _disposed = true;
    }
}
