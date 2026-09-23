namespace MediaTransfer.Core;

/// <summary>
/// Where a file came from, worked out from the path the walk gave it.
///
/// This is a GUESS, and the distinction matters. A path says which app wrote a
/// file, not whether the user values it: their own wedding photo and a forwarded
/// meme both land under the messaging app that carried them. So the source is
/// only ever used to group and to filter - never to decide that something is not
/// worth keeping. That decision belongs to the person whose phone it is.
///
/// Apps are read from the package name in the path rather than matched against a
/// fixed list, because which apps a phone has is the one thing that varies most
/// between devices. A phone with Telegram gets a Telegram group without this
/// code being changed.
/// </summary>
public static class MediaSource
{
    /// <summary>Package names worth showing under their real name.</summary>
    static readonly Dictionary<string, string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["com.whatsapp"] = "WhatsApp",
        ["com.whatsapp.w4b"] = "WhatsApp Business",
        ["org.telegram.messenger"] = "Telegram",
        ["com.instagram.android"] = "Instagram",
        ["com.facebook.katana"] = "Facebook",
        ["com.facebook.orca"] = "Messenger",
        ["com.snapchat.android"] = "Snapchat",
        ["com.viber.voip"] = "Viber",
        ["com.discord"] = "Discord",
        ["com.twitter.android"] = "X",
        ["com.zhiliaoapp.musically"] = "TikTok",
        ["org.thoughtcrime.securesms"] = "Signal",
        ["com.microsoft.teams"] = "Teams",
        ["com.google.android.apps.photos"] = "Google Fotoğraflar",
        ["com.samsung.android.scloud"] = "Samsung Cloud",
    };

    public const string Camera = "camera";
    public const string Screenshot = "screenshot";
    public const string Download = "download";
    public const string Other = "other";

    /// <summary>App ids carry this prefix so a caller can tell them apart from the fixed ones.</summary>
    public const string AppPrefix = "app:";

    /// <summary>The id a file groups under, and the name to show for that group.</summary>
    public static (string Id, string Label) Classify(string path)
    {
        string p = path.Replace('\\', '/');

        // Camera and screenshots are checked first and by folder, because they
        // are the two groups defined by what the PHONE did rather than by which
        // app wrote the file. Android puts both under DCIM, but the screenshot
        // folder's name varies enough by vendor that a substring is safer than
        // an exact path: seen under DCIM on one device and Pictures on another.
        if (Contains(p, "/DCIM/Camera")) return (Camera, "Kamera");
        if (Contains(p, "/Screenshot")) return (Screenshot, "Ekran görüntüsü");

        string? package = PackageIn(p);
        if (package is not null) return (AppPrefix + package, AppLabel(package));

        // WhatsApp on older Android sits at the storage root instead of under
        // Android/media. Measured on a J7 Prime2: same app, different place.
        if (Contains(p, "/WhatsApp/")) return (AppPrefix + "com.whatsapp", "WhatsApp");

        if (Contains(p, "/Download")) return (Download, "İndirilenler");
        return (Other, "Diğer");
    }

    /// <summary>The package name in an Android/media or Android/data path, if there is one.</summary>
    static string? PackageIn(string path)
    {
        foreach (string root in new[] { "/Android/media/", "/Android/data/" })
        {
            int at = path.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            int start = at + root.Length;
            int end = path.IndexOf('/', start);
            if (end <= start) continue;

            string candidate = path[start..end];
            // A package name has dots in it. Anything else under that folder is
            // some other structure, and guessing a label from it would produce a
            // group named after a directory nobody recognises.
            if (candidate.Contains('.')) return candidate;
        }
        return null;
    }

    /// <summary>
    /// A readable name for a package. Falls back to the last meaningful segment
    /// so an unknown app still gets a name a person can recognise, rather than
    /// being swept into "Diğer" where it would be invisible.
    /// </summary>
    public static string AppLabel(string package)
    {
        if (KnownApps.TryGetValue(package, out string? known)) return known;

        string[] parts = package.Split('.', StringSplitOptions.RemoveEmptyEntries);
        string? last = null;
        foreach (string part in parts)
        {
            if (part is "com" or "org" or "net" or "io" or "android" or "app") continue;
            last = part;
        }
        last ??= package;
        return last.Length == 0 ? package : char.ToUpperInvariant(last[0]) + last[1..];
    }

    static bool Contains(string path, string fragment) =>
        path.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
