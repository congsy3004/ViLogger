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
            Title = $"ViLogger v{version} — Help";
            SubtitleText.Text = $"ViLogger v{version} — Describes every control in ViLogger";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
