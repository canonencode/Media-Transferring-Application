using System.Globalization;

/// <summary>
/// Tests for <c>FileClassifier.ClassifyByName</c>.
///
/// The distinction these tests exist to protect is null vs FileKind.Unknown:
///   - null    = "I do not know what this is; go read the file's bytes."
///   - Unknown = "I do know, and it is not a photo or a video. Do not read it."
/// Getting those backwards is not a cosmetic bug. Returning Unknown where null
/// belongs silently drops real photos that have an odd extension; returning
/// null where Unknown belongs turns thousands of .db/.log/.json files into
/// device round trips over MTP, each one costing ~118ms on a real phone.
/// </summary>
public class FileClassifierNameTests
{
    // ---- Media extensions -------------------------------------------------

    [Theory]
    [InlineData("IMG_20240101_120000.jpg")]
    [InlineData("photo.jpeg")]
    [InlineData("scan.jfif")]
    [InlineData("screenshot.png")]
    [InlineData("meme.gif")]
    [InlineData("sticker.webp")]
    [InlineData("wallpaper.bmp")]
    [InlineData("live.heic")]
    [InlineData("burst.heif")]
    [InlineData("scan.tiff")]
    [InlineData("scan.tif")]
    [InlineData("raw_capture.dng")]
    [InlineData("modern.avif")]
    [InlineData("VID_20240101.mp4")]
    [InlineData("clip.mov")]
    [InlineData("clip.m4v")]
    [InlineData("old_phone_video.3gp")]
    [InlineData("old_phone_video.3g2")]
    [InlineData("download.avi")]
    [InlineData("download.mkv")]
    [InlineData("download.webm")]
    [InlineData("download.flv")]
    [InlineData("download.wmv")]
    [InlineData("download.mpg")]
    [InlineData("download.mpeg")]
    [InlineData("camcorder.m2ts")]
    [InlineData("camcorder.mts")]
    [InlineData("camcorder.ts")]
    public void MediaExtension_IsMediaFile(string name)
        => Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName(name));

    // ---- Document extensions ----------------------------------------------

    [Theory]
    [InlineData("contract.pdf")]
    [InlineData("letter.doc")]
    [InlineData("letter.docx")]
    [InlineData("budget.xls")]
    [InlineData("budget.xlsx")]
    [InlineData("deck.ppt")]
    [InlineData("deck.pptx")]
    public void DocumentExtension_IsDocument(string name)
        => Assert.Equal(FileKind.Document, FileClassifier.ClassifyByName(name));

    // ---- Audio: its own kind, decided without reading a byte ---------------

    /// <summary>
    /// Audio is the trap in this whole design. MP4-family audio (.m4a, .m4b,
    /// .m4p) carries the very same "ftyp" box at offset 4 that real video
    /// carries, and .wav carries the same leading "RIFF" that .webp carries.
    /// If audio reached the signature check it would come back MediaFile, so
    /// these extensions must be answered here, before any byte is read.
    ///
    /// The answer used to be Unknown, and that conflated two questions. "Do not
    /// read its bytes" was right and still holds. "Not worth transferring" was
    /// wrong, and it cost a real person real files: 107 audio recordings were
    /// left on a phone that was then wiped, 70 of them WhatsApp voice notes -
    /// the one thing the phone's owner had asked to keep. A voice note is as
    /// irreplaceable as a photograph, and it is 75 KB.
    /// </summary>
    [Theory]
    [InlineData("song.m4a")]
    [InlineData("audiobook.m4b")]
    [InlineData("protected.m4p")]
    [InlineData("song.mp3")]
    [InlineData("song.aac")]
    [InlineData("recording.wav")]
    [InlineData("song.ogg")]
    [InlineData("voice.opus")]
    [InlineData("voicenote.amr")]
    [InlineData("lossless.flac")]
    public void AudioExtension_IsAudio_NotMediaAndNotUnknown(string name)
    {
        FileKind? kind = FileClassifier.ClassifyByName(name);

        Assert.Equal(FileKind.AudioFile, kind);
        // Still not MediaFile: that is what keeps it away from the signature
        // check, which would answer MediaFile for every one of these.
        Assert.NotEqual(FileKind.MediaFile, kind);
        // And no longer Unknown, which is what kept it out of transfers.
        Assert.NotEqual(FileKind.Unknown, kind);
        // An answer, not a "go look" - no device read.
        Assert.NotNull(kind);
    }

    // ---- Known non-media: answered, never probed ---------------------------

    [Theory]
    [InlineData(".nomedia")]                 // whole filename is the extension
    [InlineData("msgstore.crypt14")]
    [InlineData("msgstore.crypt12")]
    [InlineData("doc_cache.pag")]
    [InlineData("something.chck")]
    [InlineData("manifest.json")]
    [InlineData("config.xml")]
    [InlineData("notes.txt")]
    [InlineData("index.db")]
    [InlineData("index.db-wal")]
    [InlineData("index.db-shm")]
    [InlineData("scan.log")]
    [InlineData("blob.dat")]
    [InlineData("upload.tmp")]
    [InlineData("session.lock")]
    [InlineData("backup.zip")]
    [InlineData("installer.apk")]
    [InlineData("old.bak")]
    [InlineData("app.cfg")]
    [InlineData("settings.ini")]
    [InlineData("private.key")]
    [InlineData("build.properties")]
    [InlineData("Roboto.ttf")]
    public void KnownNonMediaExtension_IsUnknown_NoSignatureFallback(string name)
        => Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyByName(name));

    // ---- Unrecognised: must be null so the caller reads the bytes ----------

    [Theory]
    [InlineData("mystery.xyz")]
    [InlineData("payload.bin")]
    [InlineData("image.heicx")]              // near-miss of a real media extension
    [InlineData("archive.tar.gz")]           // only the LAST extension is looked at
    [InlineData("photo.jpg.enc")]            // a real jpg wrapped by another tool
    [InlineData("IMG_0001.raw")]
    [InlineData("clip.mp5")]
    public void UnrecognisedExtension_IsNull_SoCallerFallsBackToSignature(string name)
    {
        FileKind? kind = FileClassifier.ClassifyByName(name);

        Assert.Null(kind);
        // Spelled out because this is the exact confusion the method guards
        // against: "not determined" must not arrive as "determined: not media".
        Assert.False(kind == FileKind.Unknown, "null must not be conflated with FileKind.Unknown");
    }

    // ---- Case variations ---------------------------------------------------

    [Theory]
    [InlineData("PHOTO.JPG")]
    [InlineData("photo.jpg")]
    [InlineData("Photo.Jpg")]
    [InlineData("photo.jPg")]
    [InlineData("VIDEO.MP4")]
    [InlineData("IMG_0001.HEIC")]
    public void ExtensionMatching_IsCaseInsensitive_ForMedia(string name)
        => Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName(name));

    [Theory]
    [InlineData("CONTRACT.PDF")]
    [InlineData("Report.DocX")]
    public void ExtensionMatching_IsCaseInsensitive_ForDocuments(string name)
        => Assert.Equal(FileKind.Document, FileClassifier.ClassifyByName(name));

    [Theory]
    [InlineData("SONG.MP3", FileKind.AudioFile)]
    [InlineData("Track.M4A", FileKind.AudioFile)]
    [InlineData(".NOMEDIA", FileKind.Unknown)]
    public void ExtensionMatching_IsCaseInsensitive_ForNonPhotoKinds(string name, FileKind expected)
        => Assert.Equal(expected, FileClassifier.ClassifyByName(name));

    // ---- Names a real phone cannot produce ---------------------------------
    // This block is the reason for extracting the class at all. Android's own
    // file pickers and camera apps will not create most of these, so before
    // there was a pure function to call, none of them could be exercised
    // without hand-crafting objects on a physical device.

    [Fact]
    public void EmojiInName_DoesNotBreakExtensionMatching()
    {
        // "holiday<camera emoji>.jpg" - U+1F4F8 is a surrogate pair, so this
        // also proves the lookup is not confused by a char that is half of a
        // code point sitting next to the dot. Android's own UI rejects emoji
        // in filenames, so this could never be produced on-device.
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName("holiday\U0001F4F8.jpg"));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName("\U0001F389\U0001F38A.png"));
        Assert.Equal(FileKind.Document, FileClassifier.ClassifyByName("\U0001F4C4report.pdf"));
    }

    [Fact]
    public void EmojiAsTheExtension_IsUnrecognised()
    {
        // ".<grinning face>" is not a known extension, so the honest answer is
        // "go read the bytes", not "not media".
        Assert.Null(FileClassifier.ClassifyByName("weird.\U0001F600"));
    }

    [Fact]
    public void TurkishCharactersInName_DoNotBreakExtensionMatching()
    {
        // "Tatil-Fotograflari.jpg" with Turkish diacritics:
        // U+011F g-breve, U+0131 dotless i, U+015F s-cedilla, U+00E7 c-cedilla,
        // U+00F6 o-umlaut, U+011E capital G-breve, U+0130 capital dotted I.
        Assert.Equal(FileKind.MediaFile,
            FileClassifier.ClassifyByName("Tatil-Fotoğrafları.jpg"));
        Assert.Equal(FileKind.MediaFile,
            FileClassifier.ClassifyByName("İstanbul-görüntüsü.mp4"));
        Assert.Equal(FileKind.Document,
            FileClassifier.ClassifyByName("Başvuru-Belgesi-ĞÇ.pdf"));
        Assert.Equal(FileKind.AudioFile,
            FileClassifier.ClassifyByName("Şarkı.mp3"));
    }

    [Fact]
    public void ExtensionMatching_SurvivesTurkishCulture()
    {
        // The Turkish "I" problem: under tr-TR, "I".ToLower() is dotless
        // U+0131, so a culture-sensitive comparison would fail to match ".JPG"
        // against ".jpg" and every uppercase-extension photo on a Turkish
        // machine would fall through to a device read. The HashSets use
        // StringComparer.OrdinalIgnoreCase, which is culture-independent - this
        // test is what proves that choice is still in place.
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName("PHOTO.JPG"));
            Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName("SCAN.TIF"));
            Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName("SCAN.TIFF"));
            Assert.Equal(FileKind.Document, FileClassifier.ClassifyByName("FILE.PDF"));
            Assert.Equal(FileKind.AudioFile, FileClassifier.ClassifyByName("TRACK.MP3"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void TurkishDottedCapitalIInExtension_IsNotFoldedToAsciiI()
    {
        // Documents a deliberate limit rather than a bug: OrdinalIgnoreCase
        // uses simple case folding, which maps 'I'<->'i' but NOT U+0130 (capital
        // dotted I) to 'i'. So ".T<U+0130>FF" is not recognised as ".tiff" and
        // falls through to a byte read - which still classifies it correctly,
        // just more slowly. Nothing is lost; it is only worth knowing.
        Assert.Null(FileClassifier.ClassifyByName("scan.TİFF"));
    }

    [Fact]
    public void NoExtensionAtAll_IsNull()
    {
        Assert.Null(FileClassifier.ClassifyByName("README"));
        Assert.Null(FileClassifier.ClassifyByName("IMG_0001"));
    }

    [Fact]
    public void NameThatIsOnlyADot_IsNull()
        => Assert.Null(FileClassifier.ClassifyByName("."));

    [Fact]
    public void NameThatIsOnlyTwoDots_IsNull()
        => Assert.Null(FileClassifier.ClassifyByName(".."));

    [Fact]
    public void MultipleDots_OnlyTheLastSegmentCounts()
    {
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName("holiday.2024.08.01.jpg"));
        // Genuinely a text file that merely mentions jpg - correctly Unknown.
        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyByName("photo.jpg.txt"));
        Assert.Null(FileClassifier.ClassifyByName("a.b.c.d"));
    }

    [Fact]
    public void TrailingDot_StillResolvesTheExtension()
    {
        // Path.GetExtension returns "" when a dot is the final character, so
        // these used to fall through to a byte read costing ~118ms each - and,
        // once the circuit breaker had tripped, to no classification at all.
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName("photo.jpg."));
        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyByName("notes.txt."));
    }

    [Fact]
    public void TrailingSpaceAfterExtension_StillResolvesTheExtension()
    {
        // An MTP object name is just a string the device hands over, so unlike
        // a Windows path it really can end in a space. ".jpg " matched nothing.
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName("photo.jpg "));
        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyByName("notes.txt "));
    }

    [Fact]
    public void AndroidInPlaceTrashAndPendingNames_AreNotMedia()
    {
        // Android 11+ does not move a deleted photo to a .Trash folder; it
        // renames it where it lies. Skipping the folder never caught this, so
        // deleted photos were still being reported as media.
        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyByName(".trashed-1699999999-IMG_0042.jpg"));
        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyByName(".pending-1699999999-IMG_0043.jpg"));
        // A normal file that merely mentions the word is untouched.
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName("trashed-holiday.jpg"));
    }

    [Theory]
    [InlineData(".TRASHED-1699999999-IMG_0042.jpg")]        // case
    [InlineData(".Pending-1699999999-IMG_0043.mp4")]
    [InlineData("DCIM/Camera/.trashed-1699999999-IMG_0042.jpg")]  // the prefix is on the FILE name, not the path
    [InlineData(@"DCIM\Camera\.pending-1699999999-IMG_0043.jpg")]
    public void TrashedAndPendingPrefixes_MatchCaseInsensitively_OnTheFileNameOnly(string name)
        => Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyByName(name));

    [Fact]
    public void TrashedPrefix_RequiresTheHyphen_SoSimilarNamesAreNotSwallowed()
    {
        // ".trashed-<timestamp>-" is Android's exact scheme. A file that merely
        // starts with ".trashed" (no hyphen) is somebody's own dotfile, and its
        // extension must still be what decides it - swallowing it would be a
        // silent loss of a real photo.
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName(".trashed.jpg"));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName(".trashedIMG.jpg"));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName(".pending.jpg"));
    }

    [Fact]
    public void LeadingAndInternalSpaces_DoNotAffectTheExtension()
    {
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName("  my holiday photo.jpg"));
        Assert.Null(FileClassifier.ClassifyByName("   "));
    }

    [Fact]
    public void EmptyName_IsNull()
        => Assert.Null(FileClassifier.ClassifyByName(""));

    [Fact]
    public void VeryLongName_IsClassifiedNormally()
    {
        string longStem = new string('a', 3000);

        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName(longStem + ".jpg"));
        Assert.Equal(FileKind.AudioFile, FileClassifier.ClassifyByName(longStem + ".mp3"));
        Assert.Null(FileClassifier.ClassifyByName(longStem));
        // A long run of dots - pathological, but must not hang or throw.
        Assert.Null(FileClassifier.ClassifyByName(new string('.', 3000)));
    }

    [Fact]
    public void NameThatLooksLikeAPath_UsesTheLastSegmentsExtension()
    {
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName("DCIM/Camera/IMG_0001.jpg"));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName(@"DCIM\Camera\IMG_0001.jpg"));
    }

    [Fact]
    public void NameThatLooksLikeAPath_WithDotInADirectorySegmentOnly_IsNull()
    {
        // The dot belongs to a directory, not the file, so there is no
        // extension: Path.GetExtension stops at the separator rather than
        // reaching back into "folder.jpg". Worth pinning down, because the
        // opposite behaviour would mark every file inside a dotted folder as
        // that folder's type.
        Assert.Null(FileClassifier.ClassifyByName("folder.jpg/README"));
        Assert.Null(FileClassifier.ClassifyByName(@"folder.jpg\README"));
        Assert.Null(FileClassifier.ClassifyByName("DCIM/.thumbnails/1234"));
        Assert.Null(FileClassifier.ClassifyByName("photo.jpg/"));
    }

    [Fact]
    public void DotfileWhoseWholeNameIsAKnownExtension_IsAnswered()
    {
        // ".nomedia" is a real, common Android file and its entire name is the
        // extension. It must be answered Unknown, not probed.
        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyByName(".nomedia"));
        // And a dotfile whose whole name is a media extension is still media.
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName(".jpg"));
    }

    [Fact]
    public void ExtensionWithNoStem_ButUnknown_IsNull()
        => Assert.Null(FileClassifier.ClassifyByName(".unheardof"));

    [Fact]
    public void ControlCharactersAndReservedWindowsNames_DoNotThrow()
    {
        // MTP object names are not Windows paths and are not validated as
        // such, so anything can arrive. Path.GetExtension in .NET (Core) no
        // longer rejects invalid path characters; pinning that down here means
        // a future change to that assumption shows up as a test failure rather
        // than an exception mid-scan.
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName("badname.jpg"));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName("CON.jpg"));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName("a:b*c?.jpg"));
        Assert.Null(FileClassifier.ClassifyByName(" "));
    }

    // ---- The extension lying about the content -----------------------------

    [Fact]
    public void ExtensionWins_EvenWhenItLiesAboutTheContent()
    {
        // Documented trade-off, not an oversight: a recognised extension short
        // circuits before any byte is read, so a PDF renamed to .jpg is
        // reported as media and a JPEG renamed to .txt is reported as Unknown
        // and never probed. The design accepts this because reading every
        // file's header over MTP costs ~118ms each, and a wrong extension is
        // rare next to tens of thousands of correct ones.
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyByName("actually_a_pdf.jpg"));
        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyByName("actually_a_jpeg.txt"));
        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyByName("actually_a_jpeg.dat"));
    }
}
