namespace Gac.Tray.UI;

/// <summary>
/// The GAC icon (CHANGE-002 §1.2/§1.4), embedded from <c>assets/icons/gac.ico</c>: used by the Tray App, the server
/// management app and the demo's picker/server/client views. The admin icon lives only in the Console assembly.
/// </summary>
public static class GacIcons
{
    private static Icon? _gac;

    /// <summary>The multi-size GAC icon (Windows picks the best frame for each use).</summary>
    public static Icon Gac => _gac ??= Load(typeof(GacIcons).Assembly, "Gac.Icons.gac.ico");

    /// <summary>A specific frame size, e.g. 16 or 32 for a tray <see cref="NotifyIcon"/>.</summary>
    public static Icon GacSized(int size) => new(Gac, new Size(size, size));

    public static Icon Load(System.Reflection.Assembly asm, string resource)
    {
        using var s = asm.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("Missing icon resource " + resource);
        return new Icon(s);
    }
}
