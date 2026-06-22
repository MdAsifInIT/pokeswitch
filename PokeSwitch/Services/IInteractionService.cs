namespace PokeSwitch.Services;

public interface IInteractionService
{
    bool Confirm(string title, string message);
    void CopyToClipboard(string text);
}
