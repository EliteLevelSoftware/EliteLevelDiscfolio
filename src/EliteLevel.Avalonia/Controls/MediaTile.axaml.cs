using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace EliteLevel.Avalonia.Controls;

public partial class MediaTile : UserControl
{
    public static readonly StyledProperty<string?> SeedProperty =
        AvaloniaProperty.Register<MediaTile, string?>(nameof(Seed));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<MediaTile, string?>(nameof(Title));

    public static readonly StyledProperty<string?> MetaProperty =
        AvaloniaProperty.Register<MediaTile, string?>(nameof(Meta));

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SeedProperty)
        {
            ArtImage.Source = CoverArt.Render(Seed, 360, 480);
        }
        else if (change.Property == TitleProperty)
        {
            TitleText.Text = Title;
        }
        else if (change.Property == MetaProperty)
        {
            MetaText.Text = Meta;
        }
    }
}
