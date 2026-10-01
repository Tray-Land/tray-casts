using Windows.ApplicationModel;

namespace TrayCasts.Services;

internal static class AppVersion
{
    /// <summary>"1.2.3" from the package, or "dev" when unpackaged.</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        try
        {
            PackageVersion v = Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            return "dev";
        }
    }
}
