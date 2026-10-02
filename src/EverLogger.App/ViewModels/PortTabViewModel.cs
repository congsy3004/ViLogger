using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using EverLogger.App.Helpers;
using EverLogger.Core.Data;
using EverLogger.Core.Logging;
using EverLogger.Core.Serial;
using RJCP.IO.Ports;

namespace EverLogger.App.ViewModels;

public class PortTabViewModel : ViewModelBase
{
    private SerialPortConfig _config;
    private bool _isConfigured;
    private bool _isConnected;
    private bool _isLogging;
    private string _logMode = "Off";
    private bool _showTimestamp = true;
    private string _statusText = "Not configured";
    private long _bytesReceived;
    private long _bytesLogged;
    private LogFormat _selectedDisplayFormat = LogFormat.Ascii;
    private LogFormat _selectedLogFormat = LogFormat.Ascii;
    private bool _autoScroll = true;
    private readonly StringBuilder _lineBuffer = new();
    private LogFileWriter? _logWriter;
    private const int MaxMonitorLines = 5000;
    private const int TrimBatchSize = 500;

    // Port selection (unconfigured state)
    private PortInfo? _selectedPort;
    private int _selectedBaudRate = 115200;
    private string _customName = string.Empty;

    // Callbacks to MainViewModel
    private readonly Action<PortTabViewModel> _connectAction;
    private readonly Action<PortTabViewModel> _disconnectAction;
    private readonly Action<PortTabViewModel, byte[]> _sendAction;
    private readonly Action<PortTabViewModel> _removeAction;
    private readonly Func<bool> _canRemoveFunc;
    private readonly Func<string> _getLogDirectory;
    private readonly Func<string> _getLogFileTemplate;

    // Tx state
    private string _txInput = string.Empty;
    private string _selectedLineEnding = "CRLF";
    private string _txMode = "ASCII";
    private long _bytesSent;

    // Shared collections from MainViewModel
    public ObservableCollection<PortInfo> AvailablePorts { get; }
    public ObservableCollection<int> CommonBaudRates { get; }

    public PortTabViewModel(
        ObservableCollection<PortInfo> availablePorts,
        ObservableCollection<int> commonBaudRates,
        Action<PortTabViewModel> connectAction,
        Action<PortTabViewModel> disconnectAction,
        Action<PortTabViewModel, byte[]> sendAction,
        Action<PortTabViewModel> removeAction,
        Func<bool> canRemove,
        Func<string> getLogDirectory,
        Func<string> getLogFileTemplate)
    {
        _config = new SerialPortConfig();
        _connectAction = connectAction;
        _disconnectAction = disconnectAction;
        _sendAction = sendAction;
        _removeAction = removeAction;
        _canRemoveFunc = canRemove;
        _getLogDirectory = getLogDirectory;
        _getLogFileTemplate = getLogFileTemplate;
        AvailablePorts = availablePorts;
        CommonBaudRates = commonBaudRates;

        MonitorLines = new ObservableCollection<string>();

        // Config commands
        ApplyConfigCommand = new RelayCommand(ApplyConfig, () => _selectedPort != null);
        ResetConfigCommand = new RelayCommand(ResetConfig, () => IsConfigured && !IsConnected);

        // Port control commands
        ConnectCommand = new RelayCommand(
            () => _connectAction(this),
            () => IsConfigured && !IsConnected);
        DisconnectCommand = new RelayCommand(
            () => _disconnectAction(this),
            () => IsConnected);
        ToggleConnectCommand = new RelayCommand(
            () => { if (IsConnected) _disconnectAction(this); else _connectAction(this); },
            () => IsConfigured);
        ToggleLogCommand = new RelayCommand(ToggleLog, () => IsConfigured);
        ClearCommand = new RelayCommand(ClearMonitor);
        ToggleTimestampCommand = new RelayCommand(() => ShowTimestamp = !ShowTimestamp);
        ToggleAutoScrollCommand = new RelayCommand(() => AutoScroll = !AutoScroll);
        RemoveThisPortCommand = new RelayCommand(() => _removeAction(this), _canRemoveFunc);

        // Tx command
        SendCommand = new RelayCommand(Send, () => IsConnected && !string.IsNullOrEmpty(TxInput));
    }

    // ───────────────── Config State ─────────────────

