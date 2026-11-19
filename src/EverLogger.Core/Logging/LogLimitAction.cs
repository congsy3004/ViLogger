using System;

namespace EverLogger.Core.Logging;

/// <summary>
/// Action taken when an active log file reaches the configured maximum size.
/// </summary>
public enum LogLimitAction
{
    /// <summary>Close the full file and continue writing to a new numbered part file.</summary>
    Rotate,

    /// <summary>Flush and close the full file and stop logging for that port.</summary>
    Stop
}

/// <summary>
/// Significant lifecycle events emitted by <see cref="LogFileWriter"/>.
/// </summary>
public enum LogWriterEventKind
{
    /// <summary>The active log file reached its size limit and rotated to a new part file.</summary>
    Rotated,

    /// <summary>The active log file reached its size limit and logging stopped automatically.</summary>
    LimitReachedStopped
}

/// <summary>
/// Describes a size-limit rotation or auto-stop event produced by <see cref="LogFileWriter"/>.
/// </summary>
public sealed record LogWriterEvent(
    LogWriterEventKind Kind,
    string PortName,
    string CompletedFilePath,
    long CompletedFileBytes,
    long MaxFileSizeBytes,
    string? NewFilePath,
    int CompletedPartNumber,
    int? NewPartNumber,
    DateTime Timestamp);
