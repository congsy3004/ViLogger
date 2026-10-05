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
    private const int UiTickMs = 50;
    private const int MaxPacketsPerTick = 100_000;

    private readonly SerialPortManager _portManager;
    private readonly DispatcherTimer _uiTimer;
    private readonly ConcurrentQueue<DataPacket> _dataQueue;
    private readonly ConcurrentDictionary<string, PortTabViewModel> _activePorts = new(StringComparer.OrdinalIgnoreCase);

    private string _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
    private string _logFileNameTemplate = "{port}_{datetime}";
    private bool _isAllConnected;
    private bool _isAllLogging;
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

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(UiTickMs) };
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
                tab.IsHardwareRemoved = false;
                tab.IsConnected = true;
                tab.StatusText = "Connected";
                tab.AddSystemMessage($"[--- {tab.PortName} Connected ---]");
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
        try
        {
            _portManager.ClosePort(tab.PortName);
            _portManager.RemovePort(tab.PortName);
            tab.IsHardwareRemoved = false;
            tab.IsConnected = false;
            tab.StatusText = "Disconnected";
            tab.AddSystemMessage($"[--- {tab.PortName} Disconnected ---]");
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

    /// <summary>
    /// Serial read thread. Each packet goes to two independent consumers:
    /// the log writer (lossless, if logging) and the display queue (drained by the UI timer).
    /// </summary>
    private void PortManager_DataReceived(DataPacket packet)
    {
        if (_activePorts.TryGetValue(packet.PortName, out var tab))
        {
            tab.EnqueueLogData(packet); // no-op when logging is off
        }

        _dataQueue.Enqueue(packet);
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

            bool portStillExists = false;
            try
            {
                var available = SerialPortManager.GetAvailablePorts();
                portStillExists = available.Any(p => string.Equals(p, portName, StringComparison.OrdinalIgnoreCase));
            }
            catch { }

            if (tab != null)
            {
                bool wasConnected = tab.IsConnected;
                tab.IsConnected = false;
                if (!portStillExists)
                {
                    tab.IsHardwareRemoved = true;
                    if (wasConnected)
                    {
                        tab.AddSystemMessage($"[--- {portName} Disconnected (Hardware removed) ---]");
                    }
                }
                else
                {
                    if (wasConnected)
                    {
                        tab.AddSystemMessage($"[--- {portName} Disconnected ({ex.Message}) ---]");
                    }
                }

                tab.StatusText = !portStillExists ? "Disconnected (Hardware removed)" : $"Error: {ex.Message}";
                if (tab.IsLogging) tab.StopLogging();
            }

            StatusBarText = !portStillExists
                ? $"Port {portName}: Hardware removed / disconnected."
                : $"Port {portName} disconnected unexpectedly. Click Connect to retry.";

            // Clean up the stale connection so the user can reconnect by clicking the button.
            // ClosePort/RemovePort are wrapped in try/catch because the port may already
            // be in an unusable state (the very reason ErrorOccurred fired).
            try { _portManager.ClosePort(portName); } catch { }
            try { _portManager.RemovePort(portName); } catch { }
            _activePorts.TryRemove(portName, out _);

            UpdateGlobalStates();
        });
    }

    /// <summary>
    /// Called when Windows notifies WM_DEVICECHANGE (USB serial device plugged in or removed).
    /// Updates the hardware-removed indication of configured ports and refreshes the port list.
    /// </summary>
    public void OnDeviceChanged()
    {
        string[] available;
        try
        {
            available = SerialPortManager.GetAvailablePorts();
        }
        catch
        {
            return;
        }

        // WM_DEVICECHANGE fires for any device. Only rescan the port list when the set of
        // COM ports actually changed, so dropdown selections are not reset needlessly.
        var currentNames = AvailablePorts.Select(p => p.PortName);
        if (!currentNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(available.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            RefreshPorts();
        }

        foreach (var tab in Ports)
        {
            if (!tab.IsConfigured || string.IsNullOrEmpty(tab.PortName))
                continue;

            bool portExists = available.Any(p => string.Equals(p, tab.PortName, StringComparison.OrdinalIgnoreCase));

            if (!portExists && !tab.IsHardwareRemoved)
            {
                tab.IsHardwareRemoved = true;
                tab.StatusText = "Disconnected (Hardware removed)";
                StatusBarText = $"Port {tab.PortName}: Hardware removed / disconnected.";

                if (tab.IsConnected)
                {
                    tab.AddSystemMessage($"[--- {tab.PortName} Disconnected (Hardware removed) ---]");
                    tab.IsConnected = false;
                    if (tab.IsLogging) tab.StopLogging();

                    try { _portManager.ClosePort(tab.PortName); } catch { }
                    try { _portManager.RemovePort(tab.PortName); } catch { }
                    _activePorts.TryRemove(tab.PortName, out _);

                    UpdateGlobalStates();
                }
            }
            else if (portExists && tab.IsHardwareRemoved)
            {
                tab.IsHardwareRemoved = false;
                if (!tab.IsConnected)
                {
                    tab.StatusText = "Disconnected";
                    StatusBarText = $"Port {tab.PortName}: Hardware detected. Ready to connect.";
                }
            }
        }
    }

    private void UpdateGlobalStates()
    {
        var configured = Ports.Where(p => p.IsConfigured).ToList();
        IsAllConnected = configured.Any() && configured.All(p => p.IsConnected);
        IsAllLogging = configured.Any(p => p.IsLogging);

        ((RelayCommand)ToggleConnectAllCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ToggleLogAllCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ConnectAllCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DisconnectAllCommand).RaiseCanExecuteChanged();
        ((RelayCommand)LogAllOnCommand).RaiseCanExecuteChanged();
        ((RelayCommand)LogAllOffCommand).RaiseCanExecuteChanged();
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        // 1) Move received data into each port's text buffer. This does no UI work per packet,
        //    so draining everything is cheap; the cap only guards against a pathological backlog.
        int packetsProcessed = 0;
        while (packetsProcessed < MaxPacketsPerTick && _dataQueue.TryDequeue(out var packet))
        {
            packetsProcessed++;
            if (_activePorts.TryGetValue(packet.PortName, out var tab))
            {
                try
                {
                    tab.AppendData(packet);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Display error ({packet.PortName}): {ex}");
                }
            }
        }

        // 2) One UI update per port per tick: counters, log health, terminal text.
        foreach (var tab in Ports)
        {
            try
            {
                tab.FlushDisplay();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Display flush error ({tab.PortName}): {ex}");
            }
        }
    }

    /// <summary>
    /// Performs graceful shutdown: flushes all log files and closes all ports.
    /// </summary>
    public void Shutdown()
    {
        _uiTimer.Stop();
        foreach (var tab in Ports)
        {
            tab.Cleanup();
        }
        _portManager.Dispose();
    }
}
