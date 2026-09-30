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

    // Callbacks to MainViewModel
    private readonly Action<PortTabViewModel> _connectAction;
    private readonly Action<PortTabViewModel> _disconnectAction;
    private readonly Func<string> _getLogDirectory;
    private readonly Func<string> _getLogFileTemplate;

    // Shared collections from MainViewModel
    public ObservableCollection<PortInfo> AvailablePorts { get; }
    public ObservableCollection<int> CommonBaudRates { get; }

    public PortTabViewModel(
        ObservableCollection<PortInfo> availablePorts,
        ObservableCollection<int> commonBaudRates,
        Action<PortTabViewModel> connectAction,
        Action<PortTabViewModel> disconnectAction,
        Func<string> getLogDirectory,
        Func<string> getLogFileTemplate)
    {
        _config = new SerialPortConfig();
        _connectAction = connectAction;
        _disconnectAction = disconnectAction;
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
        ToggleLogCommand = new RelayCommand(ToggleLog, () => IsConfigured);
        ClearCommand = new RelayCommand(ClearMonitor);
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

    // ───────────────── Display Properties ─────────────────

    public string DisplayLabel => !_isConfigured
        ? "Not Configured"
        : (string.IsNullOrWhiteSpace(_config.DisplayName)
            ? _config.PortName
            : $"{_config.PortName} - {_config.DisplayName}");

    public LogFormat[] LogFormatValues { get; } = Enum.GetValues<LogFormat>();

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
                ((RelayCommand)ResetConfigCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsLogging
    {
        get => _isLogging;
        set => SetProperty(ref _isLogging, value);
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
    public ICommand ToggleLogCommand { get; }
    public ICommand ClearCommand { get; }

    public string LogButtonText => IsLogging ? "Log OFF" : "Log ON";

    // ───────────────── Config Actions ─────────────────

    private void ApplyConfig()
    {
        if (_selectedPort == null) return;

        Config = new SerialPortConfig
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

    public void StartLogging()
    {
        if (IsLogging || !IsConfigured) return;

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
        OnPropertyChanged(nameof(LogButtonText));
    }

    public void StopLogging()
    {
        if (!IsLogging) return;

        _logWriter?.Stop();
        _logWriter = null;
        IsLogging = false;
        OnPropertyChanged(nameof(LogButtonText));
    }

    private void ToggleLog()
    {
        if (IsLogging)
            StopLogging();
        else
            StartLogging();
    }

    public void EnqueueLogData(DataPacket packet)
    {
        _logWriter?.Queue.TryWrite(packet);
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
        MonitorLines.Add(line);

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
