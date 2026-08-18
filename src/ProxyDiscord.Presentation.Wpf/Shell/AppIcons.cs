using System.IO;
using System.Windows.Media.Imaging;

namespace ProxyDiscord.Presentation.Wpf.Shell;

internal static class AppIcons
{
    private const string ASSETS_FOLDER = "assets";
    private const string APP_ICON_FILE = "app.ico";

    public static string AppIconPath => Path.Combine(AppContext.BaseDirectory, ASSETS_FOLDER, APP_ICON_FILE);

    public static bool AppIconExists => File.Exists(AppIconPath);

    public static BitmapImage? TryLoadAppIcon()
    {
        if (!AppIconExists)
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(AppIconPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }
}
