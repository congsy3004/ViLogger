using System;
using System.Collections.ObjectModel;
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
    private bool _isConnected;
    private bool _isLogging;
    private string _statusText = string.Empty;
    private long _bytesReceived;
    private long _bytesLogged;
    private readonly StringBuilder _lineBuffer = new();
    private const int MaxMonitorLines = 5000;
    private const int TrimBatchSize = 500;

    public PortTabViewModel(SerialPortConfig initialConfig)
    {
        _config = initialConfig;
        MonitorLines = new ObservableCollection<string>();
        ClearCommand = new RelayCommand(ClearMonitor);
    }

    /// <summary>
    /// Friendly label: "COM3 - USB Serial Port" or just "COM3" if no description.
    /// </summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(_config.DisplayName)
        ? _config.PortName
        : $"{_config.PortName} - {_config.DisplayName}";

    public SerialPortConfig Config
    {
        get => _config;
        set => SetProperty(ref _config, value);
    }

    public string PortName
    {
        get => _config.PortName;
        set
        {
            if (_config.PortName != value)
            {
                Config = _config with { PortName = value };
                OnPropertyChanged();
            }
        }
    }

    public int BaudRate
    {
        get => _config.BaudRate;
        set
        {
            if (_config.BaudRate != value)
            {
                Config = _config with { BaudRate = value };
                OnPropertyChanged();
            }
        }
    }

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

    public bool IsConnected
    {
        get => _isConnected;
        set => SetProperty(ref _isConnected, value);
    }

    public bool IsLogging
    {
        get => _isLogging;
        set => SetProperty(ref _isLogging, value);
    }

    public ObservableCollection<string> MonitorLines { get; }

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

    public ICommand ClearCommand { get; }

    public void AppendData(DataPacket packet, LogFormat displayFormat)
    {
        if (packet.Data == null || packet.Data.Length == 0) return;
        
        BytesReceived += packet.Data.Length;
        if (IsLogging)
        {
            BytesLogged += packet.Data.Length;
        }

        switch (displayFormat)
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

    /// <summary>
    /// Handles ASCII data with proper line buffering for partial lines.
    /// </summary>
    private void AppendAsciiData(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            char c = (char)data[i];
            if (c == '\n')
            {
                // Emit the buffered line
                AddLine(_lineBuffer.ToString());
                _lineBuffer.Clear();
            }
            else if (c == '\r')
            {
                // Skip carriage return (handle \r\n as just \n)
                continue;
            }
            else
            {
                _lineBuffer.Append(c < 0x20 || c > 0x7E ? '.' : c);
            }
        }

        // If buffer gets too large without a newline, flush it
        if (_lineBuffer.Length > 4096)
        {
            AddLine(_lineBuffer.ToString());
            _lineBuffer.Clear();
        }
    }

    private void AddLine(string line)
    {
        MonitorLines.Add(line);

        // Batch trim to avoid O(N) per-item removal
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
}
