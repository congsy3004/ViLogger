using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EverLogger.App.ViewModels;

namespace EverLogger.App;

public partial class MainWindow : Window
{
    private const int WM_DEVICECHANGE = 0x0219;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = PresentationSource.FromVisual(this) as HwndSource;
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE)
        {
            if (DataContext is MainViewModel mainVm)
            {
                mainVm.OnDeviceChanged();
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Connects a port's terminal TextBox to its view model when the TextBox is loaded.
    /// </summary>
    private void MonitorTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            AttachMonitor(textBox);
        }
    }

    /// <summary>
    /// Re-connects the terminal TextBox if it is ever reused for a different port.
    /// </summary>
    private void MonitorTextBox_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.IsLoaded)
        {
            AttachMonitor(textBox);
        }
    }

    /// <summary>
    /// Disconnects the terminal TextBox from its view model when it is unloaded.
    /// </summary>
    private void MonitorTextBox_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            DetachMonitor(textBox);
        }
    }

    private static void AttachMonitor(TextBox textBox)
    {
        // Never subscribe twice, and never stay subscribed to a previous port.
        DetachMonitor(textBox);

        if (textBox.DataContext is not PortTabViewModel portVm)
        {
            textBox.Clear();
            return;
        }

        EventHandler onUpdate = (_, _) => ApplyTerminalUpdate(textBox, portVm);
        portVm.DisplayUpdateReady += onUpdate;
        textBox.Tag = (Action)(() => portVm.DisplayUpdateReady -= onUpdate);

        // Always start from the full current text: the view may have been (re)created after
        // data already arrived, and must never be left out of date.
        portVm.Terminal.RequestReset();
        ApplyTerminalUpdate(textBox, portVm);
    }

    private static void DetachMonitor(TextBox textBox)
    {
        if (textBox.Tag is Action detach)
        {
            detach();
            textBox.Tag = null;
        }
    }

    /// <summary>
    /// Brings the terminal TextBox up to date with one append (or one full reload after the
    /// buffer was trimmed or cleared), then follows the end if AutoScroll is on.
    /// </summary>
    private static void ApplyTerminalUpdate(TextBox textBox, PortTabViewModel portVm)
    {
        if (!portVm.Terminal.TryTakeUpdate(out bool reset, out string text)) return;

        if (reset)
        {
            textBox.Text = text;
        }
        else
        {
            textBox.AppendText(text);
        }

        if (portVm.AutoScroll)
        {
            textBox.ScrollToEnd();

            if (reset)
            {
                // After a full reload the text layout may finish after this call; scroll once
                // more when the UI is idle so the view really ends at the bottom.
                textBox.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    if (portVm.AutoScroll) textBox.ScrollToEnd();
                });
            }
        }
    }

    /// <summary>
    /// Handles Enter key in Tx input:
    /// - Plain Enter: sends the TX content immediately (same as clicking the Send button).
    /// - Ctrl+Enter: inserts CR (&lt;CR&gt; / 0D) into the input. If the last character was
    ///   already CR, inserts LF instead (&lt;LF&gt; / 0A). LF is only appended immediately
    ///   after CR (the smart rule).
    /// </summary>
    private void TxTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is not TextBox tb || tb.DataContext is not PortTabViewModel portVm) return;

        bool ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (!ctrlHeld)
        {
            // Plain Enter → send
            if (portVm.SendCommand.CanExecute(null))
            {
                portVm.SendCommand.Execute(null);
            }
        }
        else
        {
            // Ctrl+Enter → insert CR, then LF if last was CR (smart rule)
            string current = portVm.TxInput;
            if (portVm.SendAsHex)
            {
                // HEX mode: insert 0D (CR byte), then 0A (LF byte) if last byte was 0D
                string rawHex = new string(
                    current.Where(c => "0123456789ABCDEFabcdef".Contains(c)).ToArray())
                    .ToUpperInvariant();
                bool lastWasCR = rawHex.Length >= 2 && rawHex.Substring(rawHex.Length - 2) == "0D";
                portVm.TxInput = lastWasCR ? current + "0A" : current + "0D";
            }
            else
            {
                // ASCII mode: insert <CR> token, then <LF> token if last token was <CR>
                if (current.EndsWith("<CR>"))
                    portVm.TxInput = current + "<LF>";
                else
                    portVm.TxInput = current + "<CR>";
            }
            tb.CaretIndex = portVm.TxInput.Length;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Ensure all logs are flushed and serial connections closed when the application closes.
    /// </summary>
    private HelpWindow? _helpWindow;

    /// <summary>
    /// Opens the help window (or brings it to front if already open).
    /// </summary>
    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_helpWindow == null || !_helpWindow.IsLoaded)
        {
            _helpWindow = new HelpWindow { Owner = this };
            _helpWindow.Show();
        }
        else
        {
            _helpWindow.Activate();
        }
    }

    /// <summary>
    /// Ensure all logs are flushed and serial connections closed when the application closes.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is MainViewModel mainVm)
        {
            mainVm.Shutdown();
        }
    }
}
