using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using EverLogger.App.Helpers;
using EverLogger.Core.Data;
using EverLogger.Core.Logging;
using EverLogger.Core.Serial;

namespace EverLogger.App.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly SerialPortManager _portManager;
    private LogSession? _logSession;
    private readonly DispatcherTimer _uiTimer;
    private readonly ConcurrentQueue<DataPacket> _dataQueue;

    private PortTabViewModel? _selectedPort;
    private PortInfo? _selectedNewPort;
    private int _selectedBaudRate = 115200;
    private LogFormat _selectedLogFormat = LogFormat.Ascii;
    private LogFormat _selectedFileLogFormat = LogFormat.Ascii;
    private string _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
    private string _logFileNameTemplate = "{port}_{datetime}";
    private string _logFileExtension = ".log";
    private bool _isAllConnected;
    private bool _isAllLogging;
    private string _statusBarText = "Ready";
    private bool _autoScroll = true;
    private LayoutOption _selectedLayout;

    public MainViewModel()
    {
        _portManager = new SerialPortManager();
        _portManager.DataReceived += PortManager_DataReceived;
        _portManager.ConnectionStateChanged += PortManager_ConnectionStateChanged;
        _portManager.ErrorOccurred += PortManager_ErrorOccurred;
        
        _dataQueue = new ConcurrentQueue<DataPacket>();
        
        Ports = new ObservableCollection<PortTabViewModel>();
        AvailablePorts = new ObservableCollection<PortInfo>();
        CommonBaudRates = new ObservableCollection<int> { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };
        LayoutOptions = new ObservableCollection<LayoutOption>
        {
            new LayoutOption(1, 1),
            new LayoutOption(2, 1),
            new LayoutOption(1, 2),
            new LayoutOption(2, 2),
        };
        _selectedLayout = LayoutOptions[0];

        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        AddPortCommand = new RelayCommand(AddPort, () => SelectedNewPort != null);
        RemovePortCommand = new RelayCommand(RemovePort, () => SelectedPort != null);
        ConnectAllCommand = new RelayCommand(ConnectAll, () => Ports.Any());
        DisconnectAllCommand = new RelayCommand(DisconnectAll, () => Ports.Any() && Ports.Any(p => p.IsConnected));
        StartLoggingCommand = new RelayCommand(StartLogging, () => Ports.Any() && !IsAllLogging);
        StopLoggingCommand = new RelayCommand(StopLogging, () => IsAllLogging);
        ClearAllCommand = new RelayCommand(ClearAll);
        
        // Simple folder browser placeholder since standard WPF doesn't have an easy modern one without extra packages
        BrowseLogDirectoryCommand = new RelayCommand(() =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Log Directory",
                InitialDirectory = LogDirectory
            };
            if (dialog.ShowDialog() == true)
            {
                LogDirectory = dialog.FolderName;
            }
        });

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // ~30fps
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();

        RefreshPorts();
    }

    public ObservableCollection<PortTabViewModel> Ports { get; }
    public ObservableCollection<PortInfo> AvailablePorts { get; }
    public ObservableCollection<int> CommonBaudRates { get; }
    public ObservableCollection<LayoutOption> LayoutOptions { get; }
    public LogFormat[] LogFormatValues { get; } = Enum.GetValues<LogFormat>();

    public PortTabViewModel? SelectedPort
    {
        get => _selectedPort;
        set
        {
            if (SetProperty(ref _selectedPort, value))
            {
                ((RelayCommand)RemovePortCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public PortInfo? SelectedNewPort
    {
        get => _selectedNewPort;
        set
        {
            if (SetProperty(ref _selectedNewPort, value))
            {
                ((RelayCommand)AddPortCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public LayoutOption SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            if (SetProperty(ref _selectedLayout, value))
            {
                OnPropertyChanged(nameof(LayoutColumns));
                OnPropertyChanged(nameof(LayoutRows));
            }
        }
    }

    public int LayoutColumns => _selectedLayout.Columns;
    public int LayoutRows => _selectedLayout.Rows;

    public int SelectedBaudRate
    {
        get => _selectedBaudRate;
        set => SetProperty(ref _selectedBaudRate, value);
    }

    public LogFormat SelectedLogFormat
    {
        get => _selectedLogFormat;
        set
        {
            if (SetProperty(ref _selectedLogFormat, value))
            {
                LogFileExtension = value == LogFormat.Binary ? ".bin" : ".log";
            }
        }
    }

    public LogFormat SelectedFileLogFormat
    {
        get => _selectedFileLogFormat;
        set => SetProperty(ref _selectedFileLogFormat, value);
    }

    public string LogDirectory
    {
        get => _logDirectory;
        set => SetProperty(ref _logDirectory, value);
    }

    public string LogFileNameTemplate
    {
        get => _logFileNameTemplate;
        set => SetProperty(ref _logFileNameTemplate, value);
    }

    public string LogFileExtension
    {
        get => _logFileExtension;
        set => SetProperty(ref _logFileExtension, value);
    }

    public bool IsAllConnected
    {
        get => _isAllConnected;
        set
        {
            if (SetProperty(ref _isAllConnected, value))
            {
                ((RelayCommand)ConnectAllCommand).RaiseCanExecuteChanged();
                ((RelayCommand)DisconnectAllCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsAllLogging
    {
        get => _isAllLogging;
        set
        {
            if (SetProperty(ref _isAllLogging, value))
            {
                ((RelayCommand)StartLoggingCommand).RaiseCanExecuteChanged();
                ((RelayCommand)StopLoggingCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusBarText
    {
        get => _statusBarText;
        set => SetProperty(ref _statusBarText, value);
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public ICommand RefreshPortsCommand { get; }
    public ICommand AddPortCommand { get; }
    public ICommand RemovePortCommand { get; }
    public ICommand ConnectAllCommand { get; }
    public ICommand DisconnectAllCommand { get; }
    public ICommand StartLoggingCommand { get; }
    public ICommand StopLoggingCommand { get; }
    public ICommand BrowseLogDirectoryCommand { get; }
    public ICommand ClearAllCommand { get; }

    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        try
        {
            foreach (var (portName, description) in SerialPortManager.GetAvailablePortDescriptions())
            {
                AvailablePorts.Add(new PortInfo(portName, description));
            }
        }
        catch
        {
            // Fallback to simple port names if descriptions fail
            foreach (var port in SerialPortManager.GetAvailablePorts())
            {
                AvailablePorts.Add(new PortInfo(port, ""));
            }
        }
        if (AvailablePorts.Any())
        {
            SelectedNewPort = AvailablePorts.First();
        }
    }

    private void AddPort()
    {
        if (SelectedNewPort == null) return;
        
        var config = new SerialPortConfig
        {
            PortName = SelectedNewPort.PortName,
            BaudRate = SelectedBaudRate,
            DisplayName = SelectedNewPort.Description
        };
        
        var tab = new PortTabViewModel(config);
        Ports.Add(tab);
        SelectedPort = tab;
        
        ((RelayCommand)ConnectAllCommand).RaiseCanExecuteChanged();
        ((RelayCommand)StartLoggingCommand).RaiseCanExecuteChanged();
    }

    private void RemovePort()
    {
        if (SelectedPort != null)
        {
            string portName = SelectedPort.PortName;
            if (SelectedPort.IsConnected)
            {
                _portManager.ClosePort(portName);
            }
            _portManager.RemovePort(portName);
            if (IsAllLogging && _logSession != null)
            {
                _logSession.RemovePort(portName);
            }
            Ports.Remove(SelectedPort);
            SelectedPort = Ports.FirstOrDefault();
            UpdateGlobalStates();
        }
    }

    private void ConnectAll()
    {
        int failed = 0;
        foreach (var tab in Ports)
        {
            try
            {
                if (!_portManager.Connections.ContainsKey(tab.PortName))
                {
                    _portManager.AddPort(tab.Config);
                }
            }
            catch (Exception ex)
            {
                tab.StatusText = $"Error: {ex.Message}";
                failed++;
            }
        }
        
        _portManager.OpenAll();
        StatusBarText = failed > 0 ? $"Connected with {failed} error(s)" : "All ports connected";
    }

    private void DisconnectAll()
    {
        _portManager.CloseAll();
        foreach (var tab in Ports)
        {
            tab.IsConnected = false;
            tab.StatusText = "Disconnected";
        }
        IsAllConnected = false;
        StatusBarText = "All ports disconnected";
    }

    private void StartLogging()
    {
        if (!Directory.Exists(LogDirectory))
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
            }
            catch (Exception ex)
            {
                StatusBarText = $"Failed to create log directory: {ex.Message}";
                return;
            }
        }

        var template = new LogFileNameTemplate(LogFileNameTemplate, LogFileExtension);
        _logSession = new LogSession(LogDirectory, SelectedFileLogFormat, template);

        foreach (var tab in Ports)
        {
            _logSession.AddPort(tab.PortName);
            tab.IsLogging = true;
        }

        _logSession.StartAll();
        IsAllLogging = true;
        StatusBarText = "Logging started";
    }

    private void StopLogging()
    {
        if (_logSession != null)
        {
            _logSession.StopAll();
            _logSession = null;
        }

        foreach (var tab in Ports)
        {
            tab.IsLogging = false;
        }

        IsAllLogging = false;
        StatusBarText = "Logging stopped";
    }

    private void ClearAll()
    {
        foreach (var tab in Ports)
        {
            tab.ClearMonitor();
        }
    }

    private void PortManager_DataReceived(DataPacket packet)
    {
        _dataQueue.Enqueue(packet);
        var session = _logSession;
        if (IsAllLogging && session != null && session.IsRunning)
        {
            session.EnqueueData(packet);
        }
    }

    private void PortManager_ConnectionStateChanged(string portName)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var tab = Ports.FirstOrDefault(p => p.PortName == portName);
            if (tab != null)
            {
                bool isOpen = _portManager.Connections.TryGetValue(portName, out var conn) && conn.IsOpen;
                tab.IsConnected = isOpen;
                tab.StatusText = isOpen ? "Connected" : "Disconnected";
            }
            UpdateGlobalStates();
        });
    }

    private void PortManager_ErrorOccurred(string portName, Exception ex)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var tab = Ports.FirstOrDefault(p => p.PortName == portName);
            if (tab != null)
            {
                tab.StatusText = $"Error: {ex.Message}";
                tab.IsConnected = false;
            }
            StatusBarText = $"Error on {portName}: {ex.Message}";
            UpdateGlobalStates();
        });
    }

    private void UpdateGlobalStates()
    {
        if (Ports.Count == 0)
        {
            IsAllConnected = false;
        }
        else
        {
            IsAllConnected = Ports.All(p => p.IsConnected);
        }
        ((RelayCommand)ConnectAllCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DisconnectAllCommand).RaiseCanExecuteChanged();
        ((RelayCommand)StartLoggingCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RemovePortCommand).RaiseCanExecuteChanged();
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        int packetsProcessed = 0;
        // Drain entire queue per tick, cap at 5000 to avoid UI stall
        while (packetsProcessed < 5000 && _dataQueue.TryDequeue(out var packet))
        {
            var tab = Ports.FirstOrDefault(p => p.PortName == packet.PortName);
            if (tab != null)
            {
                tab.AppendData(packet, SelectedLogFormat);
            }
            packetsProcessed++;
        }
    }
}
