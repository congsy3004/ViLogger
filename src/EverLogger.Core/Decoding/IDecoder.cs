using System;
using System.Collections.Generic;

namespace EverLogger.Core.Decoding;

/// <summary>
/// Interface for protocol decoders (e.g., NMEA, RTCM3).
/// Not implemented in this version — provided for future extensibility.
/// </summary>
public interface IDecoder
{
    /// <summary>Human-readable name of the decoder.</summary>
    string Name { get; }
    
    /// <summary>Description of the protocol this decoder handles.</summary>
    string Description { get; }
    
    /// <summary>
    /// Process incoming raw bytes and return decoded messages.
    /// </summary>
    /// <param name="data">Raw bytes from the serial port.</param>
    /// <returns>Decoded message strings, or empty if no complete message yet.</returns>
    IEnumerable<string> Decode(ReadOnlySpan<byte> data);
    
    /// <summary>Reset the decoder's internal state.</summary>
    void Reset();
}
