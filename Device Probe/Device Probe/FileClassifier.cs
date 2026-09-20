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
        // Android 11+ does NOT move a deleted photo into a .Trash folder; it
        // renames it in place to ".trashed-<timestamp>-IMG_0001.jpg". Skipping
        // the folder therefore never caught the modern mechanism, and deleted
        // photos were turning up in results regardless. ".pending-" is the same
        // scheme for a write still in progress - a half-written file nobody
        // wants transferred.
        string fileName = Path.GetFileName(name);
        if (fileName.StartsWith(".trashed-", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith(".pending-", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.Unknown;
        }

        // Trailing dots and spaces are legal in a filename the device hands us
        // but are not part of the extension. Without trimming, "photo.jpg "
        // yields ".jpg " which matches nothing, so the file fell through to a
        // content read costing roughly 118ms - or, once the breaker had
        // tripped, went unclassified entirely.
        // The NAME is trimmed, not the extension: Path.GetExtension("photo.jpg.")
        // already returns "" because the final character is the dot, so there
        // would be nothing left to trim.
        string extension = Path.GetExtension(name.TrimEnd('.', ' '));

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

        // BMP ("BM" plus a plausible file size). The size check is not
        // decoration: on two bytes alone, the text "BMW service log" was
        // classified as a photo. A real BMP stores its total size little-endian
        // at offset 2, which for anything we would want must be at least the
        // 14-byte header, and the ASCII text that produces false hits lands far
        // outside any sane range.
        if (header[0] == 0x42 && header[1] == 0x4D)
        {
            uint declaredSize = (uint)(header[2] | (header[3] << 8) | (header[4] << 16) | (header[5] << 24));
            // Both bounds matter. A lower bound alone still accepted "BMW
            // service ", whose size field reads as roughly 1.7 GB: printable
            // ASCII in the high byte forces the value above 0x20000000, while
            // any real BMP is far below it.
            if (declaredSize >= 14 && declaredSize < 0x20000000) return FileKind.MediaFile;
        }

        // RIFF container: the four bytes at offset 8 say WHICH format it is,
        // and they were never read. "RIFF....WAVE" and "RIFF....AVI " were both
        // being reported as photos. Only WEBP is media for our purposes - AVI
        // arrives with its own extension, and WAVE is audio.
        if (Matches(header, 0, "RIFF")) return Matches(header, 8, "WEBP") ? FileKind.MediaFile : FileKind.Unknown;

        // ISO base media (HEIC/MP4/MOV/3GP): an "ftyp" box at offset 4. Bytes
        // 0-3 are that box's length, which must be a sane, 4-aligned value -
        // without checking it, the ASCII text "the ftype is" matched. Bytes
        // 8-11 are the major brand, which is how audio-only MP4 files are
        // rejected; they cannot be excluded by extension when they have none.
        if (Matches(header, 4, "ftyp"))
        {
            uint boxLength = (uint)((header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3]);
            bool plausibleBox = boxLength >= 8 && boxLength <= 1024 && boxLength % 4 == 0;
            bool audioBrand = Matches(header, 8, "M4A ") || Matches(header, 8, "M4B ") || Matches(header, 8, "M4P ");
            if (plausibleBox && !audioBrand) return FileKind.MediaFile;
        }

        // TIFF, and with it DNG - the RAW format Samsung and Pixel cameras
        // write. Both are in the extension list but neither had a signature, so
        // one with a damaged extension was invisible.
        if (Matches(header, 0, "II") && header[2] == 0x2A && header[3] == 0x00) return FileKind.MediaFile;
        if (Matches(header, 0, "MM") && header[2] == 0x00 && header[3] == 0x2A) return FileKind.MediaFile;

        // Matroska/WebM (EBML header) - .mkv and .webm are both claimed by the
        // extension list.
        if (header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3) return FileKind.MediaFile;

        // JPEG XL, both the raw codestream and the ISOBMFF-wrapped form.
        if (header[0] == 0xFF && header[1] == 0x0A) return FileKind.MediaFile;
        if (header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x00 && header[3] == 0x0C &&
            Matches(header, 4, "JXL ")) return FileKind.MediaFile;

        // JPEG 2000.
        if (header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x00 && header[3] == 0x0C &&
            Matches(header, 4, "jP  ")) return FileKind.MediaFile;

        // PDF ("%PDF-")
        if (Matches(header, 0, "%PDF-")) return FileKind.Document;

        return FileKind.Unknown;
    }

    /// <summary>
    /// Compares bytes at an offset against ASCII, without allocating. Returns
    /// false rather than throwing when the buffer is too short, so every caller
    /// above stays safe regardless of what the device returned.
    /// </summary>
    static bool Matches(ReadOnlySpan<byte> header, int offset, string ascii)
    {
        if (offset + ascii.Length > header.Length) return false;
        for (int i = 0; i < ascii.Length; i++)
        {
            if (header[offset + i] != (byte)ascii[i]) return false;
        }
        return true;
    }
}
