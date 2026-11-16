using Microsoft.UI.Xaml;

namespace DingLater.App.Views;

/// <summary>Window-size breakpoints shared by the shell and pages so their edges stay aligned.</summary>
internal static class AdaptiveLayout
{
    internal const double TwoPaneMinWidth = 1008;
    internal const double CompactMaxWidth = 720;
    internal const double DenseMaxHeight = 600;

    internal static bool IsTwoPane(double windowWidth) => windowWidth >= TwoPaneMinWidth;

    internal static bool IsCompact(double windowWidth) => windowWidth < CompactMaxWidth;

    internal static Thickness PagePadding(double windowWidth, double windowHeight)
    {
        if (windowHeight > 0 && windowHeight < DenseMaxHeight)
        {
            return new Thickness(12);
        }

        return IsCompact(windowWidth)
            ? new Thickness(16)
            : (Thickness)Application.Current.Resources["DingPagePadding"];
    }
}
