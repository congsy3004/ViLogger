using System.Reflection;
using System.Windows;

namespace EverLogger.App;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        string? rawVer = typeof(HelpWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string? version = rawVer?.Split('+')[0];
        if (!string.IsNullOrWhiteSpace(version))
        {
            Title = $"ARVSerialTool v{version} — Help";
            SubtitleText.Text = $"ARVSerialTool v{version} — Describes every control in ARVSerialTool";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
