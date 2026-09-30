using System;
using System.IO;

namespace EverLogger.Core.Logging;

/// <summary>
/// Template engine for generating log file names.
/// </summary>
public class LogFileNameTemplate
{
    private readonly string _template;
    private readonly string _extension;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogFileNameTemplate"/> class.
    /// </summary>
    /// <param name="template">The template string containing tokens (e.g., "{port}_{datetime}"). Default is "{port}_{datetime}".</param>
    /// <param name="extension">The file extension to append. Default is ".log".</param>
    public LogFileNameTemplate(string template = "{port}_{datetime}", string extension = ".log")
    {
        _template = string.IsNullOrWhiteSpace(template) ? "{port}_{datetime}" : template;
        _extension = extension ?? string.Empty;
    }

    /// <summary>
    /// Generates a valid file name based on the template, port name, and timestamp.
    /// </summary>
    /// <param name="portName">The COM port name.</param>
    /// <param name="timestamp">The timestamp to use for formatting.</param>
    /// <returns>A formatted and sanitized file name.</returns>
    public string Generate(string portName, DateTime timestamp)
    {
        var name = _template
            .Replace("{port}", portName)
            .Replace("{date}", timestamp.ToString("yyyy-MM-dd"))
            .Replace("{time}", timestamp.ToString("HH-mm-ss"))
            .Replace("{datetime}", timestamp.ToString("yyyy-MM-dd_HH-mm-ss"))
            .Replace("{timestamp}", timestamp.ToString("yyyyMMddHHmmss"));

        name += _extension;

        // Clean invalid characters
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }
}
