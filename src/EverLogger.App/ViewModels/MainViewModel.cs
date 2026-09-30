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
    private readonly DispatcherTimer _uiTimer;
    private readonly ConcurrentQueue<DataPacket> _dataQueue;

    private string _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
    private string _logFileNameTemplate = "{port}_{datetime}";
    private bool _isAllConnected;
    private bool _isAllLogging;
    private string _statusBarText = "Ready";
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
            new LayoutOption(3, 1),
            new LayoutOption(2, 2),
        };
        _selectedLayout = LayoutOptions[0];

        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        ConnectAllCommand = new RelayCommand(ConnectAll, () => Ports.Any(p => p.IsConfigured && !p.IsConnected));
        DisconnectAllCommand = new RelayCommand(DisconnectAll, () => Ports.Any(p => p.IsConnected));
        LogAllOnCommand = new RelayCommand(LogAllOn, () => Ports.Any(p => p.IsConfigured && !p.IsLogging));
        LogAllOffCommand = new RelayCommand(LogAllOff, () => Ports.Any(p => p.IsLogging));
        
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

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();

        RefreshPorts();
        ApplyLayout(); // Create initial port panels
    }

    // ═══════════════════ Collections ═══════════════════

    public ObservableCollection<PortTabViewModel> Ports { get; }
    public ObservableCollection<PortInfo> AvailablePorts { get; }
    public ObservableCollection<int> CommonBaudRates { get; }
    public ObservableCollection<LayoutOption> LayoutOptions { get; }

    // ═══════════════════ Properties ═══════════════════

    public LayoutOption SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            if (SetProperty(ref _selectedLayout, value))
            {
                OnPropertyChanged(nameof(LayoutColumns));
                OnPropertyChanged(nameof(LayoutRows));
                ApplyLayout();
            }
        }
    }

    public int LayoutColumns => _selectedLayout.Columns;
    public int LayoutRows => _selectedLayout.Rows;

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

    public bool IsAllConnected
    {
        get => _isAllConnected;
        set => SetProperty(ref _isAllConnected, value);
    }

    public bool IsAllLogging
    {
        get => _isAllLogging;
        set => SetProperty(ref _isAllLogging, value);
    }

    public string StatusBarText
    {
        get => _statusBarText;
        set => SetProperty(ref _statusBarText, value);
    }

    // ═══════════════════ Commands ═══════════════════

    public ICommand RefreshPortsCommand { get; }
    public ICommand ConnectAllCommand { get; }
    public ICommand DisconnectAllCommand { get; }
    public ICommand LogAllOnCommand { get; }
    public ICommand LogAllOffCommand { get; }
    public ICommand BrowseLogDirectoryCommand { get; }

    // ═══════════════════ Layout Management ═══════════════════

    /// <summary>
    /// Adjusts the Ports collection to match the selected layout's port count.
    /// Adds empty panels or removes excess panels as needed.
    /// </summary>
    private void ApplyLayout()
    {
        int target = _selectedLayout.PortCount;

        // Remove excess panels from the end
        while (Ports.Count > target)
        {
            var last = Ports[Ports.Count - 1];
            last.Cleanup();
            if (last.IsConfigured)
            {
                _portManager.RemovePort(last.PortName);
            }
            Ports.RemoveAt(Ports.Count - 1);
        }

        // Add empty (unconfigured) panels
        while (Ports.Count < target)
        {
            Ports.Add(CreatePortTab());
        }

        UpdateGlobalStates();
    }

    private PortTabViewModel CreatePortTab()
    {
        return new PortTabViewModel(
            AvailablePorts,
            CommonBaudRates,
            connectAction: ConnectPort,
            disconnectAction: DisconnectPort,
            sendAction: SendToPort,
            getLogDirectory: () => LogDirectory,
            getLogFileTemplate: () => LogFileNameTemplate);
    }

    // ═══════════════════ Port Refresh ═══════════════════

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
            foreach (var port in SerialPortManager.GetAvailablePorts())
            {
                AvailablePorts.Add(new PortInfo(port, ""));
            }
        }
    }

    // ═══════════════════ Per-Port Callbacks ═══════════════════

    private void ConnectPort(PortTabViewModel tab)
    {
        try
        {
            if (!_portManager.Connections.ContainsKey(tab.PortName))
            {
                _portManager.AddPort(tab.Config);
            }
            _portManager.OpenPort(tab.PortName);
            StatusBarText = $"{tab.PortName} connected";
        }
        catch (Exception ex)
        {
            tab.StatusText = $"Error: {ex.Message}";
            StatusBarText = $"Failed to connect {tab.PortName}: {ex.Message}";
        }
    }

    private void DisconnectPort(PortTabViewModel tab)
    {
        try
        {
            _portManager.ClosePort(tab.PortName);
            _portManager.RemovePort(tab.PortName);
            StatusBarText = $"{tab.PortName} disconnected";
        }
        catch (Exception ex)
        {
            tab.StatusText = $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Called by PortTabViewModel.SendCommand via callback.
    /// </summary>
    private void SendToPort(PortTabViewModel tab, byte[] data)
    {
        _portManager.WriteToPort(tab.PortName, data);
    }

    // ═══════════════════ Global Actions ═══════════════════

    private void ConnectAll()
    {
        foreach (var tab in Ports)
        {
            if (tab.IsConfigured && !tab.IsConnected)
            {
                ConnectPort(tab);
            }
        }
        StatusBarText = "All configured ports connected";
    }

    private void DisconnectAll()
    {
        _portManager.CloseAll();
        foreach (var tab in Ports)
        {
            tab.IsConnected = false;
            tab.StatusText = tab.IsConfigured ? "Disconnected" : "Not configured";
        }
        IsAllConnected = false;
        StatusBarText = "All ports disconnected";
    }

    private void LogAllOn()
    {
        foreach (var tab in Ports)
        {
            if (tab.IsConfigured && !tab.IsLogging)
            {
                tab.StartLogging();
            }
        }
        UpdateGlobalStates();
        StatusBarText = "Logging started on all configured ports";
    }

    private void LogAllOff()
    {
        foreach (var tab in Ports)
        {
            tab.StopLogging();
        }
        UpdateGlobalStates();
        StatusBarText = "Logging stopped on all ports";
    }

    // ═══════════════════ Event Handlers ═══════════════════

    private void PortManager_DataReceived(DataPacket packet)
    {
        _dataQueue.Enqueue(packet);

        var tab = Ports.FirstOrDefault(p => p.IsConfigured && p.PortName == packet.PortName);
        if (tab != null && tab.IsLogging)
        {
            tab.EnqueueLogData(packet);
        }
    }

    private void PortManager_ConnectionStateChanged(string portName)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var tab = Ports.FirstOrDefault(p => p.IsConfigured && p.PortName == portName);
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
            var tab = Ports.FirstOrDefault(p => p.IsConfigured && p.PortName == portName);
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
        var configured = Ports.Where(p => p.IsConfigured).ToList();
        IsAllConnected = configured.Any() && configured.All(p => p.IsConnected);
        IsAllLogging = configured.Any(p => p.IsLogging);

        ((RelayCommand)ConnectAllCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DisconnectAllCommand).RaiseCanExecuteChanged();
        ((RelayCommand)LogAllOnCommand).RaiseCanExecuteChanged();
        ((RelayCommand)LogAllOffCommand).RaiseCanExecuteChanged();
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        int packetsProcessed = 0;
        while (packetsProcessed < 5000 && _dataQueue.TryDequeue(out var packet))
        {
            var tab = Ports.FirstOrDefault(p => p.IsConfigured && p.PortName == packet.PortName);
            if (tab != null)
            {
                tab.AppendData(packet);
            }
            packetsProcessed++;
        }
    }
}
