using System.Globalization;
using System.Resources;

namespace PlaylistViewer.Android;

internal static class AppStrings
{
    private static readonly ResourceManager ResourceManager = new(
        "PlaylistViewer.Android.AppStrings",
        typeof(AppStrings).Assembly);

    public static string Get(string key) =>
        ResourceManager.GetString(key, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException($"Missing localized string: {key}");

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
}
