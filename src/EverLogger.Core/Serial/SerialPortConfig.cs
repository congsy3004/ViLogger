namespace EverLogger.Core.Serial;

using RJCP.IO.Ports;

/// <summary>
/// Configuration for a serial port connection.
/// </summary>
public record SerialPortConfig
{
    /// <summary>The COM port name (e.g., "COM1").</summary>
    public string PortName { get; init; } = "COM1";

    /// <summary>The baud rate.</summary>
    public int BaudRate { get; init; } = 115200;

    /// <summary>The number of data bits.</summary>
    public int DataBits { get; init; } = 8;

    /// <summary>The parity checking protocol.</summary>
    public Parity Parity { get; init; } = Parity.None;

    /// <summary>The number of stop bits.</summary>
    public StopBits StopBits { get; init; } = StopBits.One;

    /// <summary>The handshaking protocol for flow control.</summary>
    public Handshake Handshake { get; init; } = Handshake.None;

    /// <summary>The size of the read buffer in bytes.</summary>
    public int ReadBufferSize { get; init; } = 65536; // 64KB driver buffer

    /// <summary>The size of the write buffer in bytes.</summary>
    public int WriteBufferSize { get; init; } = 4096;

    /// <summary>A user-friendly display name for the port.</summary>
    public string DisplayName { get; init; } = "";
}