    public bool IsConfigured
    {
        get => _isConfigured;
        private set
        {
            if (SetProperty(ref _isConfigured, value))
            {
                ((RelayCommand)ConnectCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ToggleConnectCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ResetConfigCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ToggleLogCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// The selected port in the config dropdown (unconfigured state).
    /// </summary>
    public PortInfo? SelectedPort
    {
        get => _selectedPort;
        set
        {
            if (SetProperty(ref _selectedPort, value))
            {
                ((RelayCommand)ApplyConfigCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// The selected baud rate in the config dropdown (unconfigured state).
    /// </summary>
    public int SelectedBaudRate
    {
        get => _selectedBaudRate;
        set => SetProperty(ref _selectedBaudRate, value);
    }

    /// <summary>
    /// User-defined memorable name for this port (e.g. "GPS Module", "Debug UART").
    /// </summary>
    public string CustomName
    {
        get => _customName;
        set => SetProperty(ref _customName, value);
    }

    // ───────────────── Display Properties ─────────────────

    public string DisplayLabel
    {
        get
        {
            if (!_isConfigured) return "Not Configured";

            string recognizedName = string.IsNullOrWhiteSpace(_config.DisplayName)
                ? _config.PortName
                : _config.DisplayName;

            return string.IsNullOrWhiteSpace(_customName)
                ? recognizedName
                : $"{_customName} - {recognizedName}";
        }
    }

    public LogFormat[] LogFormatValues { get; } = Enum.GetValues<LogFormat>();
    public int[] DataBitsValues { get; } = { 5, 6, 7, 8 };
    public Parity[] ParityValues { get; } = Enum.GetValues<Parity>();
    public StopBits[] StopBitsValues { get; } = Enum.GetValues<StopBits>();
    public Handshake[] HandshakeValues { get; } = Enum.GetValues<Handshake>();

    // ───────────────── Serial Config ─────────────────

    public SerialPortConfig Config
    {
        get => _config;
        set => SetProperty(ref _config, value);
    }

    public string PortName => _config.PortName;

    public int DataBits
    {
        get => _config.DataBits;
        set
        {
            if (_config.DataBits != value)
            {
                Config = _config with { DataBits = value };
                OnPropertyChanged();
            }
        }
    }

    public Parity SelectedParity
    {
        get => _config.Parity;
        set
        {
            if (_config.Parity != value)
            {
                Config = _config with { Parity = value };
                OnPropertyChanged();
            }
        }
    }

    public StopBits SelectedStopBits
    {
        get => _config.StopBits;
        set
        {
            if (_config.StopBits != value)
            {
                Config = _config with { StopBits = value };
                OnPropertyChanged();
            }
        }
    }

    public Handshake SelectedHandshake
    {
        get => _config.Handshake;
        set
        {
            if (_config.Handshake != value)
            {
                Config = _config with { Handshake = value };
                OnPropertyChanged();
            }
        }
    }

    // ───────────────── State Properties ─────────────────

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (SetProperty(ref _isConnected, value))
            {
                ((RelayCommand)ConnectCommand).RaiseCanExecuteChanged();
                ((RelayCommand)DisconnectCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ToggleConnectCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ResetConfigCommand).RaiseCanExecuteChanged();
                ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsLogging
    {
        get => _isLogging;
        set => SetProperty(ref _isLogging, value);
    }

    public string[] LogModeOptions { get; } = ["Off", "Ascii", "Hex", "Binary"];

    public string LogMode
    {
        get => _logMode;
        set
        {
            if (_logMode == value) return;
            _logMode = value;
            OnPropertyChanged();

            // Always stop the current writer first
            DoStopLogging();

            if (value != "Off")
            {
                // Map mode name to LogFormat enum for the file writer
                SelectedLogFormat = value switch
                {
                    "Hex"    => LogFormat.Hex,
                    "Binary" => LogFormat.Binary,
                    _        => LogFormat.Ascii
                };
                DoStartLogging();
            }
        }
    }

    public bool ShowTimestamp
    {
        get => _showTimestamp;
        set => SetProperty(ref _showTimestamp, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public long BytesReceived
    {
        get => _bytesReceived;
        set => SetProperty(ref _bytesReceived, value);
    }

    public long BytesLogged
    {
        get => _bytesLogged;
        set => SetProperty(ref _bytesLogged, value);
    }

    // ───────────────── Per-Port Settings ─────────────────

    public LogFormat SelectedDisplayFormat
    {
        get => _selectedDisplayFormat;
        set => SetProperty(ref _selectedDisplayFormat, value);
    }

    public LogFormat SelectedLogFormat
    {
        get => _selectedLogFormat;
        set => SetProperty(ref _selectedLogFormat, value);
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public ObservableCollection<string> MonitorLines { get; }

    // ───────────────── Commands ─────────────────

    public ICommand ApplyConfigCommand { get; }
    public ICommand ResetConfigCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ToggleConnectCommand { get; }
    public ICommand ToggleLogCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ToggleTimestampCommand { get; }
    public ICommand ToggleAutoScrollCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand RemoveThisPortCommand { get; }


    // ───────────────── Tx Properties ─────────────────

    public string[] LineEndingOptions { get; } = ["None", "CR", "LF", "CRLF"];
    public string[] TxModeOptions { get; } = ["ASCII", "HEX"];
    public string[] SendMacroOptions { get; } = ["Ctrl+Enter", "Alt+Enter", "Shift+Enter"];

    private string _sendMacro = "Ctrl+Enter";
    public string SendMacro
    {
        get => _sendMacro;
        set => SetProperty(ref _sendMacro, value);
    }

    public string TxMode
    {
        get => _txMode;
        set
        {
            if (_txMode == value) return;
            string previous = _txMode;
            _txMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SendAsHex));

            // Convert existing TX input when switching modes
            if (!string.IsNullOrEmpty(_txInput))
            {
                if (value == "HEX" && previous == "ASCII")
                {
                    // Decode display tokens to real bytes, then convert to hex
                    string decoded = _txInput
                        .Replace("<CR><LF>", "\r\n")
                        .Replace("<CR>",     "\r")
                        .Replace("<LF>",     "\n");
                    byte[] bytes = System.Text.Encoding.ASCII.GetBytes(decoded);
                    TxInput = BitConverter.ToString(bytes).Replace("-", " ");
                }
                else if (value == "ASCII" && previous == "HEX")
                {
                    // Hex bytes → ASCII text (best effort; clear on failure)
                    try
                    {
                        string raw = new string(
                            _txInput.Where(c => "0123456789ABCDEFabcdef".Contains(c)).ToArray());
                        if (raw.Length > 0 && raw.Length % 2 == 0)
                        {
                            byte[] bytes = new byte[raw.Length / 2];
                            for (int i = 0; i < bytes.Length; i++)
                                bytes[i] = Convert.ToByte(raw.Substring(i * 2, 2), 16);
                            TxInput = System.Text.Encoding.ASCII.GetString(bytes);
                        }
                        else
                        {
                            TxInput = string.Empty;
                        }
                    }
                    catch
                    {
                        TxInput = string.Empty;
                    }
                }
            }

            ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
        }
    }

    /// <summary>True when TxMode is "HEX".</summary>
    public bool SendAsHex => _txMode == "HEX";

    public string TxInput
    {
        get => _txInput;
        set
        {
            if (SendAsHex)
            {
                // Keep only valid hex characters, uppercase
                string raw = new string(
                    value.Where(c => "0123456789ABCDEFabcdef".Contains(c)).ToArray())
                    .ToUpperInvariant();

                // Re-group into pairs separated by a space: "AB4D3D" → "AB 4D 3D"
                var parts = new System.Collections.Generic.List<string>();
                for (int i = 0; i < raw.Length; i += 2)
                    parts.Add(raw.Substring(i, Math.Min(2, raw.Length - i)));
                value = string.Join(" ", parts);
            }
            else
            {
                // ASCII mode: replace any actual CR/LF characters (e.g. from paste) with
                // explicit display tokens so the TextBox stays single-line and non-ambiguous.
                // Order: CRLF first, then standalone CR, then standalone LF.
                value = value
                    .Replace("\r\n", "<CR><LF>")
                    .Replace("\r",   "<CR>")
                    .Replace("\n",   "<LF>");
            }

            if (SetProperty(ref _txInput, value))
            {
                ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedLineEnding
    {
        get => _selectedLineEnding;
        set => SetProperty(ref _selectedLineEnding, value);
    }

    public long BytesSent
    {
        get => _bytesSent;
        set => SetProperty(ref _bytesSent, value);
    }

    // ───────────────── Config Actions ─────────────────

    private void ApplyConfig()
    {
        if (_selectedPort == null) return;

        Config = _config with
        {
            PortName = _selectedPort.PortName,
            BaudRate = _selectedBaudRate,
            DisplayName = _selectedPort.Description
        };

        IsConfigured = true;
        StatusText = "Disconnected";
        OnPropertyChanged(nameof(DisplayLabel));
        OnPropertyChanged(nameof(PortName));
    }

    private void ResetConfig()
    {
        StopLogging();
        IsConfigured = false;
        StatusText = "Not configured";
        OnPropertyChanged(nameof(DisplayLabel));
    }

    // ───────────────── Logging ─────────────────

    /// <summary>
    /// Starts logging at the given mode (Ascii/Hex/Binary). Sets LogMode and opens the file writer.
    /// If LogMode is already a non-Off value, this is a no-op (already logging).
    /// Called by LogAllOn in MainViewModel.
    /// </summary>
    public void StartLogging()
    {
        if (_logMode != "Off") return; // already logging
        LogMode = "Ascii"; // default format for global start-all
    }

    /// <summary>
    /// Stops logging and resets LogMode to "Off". Called by error handler and LogAllOff.
    /// </summary>
    public void StopLogging()
    {
        LogMode = "Off";
    }

    /// <summary>
    /// Actually opens the log file writer. Called only from the LogMode setter.
    /// </summary>
    private void DoStartLogging()
    {
        if (_isLogging || !IsConfigured) return;

        string logDir = _getLogDirectory();
        string templateStr = _getLogFileTemplate();

        try
        {
            Directory.CreateDirectory(logDir);
        }
        catch { return; }

        string ext = SelectedLogFormat == LogFormat.Binary ? ".bin" : ".log";
        var template = new LogFileNameTemplate(templateStr, ext);
        _logWriter = new LogFileWriter(logDir, SelectedLogFormat, template, PortName);
        _logWriter.Start();
        IsLogging = true;
    }

    /// <summary>
    /// Stops and disposes the log file writer. Called only from the LogMode setter.
    /// </summary>
    private void DoStopLogging()
    {
        if (!_isLogging) return;

        _logWriter?.Stop();
        _logWriter = null;
        IsLogging = false;
    }

    private void ToggleLog()
    {
        LogMode = _logMode == "Off" ? "Ascii" : "Off";
    }

    public void EnqueueLogData(DataPacket packet)
    {
        _logWriter?.Queue.TryWrite(packet);
    }

    // ───────────────── Tx (Send) ─────────────────

    private void Send()
    {
        if (string.IsNullOrEmpty(TxInput) || !IsConnected) return;

        try
        {
            byte[] data;
            if (SendAsHex)
            {
                data = ParseHexString(TxInput);
            }
            else
            {
                string lineEnding = SelectedLineEnding switch
                {
                    "CR"   => "\r",
                    "LF"   => "\n",
                    "CRLF" => "\r\n",
                    _      => ""
                };
                // Decode display tokens back to real CR/LF bytes before encoding
                string text = TxInput
                    .Replace("<CR><LF>", "\r\n")
                    .Replace("<CR>",     "\r")
                    .Replace("<LF>",     "\n");
                data = System.Text.Encoding.ASCII.GetBytes(text + lineEnding);
            }

            _sendAction(this, data);
            BytesSent += data.Length;

            // Echo in monitor — show the token representation so the user sees exactly what was sent
            string display = SendAsHex
                ? BitConverter.ToString(data).Replace("-", " ")
                : TxInput;
            AddLine($"TX>> {display}");

            TxInput = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText = $"Tx Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Parses a hex string like "48 65 6C 6C 6F" or "48656C6C6F" into bytes.
    /// </summary>
    private static byte[] ParseHexString(string hex)
    {
        // Remove common separators
        hex = hex.Replace(" ", "").Replace("-", "").Replace("0x", "").Replace(",", "");
        if (hex.Length % 2 != 0)
            throw new FormatException("Hex string must have an even number of characters.");

        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    // ───────────────── Monitor Display ─────────────────

    public void AppendData(DataPacket packet)
    {
        if (packet.Data == null || packet.Data.Length == 0) return;
        
        BytesReceived += packet.Data.Length;
        if (IsLogging)
        {
            BytesLogged += packet.Data.Length;
        }

        switch (_selectedDisplayFormat)
        {
            case LogFormat.Ascii:
                AppendAsciiData(packet.Data);
                break;
            case LogFormat.Hex:
                AddLine(BitConverter.ToString(packet.Data).Replace("-", " "));
                break;
            case LogFormat.Binary:
                AddLine($"[Binary Data: {packet.Data.Length} bytes]");
                break;
        }
    }

    private void AppendAsciiData(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            char c = (char)data[i];
            if (c == '\n')
            {
                AddLine(_lineBuffer.ToString());
                _lineBuffer.Clear();
            }
            else if (c == '\r')
            {
                continue;
            }
            else
            {
                _lineBuffer.Append(c < 0x20 || c > 0x7E ? '.' : c);
            }
        }

        if (_lineBuffer.Length > 4096)
        {
            AddLine(_lineBuffer.ToString());
            _lineBuffer.Clear();
        }
    }

    private void AddLine(string line)
    {
        string entry = _showTimestamp
            ? $"[{DateTime.Now:HH:mm:ss.fff}] {line}"
            : line;
        MonitorLines.Add(entry);

        if (MonitorLines.Count > MaxMonitorLines + TrimBatchSize)
        {
            for (int i = 0; i < TrimBatchSize; i++)
            {
                MonitorLines.RemoveAt(0);
            }
        }
    }

    public void ClearMonitor()
    {
        MonitorLines.Clear();
        _lineBuffer.Clear();
        BytesReceived = 0;
        BytesLogged = 0;
        BytesSent = 0;
    }

    /// <summary>
    /// Cleanup when this panel is removed (layout shrinks).
    /// </summary>
    public void Cleanup()
    {
        StopLogging();
        if (IsConnected)
        {
            _disconnectAction(this);
        }
    }
}
