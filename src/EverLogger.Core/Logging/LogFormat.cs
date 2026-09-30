namespace EverLogger.Core.Logging;

/// <summary>Format for logged data.</summary>
public enum LogFormat
{
    /// <summary>Raw binary data as-is.</summary>
    Binary,
    
    /// <summary>Hexadecimal text representation (e.g., "4A 6F 68 6E").</summary>
    Hex,
    
    /// <summary>ASCII text with non-printable characters escaped.</summary>
    Ascii
}
