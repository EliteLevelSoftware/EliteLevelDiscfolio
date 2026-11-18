using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace EliteLevel.Avalonia.Controls;

/// <summary>
/// Undecorated window. Restored it is a 28px-radius shell with a 1px frame and its own shadow.
/// Maximised, or when the platform has no compositor, the corners go square and the shadow drops.
/// The frame stays on the shell border. Caption buttons stay Windows-style on every operating system.
/// There is no backdrop blur.
/// </summary>
public class EliteWindow : Window
{
    public static readonly DirectProperty<EliteWindow, double> ChromeMarginProperty =
        AvaloniaProperty.RegisterDirect<EliteWindow, double>(nameof(ChromeMargin), window => window.ChromeMargin);

    public static readonly DirectProperty<EliteWindow, CornerRadius> ChromeRadiusProperty =
        AvaloniaProperty.RegisterDirect<EliteWindow, CornerRadius>(nameof(ChromeRadius), window => window.ChromeRadius);

    public static readonly DirectProperty<EliteWindow, BoxShadows> ChromeShadowProperty =
        AvaloniaProperty.RegisterDirect<EliteWindow, BoxShadows>(nameof(ChromeShadow), window => window.ChromeShadow);

    private static readonly BoxShadows RestoredShadow = BoxShadows.Parse("0 40 90 0 #8C000000, 0 0 0 1 #FF5A2020");

    private double _chromeMargin = 40;
    private CornerRadius _chromeRadius = new(28);
    private BoxShadows _chromeShadow = RestoredShadow;

    public EliteWindow()
    {
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        ExtendClientAreaToDecorationsHint = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        MinWidth = 860;
        MinHeight = 600;
        Width = 1320;
        Height = 860;
    }

    public double ChromeMargin
    {
        get => _chromeMargin;
        private set => SetAndRaise(ChromeMarginProperty, ref _chromeMargin, value);
    }

    public CornerRadius ChromeRadius
    {
        get => _chromeRadius;
        private set => SetAndRaise(ChromeRadiusProperty, ref _chromeRadius, value);
    }

    public BoxShadows ChromeShadow
    {
        get => _chromeShadow;
        private set => SetAndRaise(ChromeShadowProperty, ref _chromeShadow, value);
    }

    public void DragMove(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed == false)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        BeginMoveDrag(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateChrome();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == ActualTransparencyLevelProperty)
        {
            UpdateChrome();
        }
    }

    private void UpdateChrome()
    {
        var flat = WindowState is WindowState.Maximized or WindowState.FullScreen
            || ActualTransparencyLevel == WindowTransparencyLevel.None;
        if (flat)
        {
            ChromeMargin = 0;
            ChromeRadius = new CornerRadius(0);
            ChromeShadow = default;
            return;
        }

        ChromeMargin = 40;
        ChromeRadius = new CornerRadius(28);
        ChromeShadow = Application.Current?.TryGetResource("ElShadowWindow", ActualThemeVariant, out var shadow) == true
            && shadow is BoxShadows parsed
            ? parsed
            : RestoredShadow;
    }
}
