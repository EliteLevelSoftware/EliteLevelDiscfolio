using EliteLevel.Disc.Core.Titles;

namespace EliteLevel.Disc.Core.Tests;

public class LibraryCacheTests
{
    [Fact]
    public async Task Remove_deletes_cached_art_and_leaves_the_image()
    {
        var root = Path.Combine(Path.GetTempPath(), "discfolio-cache", Guid.NewGuid().ToString("N"));
        var image = Path.Combine(root, "game.iso");
        Directory.CreateDirectory(root);
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(image, bytes);
        try
        {
            var cache = new TitleArtCache(Path.Combine(root, "cache"));
            var stored = cache.Store(image, new TitleMetadata
            {
                Title = "Northwind",
                Icon = SyntheticTitles.Png(9, 8, 7)
            });
            Assert.NotNull(stored.IconPath);
            Assert.True(File.Exists(stored.IconPath));

            cache.Remove(image);

            Assert.Equal(bytes, await File.ReadAllBytesAsync(image));
            Assert.Null(cache.TryGet(image));
            Assert.False(File.Exists(stored.IconPath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(stored.IconPath)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Remove_drops_the_cache_when_the_image_is_already_gone()
    {
        var root = Path.Combine(Path.GetTempPath(), "discfolio-cache", Guid.NewGuid().ToString("N"));
        var image = Path.Combine(root, "gone.iso");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(image, [9, 9, 9]);
        try
        {
            var cache = new TitleArtCache(Path.Combine(root, "cache"));
            var stored = cache.Store(image, new TitleMetadata
            {
                Title = "Gone",
                Icon = SyntheticTitles.Png(1, 2, 3)
            });
            Assert.NotNull(stored.IconPath);
            File.Delete(image);

            cache.Remove(image);

            Assert.False(File.Exists(image));
            Assert.False(File.Exists(stored.IconPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
