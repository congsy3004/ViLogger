using System;
using System.Diagnostics;

namespace EverLogger.Core.Data;

/// <summary>
/// A readonly struct representing a timestamped chunk of bytes from a serial port.
/// </summary>
public readonly struct DataPacket
{
    private static readonly long _baseTimestampTicks;
    private static readonly DateTime _baseUtcTime;

    static DataPacket()
    {
        _baseTimestampTicks = Stopwatch.GetTimestamp();
        _baseUtcTime = DateTime.UtcNow;
    }

    /// <summary>
    /// The high-resolution timestamp ticks captured at the moment of read.
    /// </summary>
    public long TimestampTicks { get; }

    /// <summary>
    /// The name of the COM port this data came from.
    /// </summary>
    public string PortName { get; }

    /// <summary>
    /// The raw bytes captured from the serial port.
    /// </summary>
    public byte[] Data { get; }

    /// <summary>
    /// The approximate UTC time of the capture, calculated using the calibrated stopwatch ticks.
    /// </summary>
    public DateTime TimestampUtc
    {
        get
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(_baseTimestampTicks, TimestampTicks);
            return _baseUtcTime + elapsed;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DataPacket"/> struct.
    /// </summary>
    /// <param name="timestampTicks">The timestamp ticks from Stopwatch.</param>
    /// <param name="portName">The COM port name.</param>
    /// <param name="data">The raw data bytes.</param>
    public DataPacket(long timestampTicks, string portName, byte[] data)
    {
        TimestampTicks = timestampTicks;
        PortName = portName ?? throw new ArgumentNullException(nameof(portName));
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }
}
