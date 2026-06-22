using System.Windows;
using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;

namespace PokeSwitch.Services;

public sealed class WpfInteractionService : IInteractionService
{
    private readonly Window _owner;

    public WpfInteractionService(Window owner)
    {
        _owner = owner;
    }

    public bool Confirm(string title, string message)
    {
        return WpfMessageBox.Show(_owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    public void CopyToClipboard(string text)
    {
        WpfClipboard.SetText(text);
    }
}
