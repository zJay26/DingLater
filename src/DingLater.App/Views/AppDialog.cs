using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DingLater.App.Views;

/// <summary>
/// Creates consistently styled dialogs and shows them one at a time. WinUI throws when a second
/// ContentDialog opens while another is visible, so overlapping requests wait for their turn.
/// </summary>
internal static class AppDialog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static ContentDialog Create(
        XamlRoot xamlRoot,
        string title,
        object content,
        string closeButtonText,
        string? primaryButtonText = null,
        ContentDialogButton defaultButton = ContentDialogButton.Close) => new()
        {
            XamlRoot = xamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = title,
            Content = content as UIElement ?? Message(content.ToString() ?? string.Empty),
            PrimaryButtonText = primaryButtonText ?? string.Empty,
            CloseButtonText = closeButtonText,
            DefaultButton = defaultButton
        };

    /// <summary>Asks for confirmation; destructive actions keep Cancel as the default button.</summary>
    internal static async Task<bool> ConfirmAsync(
        XamlRoot xamlRoot,
        string title,
        string message,
        string primaryButtonText,
        string closeButtonText = "取消",
        bool destructive = true)
    {
        var dialog = Create(
            xamlRoot,
            title,
            message,
            closeButtonText,
            primaryButtonText,
            destructive ? ContentDialogButton.Close : ContentDialogButton.Primary);
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    internal static Task<ContentDialogResult> AlertAsync(XamlRoot xamlRoot, string title, string message, string closeButtonText = "知道了") =>
        ShowAsync(Create(xamlRoot, title, message, closeButtonText));

    internal static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Body text that honours the user's DingLater font scale.</summary>
    internal static TextBlock Message(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["DingBodyTextStyle"],
        IsTextSelectionEnabled = true,
        MaxWidth = 460
    };
}
