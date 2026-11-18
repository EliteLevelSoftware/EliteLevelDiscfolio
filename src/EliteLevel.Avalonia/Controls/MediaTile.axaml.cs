using Avalonia;
using Avalonia.Controls;

namespace EliteLevel.Avalonia.Controls;

public partial class MediaTile : UserControl
{
    public static readonly StyledProperty<string?> SeedProperty =
        AvaloniaProperty.Register<MediaTile, string?>(nameof(Seed));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<MediaTile, string?>(nameof(Title));

    public static readonly StyledProperty<string?> MetaProperty =
        AvaloniaProperty.Register<MediaTile, string?>(nameof(Meta));

    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<MediaTile, string?>(nameof(Icon));

    public MediaTile()
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

    public string? Meta
    {
        get => GetValue(MetaProperty);
        set => SetValue(MetaProperty, value);
    }

    /// <summary>Cached icon file. Generated art is used when this is empty.</summary>
    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SeedProperty || change.Property == IconProperty)
        {
            ArtImage.Source = TitleArt.Load(Icon) ?? CoverArt.Render(Seed, 360, 480);
        }
        else if (change.Property == TitleProperty)
        {
            TitleText.Text = Title;
            var tip = string.IsNullOrWhiteSpace(Title) ? null : Title;
            ToolTip.SetTip(TitleText, tip);
            ToolTip.SetTip(this, tip);
        }
        else if (change.Property == MetaProperty)
        {
            MetaText.Text = Meta;
        }
    }
}
