using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace EliteLevel.Avalonia.Controls;

public partial class HeroCard : UserControl
{
    public static readonly StyledProperty<string?> SeedProperty =
        AvaloniaProperty.Register<HeroCard, string?>(nameof(Seed));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<HeroCard, string?>(nameof(Title));

    public static readonly StyledProperty<string?> EyebrowProperty =
        AvaloniaProperty.Register<HeroCard, string?>(nameof(Eyebrow));

    public static readonly StyledProperty<string?> ChipsProperty =
        AvaloniaProperty.Register<HeroCard, string?>(nameof(Chips));

    public static readonly StyledProperty<string?> MetaProperty =
        AvaloniaProperty.Register<HeroCard, string?>(nameof(Meta));

    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<HeroCard, string?>(nameof(Icon));

    public static readonly StyledProperty<string?> BannerProperty =
        AvaloniaProperty.Register<HeroCard, string?>(nameof(Banner));

    public HeroCard()
    {
        InitializeComponent();
    }

    public string? Seed
    {
        get => GetValue(SeedProperty);
        set => SetValue(SeedProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Eyebrow
    {
        get => GetValue(EyebrowProperty);
        set => SetValue(EyebrowProperty, value);
    }

    /// <summary>Chip labels separated by a vertical bar.</summary>
    public string? Chips
    {
        get => GetValue(ChipsProperty);
        set => SetValue(ChipsProperty, value);
    }

    /// <summary>Meta chip labels separated by a vertical bar.</summary>
    public string? Meta
    {
        get => GetValue(MetaProperty);
        set => SetValue(MetaProperty, value);
    }

    /// <summary>Cached icon, used as a softened background when no banner was found.</summary>
    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Cached banner, such as a PS3 PIC1 image.</summary>
    public string? Banner
    {
        get => GetValue(BannerProperty);
        set => SetValue(BannerProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SeedProperty || change.Property == IconProperty || change.Property == BannerProperty)
        {
            ArtImage.Source = TitleArt.LoadBanner(Banner, Icon, 960, 640) ?? CoverArt.Render(Seed, 960, 640);
        }
        else if (change.Property == TitleProperty)
        {
            TitleText.Text = Title;
            var length = Title?.Length ?? 0;
            TitleText.FontSize = length > 48 ? 28 : length > 32 ? 36 : 48;
            TitleText.LineHeight = TitleText.FontSize;
            ToolTip.SetTip(TitleText, string.IsNullOrWhiteSpace(Title) ? null : Title);
        }
        else if (change.Property == EyebrowProperty)
        {
            EyebrowText.Text = Eyebrow;
        }
        else if (change.Property == ChipsProperty)
        {
            FillChips(ChipPanel, Chips);
        }
        else if (change.Property == MetaProperty)
        {
            FillChips(MetaPanel, Meta);
        }
    }

    private static void FillChips(StackPanel panel, string? text)
    {
        panel.Children.Clear();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (var part in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            panel.Children.Add(new Border
            {
                Height = 30,
                CornerRadius = new CornerRadius(999),
                Background = new SolidColorBrush(Color.Parse("#8C140E0F")),
                BorderBrush = new SolidColorBrush(Color.Parse("#1AFFFFFF")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 0),
                Child = new TextBlock
                {
                    Text = part,
                    FontSize = 12,
                    FontWeight = FontWeight.Medium,
                    Foreground = new SolidColorBrush(Color.Parse("#EDE2DA")),
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }
    }
}
