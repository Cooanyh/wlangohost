using System.Windows;
using PadP2P.App.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PadP2P.App;

public partial class NetworkProfileWindow : FluentWindow
{
    internal NetworkProfileWindow(string ssid, string passphrase)
    {
        InitializeComponent();
        SsidTextBox.Text = ssid;
        PassphraseBox.Password = passphrase;
        IconThemeService.ApplyTo(this);
        SystemThemeWatcher.Watch(this);
    }

    internal string Ssid => SsidTextBox.Text;
    internal string Passphrase => PassphraseBox.Password;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!NetworkProfileValidator.TryValidate(Ssid, Passphrase, out var validationError))
        {
            ValidationMessage.Text = UiText.Get(validationError);
            return;
        }

        DialogResult = true;
    }
}
