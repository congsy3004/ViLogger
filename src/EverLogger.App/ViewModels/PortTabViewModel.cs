using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Windows;
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
    private bool _isHardwareRemoved;
    private bool _isLogging;
    private string _statusText = "Not configured";
    private long _bytesReceived;
    private long _bytesLogged;
    private LogFormat _selectedDisplayFormat = LogFormat.Ascii;
    private LogFormat _selectedLogFormat = LogFormat.Ascii;
    private bool _autoScroll = true;
    // Read by the serial read thread (EnqueueLogData), written on the UI thread.
    private volatile LogFileWriter? _logWriter;
    private string? _lastLogFilePath;
    private long _logBytesSeen;     // writer.BytesWritten already added to BytesLogged
    private long _pendingRxBytes;   // received since the last UI tick

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
        OpenLogFileCommand = new RelayCommand(OpenLogFile, () => !IsLogging && !string.IsNullOrEmpty(_lastLogFilePath));
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
        set
        {
            if (SetProperty(ref _isLogging, value))
            {
                ((RelayCommand)OpenLogFileCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsHardwareRemoved
    {
        get => _isHardwareRemoved;
        set => SetProperty(ref _isHardwareRemoved, value);
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

    /// <summary>
    /// ON: the terminal shows new data live and stays at the bottom.
    /// OFF: the terminal is paused so it can be scrolled, read and copied. Data is still received
    /// and logged; turning AutoScroll back ON jumps to the latest data.
    /// </summary>
    public bool AutoScroll
    {
        get => _autoScroll;
        set
        {
            if (SetProperty(ref _autoScroll, value) && value)
            {
                // Catch up right away instead of waiting for the next tick.
                RaiseDisplayUpdate();
            }
        }
    }

    /// <summary>
    /// Text model of the terminal view. UI thread only.
    /// </summary>
    public TerminalBuffer Terminal { get; } = new();

    /// <summary>
    /// Raised (on the UI thread, at most once per UI tick) when the terminal view should take an
    /// update from <see cref="Terminal"/> via <see cref="TerminalBuffer.TryTakeUpdate"/>.
    /// </summary>
    public event EventHandler? DisplayUpdateReady;

    // ───────────────── Commands ─────────────────

    public ICommand ApplyConfigCommand { get; }
    public ICommand ResetConfigCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ToggleConnectCommand { get; }
    public ICommand ToggleLogCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand OpenLogFileCommand { get; }
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
        IsHardwareRemoved = false;
        StatusText = "Disconnected";
        OnPropertyChanged(nameof(DisplayLabel));
        OnPropertyChanged(nameof(PortName));
    }

    private void ResetConfig()
    {
        StopLogging();
        IsConfigured = false;
        IsHardwareRemoved = false;
        StatusText = "Not configured";
        OnPropertyChanged(nameof(DisplayLabel));
    }

    // ───────────────── Logging ─────────────────

    public void StartLogging()
    {
        if (IsLogging || !IsConfigured) return;

        string logDir = _getLogDirectory();
        string templateStr = _getLogFileTemplate();
        string ext = SelectedLogFormat == LogFormat.Binary ? ".bin" : ".log";

        LogFileWriter writer;
        try
        {
            var template = new LogFileNameTemplate(templateStr, ext);
            writer = new LogFileWriter(logDir, SelectedLogFormat, template, PortName);
            writer.Start(); // creates the folder and opens the file; throws if that fails
        }
        catch (Exception ex)
        {
            StatusText = $"Log error: {ex.Message}";
            AddLine($"[--- Logging could not start: {ex.Message} ---]");
            return;
        }

        _logBytesSeen = 0;
        _lastLogFilePath = writer.CurrentFilePath;
        _logWriter = writer; // from here on the read thread queues received data to the file
        ((RelayCommand)OpenLogFileCommand).RaiseCanExecuteChanged();
        IsLogging = true;
    }

    public void StopLogging()
    {
        if (!IsLogging) return;

        var writer = _logWriter;

        // Detach first so no new data is queued, then let the writer finish everything that
        // is already queued and close the file.
        _logWriter = null;
        if (writer != null)
        {
            writer.Stop();
            SyncLogCounter(writer);
            _lastLogFilePath = writer.CurrentFilePath ?? _lastLogFilePath;
        }

        IsLogging = false;
        ((RelayCommand)OpenLogFileCommand).RaiseCanExecuteChanged();
    }

    private void ToggleLog()
    {
        if (IsLogging)
            StopLogging();
        else
            StartLogging();
    }

    /// <summary>
    /// Queues received data for the log file. Called on the serial read thread for every packet;
    /// does nothing when logging is off.
    /// </summary>
    public void EnqueueLogData(DataPacket packet)
    {
        _logWriter?.Queue.TryWrite(packet);
    }

    /// <summary>
    /// Adds the bytes the writer has written since the last call to the LOG counter.
    /// </summary>
    private void SyncLogCounter(LogFileWriter writer)
    {
        long written = writer.BytesWritten;
        if (written != _logBytesSeen)
        {
            BytesLogged += written - _logBytesSeen;
            _logBytesSeen = written;
        }
    }

    /// <summary>
    /// Updates the LOG counter and stops logging visibly if the writer failed (e.g. disk full).
    /// </summary>
    private void CheckLogWriter()
    {
        var writer = _logWriter;
        if (writer == null) return;

        SyncLogCounter(writer);

        if (writer.Fault is { } fault)
        {
            StopLogging();
            StatusText = $"Log error: {fault.Message}";
            AddLine($"[--- Logging stopped: {fault.Message} ---]");
        }
    }

    private void OpenLogFile()
    {
        if (IsLogging) return;
        string? path = _lastLogFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch { }
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

        // Cheap, no UI notifications: counters and the view are updated once per tick
        // in FlushDisplay().
        _pendingRxBytes += packet.Data.Length;
        Terminal.AppendBytes(packet.Data, _selectedDisplayFormat);
    }

    /// <summary>
    /// Called once per UI tick (UI thread): publishes counters, checks the log writer and
    /// tells the view to update if there is anything new.
    /// </summary>
    public void FlushDisplay()
    {
        if (_pendingRxBytes > 0)
        {
            BytesReceived += _pendingRxBytes;
            _pendingRxBytes = 0;
        }

        CheckLogWriter();

        // While AutoScroll is OFF the view is paused; the buffer keeps collecting (bounded).
        if (_autoScroll && Terminal.HasUpdate)
        {
            DisplayUpdateReady?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RaiseDisplayUpdate()
    {
        DisplayUpdateReady?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adds a complete informational line (TX echo, notices) to the terminal.
    /// </summary>
    private void AddLine(string line)
    {
        Terminal.AppendLine(line);
    }

    /// <summary>
    /// Adds a system/diagnostic message to the terminal monitor (e.g. disconnect/reconnect notice).
    /// Safe to call from any thread; does not affect log files.
    /// </summary>
    public void AddSystemMessage(string message)
    {
        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(() => AddSystemMessage(message));
            return;
        }

        AddLine(message);
    }

    public void ClearMonitor()
    {
        Terminal.Clear();
        _pendingRxBytes = 0;
        BytesReceived = 0;
        BytesLogged = 0;
        if (_logWriter is { } writer)
        {
            // Count only what is written from now on.
            _logBytesSeen = writer.BytesWritten;
        }
        BytesSent = 0;

        // Empty the view immediately, even while AutoScroll is OFF (paused).
        RaiseDisplayUpdate();
    }

    /// <summary>
    /// Cleanup when this panel is removed (layout shrinks).
    /// </summary>
    public void Cleanup()
    {
        StopLogging();
        IsHardwareRemoved = false;
        if (IsConnected)
        {
            _disconnectAction(this);
        }
    }
}
