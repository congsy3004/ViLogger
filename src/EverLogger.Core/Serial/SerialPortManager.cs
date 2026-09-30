using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using EverLogger.Core.Data;
using RJCP.IO.Ports;

namespace EverLogger.Core.Serial;

/// <summary>
/// Manages multiple serial port connections.
/// </summary>
public class SerialPortManager : IDisposable
{
    private readonly Dictionary<string, SerialPortConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Gets the current collection of configured connections.
    /// </summary>
    public IReadOnlyDictionary<string, SerialPortConnection> Connections => _connections;

    /// <summary>
    /// Event fired when data is received from any managed serial port.
    /// </summary>
    public event Action<DataPacket>? DataReceived;

    /// <summary>
    /// Event fired when an error occurs on any managed connection.
    /// </summary>
    public event Action<string, Exception>? ErrorOccurred;

    /// <summary>
    /// Event fired when a connection state changes on any managed connection.
    /// </summary>
    public event Action<string>? ConnectionStateChanged;

    /// <summary>
    /// Adds a new serial port configuration to be managed.
    /// </summary>
    /// <param name="config">The configuration to add.</param>
    public void AddPort(SerialPortConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (_connections.ContainsKey(config.PortName))
        {
            throw new InvalidOperationException($"Port {config.PortName} is already configured.");
        }

        var connection = new SerialPortConnection(config);
        connection.DataReceived += OnDataReceived;
        connection.ErrorOccurred += OnErrorOccurred;
        connection.ConnectionStateChanged += OnConnectionStateChanged;

        _connections[config.PortName] = connection;
    }

    /// <summary>
    /// Removes a managed serial port and cleans up its resources.
    /// </summary>
    /// <param name="portName">The COM port name to remove.</param>
    public void RemovePort(string portName)
    {
        if (_connections.TryGetValue(portName, out var connection))
        {
            connection.Close();
            connection.DataReceived -= OnDataReceived;
            connection.ErrorOccurred -= OnErrorOccurred;
            connection.ConnectionStateChanged -= OnConnectionStateChanged;
            connection.Dispose();
            _connections.Remove(portName);
        }
    }

    /// <summary>
    /// Opens a specific managed serial port.
    /// </summary>
    /// <param name="portName">The COM port name to open.</param>
    public void OpenPort(string portName)
    {
        if (_connections.TryGetValue(portName, out var connection))
        {
            connection.Open();
        }
    }

    /// <summary>
    /// Closes a specific managed serial port.
    /// </summary>
    /// <param name="portName">The COM port name to close.</param>
    public void ClosePort(string portName)
    {
        if (_connections.TryGetValue(portName, out var connection))
        {
            connection.Close();
        }
    }

    /// <summary>
    /// Opens all configured serial ports.
    /// </summary>
    public void OpenAll()
    {
        foreach (var connection in _connections.Values)
        {
            connection.Open();
        }
    }

    /// <summary>
    /// Closes all configured serial ports.
    /// </summary>
    public void CloseAll()
    {
        foreach (var connection in _connections.Values)
        {
            connection.Close();
        }
    }

    /// <summary>
    /// Writes data to a specific port.
    /// </summary>
    /// <param name="portName">The port to write to.</param>
    /// <param name="data">The data bytes to send.</param>
    public void WriteToPort(string portName, byte[] data)
    {
        if (_connections.TryGetValue(portName, out var connection))
        {
            connection.Write(data);
        }
        else
        {
            throw new InvalidOperationException($"Port {portName} is not configured.");
        }
    }

    /// <summary>
    /// Retrieves an array of available COM port names on the system.
    /// </summary>
    /// <returns>An array of COM port names.</returns>
    public static string[] GetAvailablePorts()
    {
        using var tempPort = new SerialPortStream();
        return tempPort.GetPortNames();
    }

    /// <summary>
    /// Retrieves available COM ports with their friendly descriptions.
    /// </summary>
    /// <returns>An array of tuples containing port name and description.</returns>
    public static (string PortName, string Description)[] GetAvailablePortDescriptions()
    {
        using var tempPort = new SerialPortStream();
        var descriptions = tempPort.GetPortDescriptions();
        return descriptions
            .Select(d => (d.Port, d.Description))
            .OrderBy(d => d.Port)
            .ToArray();
    }

    private void OnDataReceived(DataPacket packet) => DataReceived?.Invoke(packet);
    private void OnErrorOccurred(string port, Exception ex) => ErrorOccurred?.Invoke(port, ex);
    private void OnConnectionStateChanged(string port) => ConnectionStateChanged?.Invoke(port);

    /// <summary>
    /// Disposes all managed connections.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        foreach (var connection in _connections.Values)
        {
            connection.DataReceived -= OnDataReceived;
            connection.ErrorOccurred -= OnErrorOccurred;
            connection.ConnectionStateChanged -= OnConnectionStateChanged;
            connection.Dispose();
        }
        
        _connections.Clear();
        _disposed = true;
    }
}
