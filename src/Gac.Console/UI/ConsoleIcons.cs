using Gac.Tray.UI;

namespace Gac.Console.UI;

/// <summary>The admin icon (CHANGE-002 §1.1/§1.4): Console windows and the top-left logo (the Easter egg click target).</summary>
public static class ConsoleIcons
{
    private static Icon? _admin;
    private static Image? _logo;

    public static Icon Admin => _admin ??= GacIcons.Load(typeof(ConsoleIcons).Assembly, "Gac.Icons.gac-admin.ico");

    /// <summary>The 256 px admin logo shown at the top-left of the Console window.</summary>
    public static Image Logo
    {
        get
        {
            if (_logo != null) return _logo;
            using var s = typeof(ConsoleIcons).Assembly.GetManifestResourceStream("Gac.Icons.gac-admin-256.png")
                          ?? throw new InvalidOperationException("Missing logo resource");
            using var img = Image.FromStream(s);
            return _logo = new Bitmap(img);
        }
    }
}
