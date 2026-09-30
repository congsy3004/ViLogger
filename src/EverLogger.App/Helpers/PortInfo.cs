namespace EverLogger.App.Helpers;

/// <summary>
/// Represents a COM port for display in the port selection dropdown.
/// </summary>
public class PortInfo
{
    public string PortName { get; }
    public string Description { get; }
    public string DisplayText { get; }

    public PortInfo(string portName, string description)
    {
        PortName = portName;
        Description = description;
        DisplayText = string.IsNullOrWhiteSpace(description)
            ? portName
            : $"{portName} - {description}";
    }

    public override string ToString() => DisplayText;
}

/// <summary>
/// Represents a layout option for the port monitor grid.
/// </summary>
public class LayoutOption
{
    public int Columns { get; }
    public int Rows { get; }
    public string DisplayText { get; }

    public LayoutOption(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
        DisplayText = $"{columns}x{rows}";
    }

    public override string ToString() => DisplayText;
}
