using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly ConcurrentDictionary<string, PortTabViewModel> _activePorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _reconnectCts = new(StringComparer.OrdinalIgnoreCase);

    private string _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
    private string _logFileNameTemplate = "{port}_{datetime}";
    private bool _isAllConnected;
    private bool _isAllLogging;
    private bool _autoReconnectAll;
    private string _statusBarText = "Ready";
    // No _selectedLayout field; layout is computed automatically from Ports.Count

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
        AddPortCommand = new RelayCommand(AddPort, () => Ports.Count < 4);
        ConnectAllCommand = new RelayCommand(ConnectAll, () => Ports.Any(p => p.IsConfigured && !p.IsConnected));
        DisconnectAllCommand = new RelayCommand(DisconnectAll, () => Ports.Any(p => p.IsConnected));
        LogAllOnCommand = new RelayCommand(LogAllOn, () => Ports.Any(p => p.IsConfigured && !p.IsLogging));
        LogAllOffCommand = new RelayCommand(LogAllOff, () => Ports.Any(p => p.IsLogging));
        ToggleConnectAllCommand = new RelayCommand(ToggleConnectAll, () => Ports.Any(p => p.IsConfigured));
        ToggleLogAllCommand = new RelayCommand(ToggleLogAll, () => Ports.Any(p => p.IsConfigured));
        ClearAllCommand = new RelayCommand(ClearAll);
        ToggleAutoReconnectAllCommand = new RelayCommand(() => AutoReconnectAll = !AutoReconnectAll);
        OpenLogDirectoryCommand = new RelayCommand(OpenLogDirectory);
        
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
        // Start with one port panel
        Ports.Add(CreatePortTab());
        UpdateGlobalStates();
    }

    // ═══════════════════ Collections ═══════════════════

    public ObservableCollection<PortTabViewModel> Ports { get; }
    public ObservableCollection<PortInfo> AvailablePorts { get; }
    public ObservableCollection<int> CommonBaudRates { get; }
    // ═══════════════════ Properties ═══════════════════

    /// <summary>
    /// Columns: 1 port=1, 2 ports=2 (side by side), 3 ports=3 (side by side), 4 ports=2 (2×2).
    /// </summary>
    public int LayoutColumns => Ports.Count <= 1 ? 1 : (Ports.Count <= 3 ? Ports.Count : 2);

    /// <summary>
    /// Rows: 1–3 ports stay in 1 row, 4 ports use 2 rows (2×2).
    /// </summary>
    public int LayoutRows => Ports.Count <= 3 ? 1 : 2;

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

    public bool AutoReconnectAll
    {
        get => _autoReconnectAll;
        set
        {
            if (SetProperty(ref _autoReconnectAll, value))
            {
                foreach (var port in Ports)
                {
                    port.AutoReconnect = value;
                }
            }
        }
    }

    public string StatusBarText
    {
        get => _statusBarText;
        set => SetProperty(ref _statusBarText, value);
    }

    // ═══════════════════ Commands ═══════════════════

    public ICommand AddPortCommand { get; }
    public ICommand ConnectAllCommand { get; }
    public ICommand DisconnectAllCommand { get; }
    public ICommand LogAllOnCommand { get; }
    public ICommand LogAllOffCommand { get; }
    public ICommand ToggleConnectAllCommand { get; }
    public ICommand ToggleLogAllCommand { get; }
    public ICommand ClearAllCommand { get; }
    public ICommand ToggleAutoReconnectAllCommand { get; }
    public ICommand OpenLogDirectoryCommand { get; }
    public ICommand BrowseLogDirectoryCommand { get; }

    // ═══════════════════ Port Management ═══════════════════

    /// <summary>
    /// Adds a new empty port panel (max 4). Layout is recalculated automatically.
    /// </summary>
    private void AddPort()
    {
        if (Ports.Count >= 4) return;
        RefreshPorts(); // Auto-rescan so newly plugged devices appear in the new port's dropdown
        Ports.Add(CreatePortTab());
        OnPropertyChanged(nameof(LayoutColumns));
        OnPropertyChanged(nameof(LayoutRows));
        ((RelayCommand)AddPortCommand).RaiseCanExecuteChanged();
        // Refresh each tab's RemoveThisPortCommand so it enables when count > 1
        foreach (var p in Ports)
            ((RelayCommand)p.RemoveThisPortCommand).RaiseCanExecuteChanged();
        UpdateGlobalStates();
    }

    /// <summary>
    /// Removes a specific port panel. Called by each panel's close button.
    /// </summary>
    private void RemovePort(PortTabViewModel tab)
    {
        if (Ports.Count <= 1) return;
        CancelAutoReconnect(tab.PortName);
        tab.Cleanup();
        if (!string.IsNullOrEmpty(tab.PortName))
        {
            _activePorts.TryRemove(tab.PortName, out _);
            _portManager.RemovePort(tab.PortName);
        }
        Ports.Remove(tab);
        OnPropertyChanged(nameof(LayoutColumns));
        OnPropertyChanged(nameof(LayoutRows));
        ((RelayCommand)AddPortCommand).RaiseCanExecuteChanged();
        // Refresh remaining tabs' RemoveThisPortCommand (disable when only 1 left)
        foreach (var p in Ports)
            ((RelayCommand)p.RemoveThisPortCommand).RaiseCanExecuteChanged();
        UpdateGlobalStates();
    }

    private PortTabViewModel CreatePortTab()
    {
        var tab = new PortTabViewModel(
            AvailablePorts,
            CommonBaudRates,
            connectAction: ConnectPort,
            disconnectAction: DisconnectPort,
            sendAction: SendToPort,
            removeAction: RemovePort,
            canRemove: () => Ports.Count > 1,
            getLogDirectory: () => LogDirectory,
            getLogFileTemplate: () => LogFileNameTemplate);

        tab.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(PortTabViewModel.IsConfigured))
            {
                if (tab.IsConfigured && !string.IsNullOrEmpty(tab.PortName))
                {
                    _activePorts[tab.PortName] = tab;
                }
                else if (!tab.IsConfigured && !string.IsNullOrEmpty(tab.PortName))
                {
                    _activePorts.TryRemove(tab.PortName, out _);
                    _portManager.RemovePort(tab.PortName);
                }
                UpdateGlobalStates();
            }
            else if (e.PropertyName == nameof(PortTabViewModel.IsConnected) ||
                     e.PropertyName == nameof(PortTabViewModel.IsLogging))
            {
                UpdateGlobalStates();
            }
            else if (e.PropertyName == nameof(PortTabViewModel.AutoReconnect))
            {
                if (!tab.AutoReconnect)
                {
                    CancelAutoReconnect(tab.PortName);
                    if (!tab.IsConnected && (tab.StatusText == "Reconnecting..." || tab.StatusText == "Waiting for port..."))
                    {
                        tab.StatusText = "Disconnected";
                    }
                }
                else if (tab.IsConfigured && !tab.IsConnected && tab.StatusText.StartsWith("Error"))
                {
                    StartAutoReconnect(tab);
                }
                UpdateGlobalStates();
            }
        };

        return tab;
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
            if (_portManager.Connections.ContainsKey(tab.PortName))
            {
                _portManager.RemovePort(tab.PortName);
            }
            _portManager.AddPort(tab.Config);
            _activePorts[tab.PortName] = tab;
            _portManager.OpenPort(tab.PortName);
            if (_portManager.Connections.TryGetValue(tab.PortName, out var conn) && conn.IsOpen)
            {
                tab.IsConnected = true;
                tab.StatusText = "Connected";
                CancelAutoReconnect(tab.PortName);
            }
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
        CancelAutoReconnect(tab.PortName);
        try
        {
            _portManager.ClosePort(tab.PortName);
            _portManager.RemovePort(tab.PortName);
            tab.IsConnected = false;
            tab.StatusText = "Disconnected";
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
        foreach (var tab in Ports)
        {
            CancelAutoReconnect(tab.PortName);
            tab.IsConnected = false;
            tab.StatusText = tab.IsConfigured ? "Disconnected" : "Not configured";
        }
        _portManager.CloseAll();
        IsAllConnected = false;
        StatusBarText = "All ports disconnected";
    }

    private void ClearAll()
    {
        foreach (var tab in Ports)
        {
            tab.ClearMonitor();
        }
        StatusBarText = "All port monitors cleared";
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

    private void ToggleConnectAll()
    {
        if (IsAllConnected)
        {
            DisconnectAll();
        }
        else
        {
            ConnectAll();
        }
    }

    private void ToggleLogAll()
    {
        if (IsAllLogging)
        {
            LogAllOff();
        }
        else
        {
            LogAllOn();
        }
    }

    private void OpenLogDirectory()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = LogDirectory,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch (Exception ex)
        {
            StatusBarText = $"Failed to open directory: {ex.Message}";
        }
    }

    // ═══════════════════ Event Handlers ═══════════════════

    private void PortManager_DataReceived(DataPacket packet)
    {
        _dataQueue.Enqueue(packet);

        if (_activePorts.TryGetValue(packet.PortName, out var tab) && tab.IsLogging)
        {
            tab.EnqueueLogData(packet);
        }
    }

    private void PortManager_ConnectionStateChanged(string portName)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_activePorts.TryGetValue(portName, out var tab))
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
            PortTabViewModel? tab = Ports.FirstOrDefault(p => string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase));
            if (tab == null)
            {
                _activePorts.TryGetValue(portName, out tab);
            }

            if (tab != null)
            {
                tab.IsConnected = false;
                if (tab.IsLogging) tab.StopLogging();
            }

            // Clean up the stale connection so the user or auto-reconnect can reconnect cleanly.
            try { _portManager.ClosePort(portName); } catch { }
            try { _portManager.RemovePort(portName); } catch { }
            _activePorts.TryRemove(portName, out _);

            UpdateGlobalStates();

            // If auto-reconnect is already retrying for this port, don't spawn duplicate tasks
            if (_reconnectCts.ContainsKey(portName))
            {
                if (tab != null) tab.StatusText = "Reconnecting...";
                return;
            }

            if (tab != null && tab.AutoReconnect)
            {
                StartAutoReconnect(tab);
            }
            else
            {
                if (tab != null) tab.StatusText = $"Error: {ex.Message}";
                StatusBarText = $"Port {portName} disconnected unexpectedly. Click Connect to retry.";
            }
        });
    }

    private void StartAutoReconnect(PortTabViewModel tab)
    {
        string portName = tab.PortName;
        if (string.IsNullOrEmpty(portName)) return;

        CancelAutoReconnect(portName);

        var cts = new CancellationTokenSource();
        _reconnectCts[portName] = cts;
        var token = cts.Token;

        tab.StatusText = "Reconnecting...";
        StatusBarText = $"Port {portName} disconnected. Auto re-connecting...";

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1500, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (token.IsCancellationRequested) break;

                bool shouldContinue = false;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    shouldContinue = tab.IsConfigured && tab.AutoReconnect && !tab.IsConnected;
                });

                if (!shouldContinue)
                {
                    break;
                }

                bool portExists = false;
                try
                {
                    var available = SerialPortManager.GetAvailablePorts();
                    portExists = available.Any(p => string.Equals(p, portName, StringComparison.OrdinalIgnoreCase));
                }
                catch { }

                if (!portExists)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            tab.StatusText = "Waiting for port...";
                            StatusBarText = $"Waiting for {portName} to become available...";
                        }
                    });
                    continue;
                }

                bool connected = false;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || tab.IsConnected || !tab.IsConfigured) return;

                    tab.StatusText = "Connecting...";
                    ConnectPort(tab);
                    connected = tab.IsConnected;
                });

                if (connected)
                {
                    _reconnectCts.TryRemove(portName, out _);
                    break;
                }
            }
        }, token);
    }

    private void CancelAutoReconnect(string portName)
    {
        if (string.IsNullOrEmpty(portName)) return;

        if (_reconnectCts.TryRemove(portName, out var cts))
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch { }
        }
    }

    private void UpdateGlobalStates()
    {
        var configured = Ports.Where(p => p.IsConfigured).ToList();
        IsAllConnected = configured.Any() && configured.All(p => p.IsConnected);
        IsAllLogging = configured.Any(p => p.IsLogging);
        _autoReconnectAll = configured.Any() && configured.All(p => p.AutoReconnect);
        OnPropertyChanged(nameof(AutoReconnectAll));

        ((RelayCommand)ToggleConnectAllCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ToggleLogAllCommand).RaiseCanExecuteChanged();
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
            if (_activePorts.TryGetValue(packet.PortName, out var tab))
            {
                tab.AppendData(packet);
            }
            packetsProcessed++;
        }
    }

    /// <summary>
    /// Performs graceful shutdown: flushes all log files and closes all ports.
    /// </summary>
    public void Shutdown()
    {
        _uiTimer.Stop();
        foreach (var cts in _reconnectCts.Values)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { }
        }
        _reconnectCts.Clear();

        foreach (var tab in Ports)
        {
            tab.Cleanup();
        }
        _portManager.Dispose();
    }
}
