namespace EverLogger.Core.Logging;

/// <summary>Format for logged data.</summary>
public enum LogFormat
{
    /// <summary>Raw binary data as-is (.bin file).</summary>
    Binary,
    
    /// <summary>Hexadecimal text representation (e.g., "4A 6F 68 6E"), 16 bytes per line.</summary>
    Hex,
    
    /// <summary>
    /// Text log (.log file). In log files the received bytes are written exactly as received.
    /// In the terminal view, non-printable bytes are shown as '.'.
    /// </summary>
    Ascii
}
