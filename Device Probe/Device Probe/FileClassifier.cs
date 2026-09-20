/// <summary>
/// Decides what a file is, from its name and - when the name is not enough -
/// from the first bytes of its content.
///
/// Deliberately pure: no COM, no device, no console. That is the whole point of
/// it living here. Until this was separated out, nothing about classification
/// could be checked without a physical phone plugged in, which made the rules
/// below the least-tested part of the project despite being the part that
/// decides whether a user's photo gets seen at all.
/// </summary>
static class FileClassifier
{
    // How relevance is decided: by the file's own extension, NOT by the folder
    // it sits in. Folder names are unreliable - the same app stores media under
    // "WhatsApp/" on one phone and "Android/media/com.whatsapp/WhatsApp/" on
    // another (measured on a J7 Prime2 and an A56 respectively). An extension
    // does not lie about what a file is, wherever it happens to live.
    static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images: everyday phone/camera formats, plus DNG (Android/Pixel/Samsung RAW
        // mode) and AVIF/TIFF. Deliberately left out: brand-specific DSLR RAW
        // formats (.cr2, .nef, .arw, ...) - rare on a phone, can add later if needed.
        ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".webp", ".bmp",
        ".heic", ".heif", ".tiff", ".tif", ".dng", ".avif",
        // Videos: MP4/MOV/3GP are what phones actually record; the rest cover
        // videos that arrive from elsewhere (downloaded, shared, old camcorder clips).
        ".mp4", ".mov", ".m4v", ".3gp", ".3g2",
        ".avi", ".mkv", ".webm", ".flv", ".wmv", ".mpg", ".mpeg", ".m2ts", ".mts", ".ts"
    };

    // Audio formats that share the MP4-family "ftyp" container bytes with real
    // video. Excluded up front so the signature fallback never has to guess:
    // trying to tell audio-only MP4 variants apart by their major brand was
    // tested and does not work reliably, because different encoders write
    // different brands for the same audio-only content.
    static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m4a", ".m4b", ".m4p", ".mp3", ".aac", ".wav", ".ogg", ".opus", ".amr", ".flac"
    };

    // Extensions already known not to be photos or videos, built from what
    // actually turned up as false candidates on real devices (.pag is
    // CamScanner's document cache, .nomedia is Android's "don't index this"
    // marker, .crypt14 is a WhatsApp encrypted backup). Everything listed here
    // skips the signature check entirely, which is what keeps that check rare:
    // it should only ever run on extensions that are genuinely unrecognised.
    static readonly HashSet<string> KnownNonMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nomedia", ".crypt14", ".crypt12", ".pag", ".chck",
        ".json", ".xml", ".txt",
        ".db", ".db-wal", ".db-shm", ".log", ".dat", ".tmp", ".lock",
        ".zip", ".apk", ".bak", ".cfg", ".ini", ".key", ".properties", ".ttf"
    };

    static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx"
    };

    /// <summary>How many leading bytes <see cref="ClassifyBySignature"/> needs.</summary>
    public const int SignatureBytes = 12;

    /// <summary>
    /// Classifies by extension alone. Returns null when the extension is
    /// unrecognised AND not already known to be irrelevant - the only case
    /// where reading the file's actual bytes is worth a device round trip.
    /// </summary>
    public static FileKind? ClassifyByName(string name)
    {
        string extension = Path.GetExtension(name);

        if (MediaExtensions.Contains(extension)) return FileKind.MediaFile;
        if (DocumentExtensions.Contains(extension)) return FileKind.Document;
        if (AudioExtensions.Contains(extension) || KnownNonMediaExtensions.Contains(extension))
        {
            return FileKind.Unknown;
        }

        return null; // caller should fall back to the signature check
    }

    /// <summary>
    /// Classifies by "magic bytes" - the fixed bytes a format's encoder always
    /// writes at the start of a file, which a wrong or missing extension cannot
    /// fake. Expects at least <see cref="SignatureBytes"/> bytes; a shorter
    /// buffer simply will not match anything.
    /// </summary>
    public static FileKind ClassifyBySignature(ReadOnlySpan<byte> header)
    {
        if (header.Length < SignatureBytes) return FileKind.Unknown;

        // JPEG
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return FileKind.MediaFile;
        // PNG
        if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) return FileKind.MediaFile;
        // GIF ("GIF8")
        if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38) return FileKind.MediaFile;
        // BMP ("BM")
        if (header[0] == 0x42 && header[1] == 0x4D) return FileKind.MediaFile;
        // WEBP - only the leading "RIFF" is checked, which also matches WAV;
        // audio extensions are excluded before we ever get here.
        if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46) return FileKind.MediaFile;
        // HEIC/MP4/MOV/3GP all share an "ftyp" box at offset 4.
        if (header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70) return FileKind.MediaFile;
        // PDF ("%PDF-")
        if (header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46 && header[4] == 0x2D) return FileKind.Document;

        return FileKind.Unknown;
    }
}
