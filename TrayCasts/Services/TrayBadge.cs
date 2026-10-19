using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using TrayCasts.Feeds;
using TrayCasts.Tray;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;
using WinUIEx;

namespace TrayCasts.Services;

/// <summary>
/// Shows the tray icon, with the new-episode count drawn on it (<see cref="BadgeIcon"/>) when
/// there is one. Owns the generated icon handle: the shell copies the image when the tray icon is
/// updated, so the previous one is freed after. UI thread only.
/// </summary>
internal sealed class TrayBadge
{
    private const uint IconFormatVersion = 0x00030000; // Required by CreateIconFromResourceEx.

    private static readonly HttpClient ArtClient = FeedClient.CreateHttpClient($"TrayCasts/{AppVersion.Current} (Windows)");

    private readonly UISettings _uiSettings = new();
    private HICON _icon;
    private int _version;

    // The decoded app icon, kept so a count change doesn't decode the file again.
    private string? _pixelsPath;
    private int _pixelsSize;
    private uint[]? _pixels;

    // The playing show's artwork, kept for the same reason.
    private string? _artUrl;
    private int _artSize;
    private uint[]? _artPixels;

    /// <summary>
    /// Sets the tray icon to <paramref name="iconPath"/>, or to the image at <paramref name="artUrl"/>
    /// when given, badged with <paramref name="count"/> when above zero.
    /// </summary>
    public async void Update(TrayIcon trayIcon, string iconPath, int count, string? artUrl = null)
    {
        int version = ++_version;
        if (count <= 0 && artUrl is null)
        {
            trayIcon.SetIcon(iconPath);
            Release();
            return;
        }

        try
        {
            int size = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, PInvoke.GetDpiForSystem());
            uint[]? art = artUrl is null ? null : await LoadArtPixelsAsync(artUrl, size);
            if (version != _version)
            {
                return; // A newer update won.
            }

            if (art is null && count <= 0)
            {
                // The art couldn't be loaded and there is no count to show.
                trayIcon.SetIcon(iconPath);
                Release();
                return;
            }

            uint[] pixels = art ?? await LoadPixelsAsync(iconPath, size);

            if (version != _version)
            {
                return; // A newer update won.
            }

            byte[] ico = BadgeIcon.Create(pixels, size, count, BadgeColor());
            HICON icon;
            nint handle;
            unsafe
            {
                fixed (byte* bits = ico)
                {
                    icon = PInvoke.CreateIconFromResourceEx(
                        bits + BadgeIcon.ImageOffset,
                        (uint)(ico.Length - BadgeIcon.ImageOffset),
                        true,
                        IconFormatVersion,
                        size,
                        size,
                        IMAGE_FLAGS.LR_DEFAULTCOLOR);
                }

                handle = (nint)icon.Value;
            }

            if (handle == 0)
            {
                trayIcon.SetIcon(iconPath);
                Release();
                return;
            }

            trayIcon.SetIcon(Win32Interop.GetIconIdFromIcon(handle));
            Release();
            _icon = icon;
        }
        catch (Exception)
        {
            // The badge is a nicety; fall back to the plain icon.
            if (version == _version)
            {
                trayIcon.SetIcon(iconPath);
                Release();
            }
        }
    }

    /// <summary>Frees the badge; call after the tray icon has been switched back to the app icon or disposed.</summary>
    public void Release()
    {
        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
            _icon = default;
        }
    }

    /// <summary>The show's artwork scaled to the icon size, or null if it can't be fetched or decoded.</summary>
    private async Task<uint[]?> LoadArtPixelsAsync(string url, int size)
    {
        if (_artPixels is not null && _artUrl == url && _artSize == size)
        {
            return _artPixels;
        }

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            {
                return null;
            }

            byte[] bytes = await ArtClient.GetByteArrayAsync(uri);
            using InMemoryRandomAccessStream stream = new();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            _artPixels = await DecodeAsync(stream, size);
            _artUrl = url;
            _artSize = size;
            return _artPixels;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<uint[]> LoadPixelsAsync(string path, int size)
    {
        if (_pixels is not null && _pixelsPath == path && _pixelsSize == size)
        {
            return _pixels;
        }

        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStreamWithContentType stream = await file.OpenReadAsync();
        _pixels = await DecodeAsync(stream, size);
        _pixelsPath = path;
        _pixelsSize = size;
        return _pixels;
    }

    private static async Task<uint[]> DecodeAsync(IRandomAccessStream stream, int size)
    {
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

        // An .ico holds several sizes: scale down from the smallest one at least this big.
        BitmapFrame? best = null;
        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            BitmapFrame frame = await decoder.GetFrameAsync(i);
            bool better = best is null
                || (frame.PixelWidth >= size && (best.PixelWidth < size || frame.PixelWidth < best.PixelWidth))
                || (best.PixelWidth < size && frame.PixelWidth > best.PixelWidth);
            if (better)
            {
                best = frame;
            }
        }

        PixelDataProvider data = await best!.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform { ScaledWidth = (uint)size, ScaledHeight = (uint)size, InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        // BGRA bytes read little-endian are 0xAARRGGBB.
        return MemoryMarshal.Cast<byte, uint>(data.DetachPixelData()).ToArray();
    }

    /// <summary>The accent color, in the shade Windows uses on that taskbar for badges.</summary>
    private uint BadgeColor()
    {
        Color color = _uiSettings.GetColorValue(SystemThemeService.IsLight ? UIColorType.AccentDark1 : UIColorType.AccentLight2);
        return 0xFF000000 | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
    }
}
