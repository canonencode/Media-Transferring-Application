/// <summary>
/// Tests for <c>FileClassifier.ClassifyBySignature</c> - the fallback that runs
/// only when the extension was unrecognised, and the only thing standing
/// between a photo with a strange extension and being silently dropped.
/// </summary>
public class FileClassifierSignatureTests
{
    /// <summary>
    /// Builds a buffer of exactly <c>FileClassifier.SignatureBytes</c> bytes
    /// whose first bytes are <paramref name="leading"/> and whose tail is zero,
    /// which is what a real short file's header looks like after the reader
    /// fills what it got into a zeroed array.
    /// </summary>
    static byte[] Header(params byte[] leading)
    {
        var header = new byte[FileClassifier.SignatureBytes];
        leading.CopyTo(header, 0);
        return header;
    }

    [Fact]
    public void SignatureBytes_IsTwelve()
    {
        // Pinned because the caller allocates its read buffer from this
        // constant, and two checks read all the way to byte 11: the RIFF
        // subtype ("WEBP" at 8..11) and the ftyp major brand (at 8..11, which
        // is what rejects audio-only MP4). Shrink it below 12 and those checks
        // fail closed via Matches' length guard - every WEBP and MP4 with an
        // unrecognised extension would silently become Unknown.
        Assert.Equal(12, FileClassifier.SignatureBytes);
    }

    // ---- Each signature ----------------------------------------------------

    [Fact]
    public void Jpeg_IsMediaFile()
    {
        // FF D8 FF E0 = JFIF, FF D8 FF E1 = Exif (what a phone camera writes),
        // FF D8 FF DB = raw quantisation-table-first variant.
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(Header(0xFF, 0xD8, 0xFF, 0xE0)));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(Header(0xFF, 0xD8, 0xFF, 0xE1)));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(Header(0xFF, 0xD8, 0xFF, 0xDB)));
    }

    [Fact]
    public void Png_IsMediaFile()
    {
        // Full 8-byte PNG signature, including the CRLF/EOF trap bytes.
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(
            Header(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)));
    }

    [Fact]
    public void Gif_IsMediaFile()
    {
        // "GIF87a" and "GIF89a" - both share the checked "GIF8" prefix.
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(
            Header(0x47, 0x49, 0x46, 0x38, 0x37, 0x61)));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(
            Header(0x47, 0x49, 0x46, 0x38, 0x39, 0x61)));
    }

    [Fact]
    public void Bmp_IsMediaFile()
    {
        // "BM" then a little-endian file size.
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(
            Header(0x42, 0x4D, 0x36, 0x10, 0x00, 0x00)));
    }

    [Fact]
    public void RiffWebp_IsMediaFile()
    {
        // "RIFF" + size + "WEBP".
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(
            Header(0x52, 0x49, 0x46, 0x46, 0x24, 0x10, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50)));
    }

    [Fact]
    public void FtypBoxAtOffsetFour_IsMediaFile()
    {
        // The MP4 family: a big-endian box length, then "ftyp", then the brand.
        // Covers MP4, MOV (qt  ), 3GP and HEIC, which all share this layout.
        // The brand IS inspected, but only to reject the three audio-only ones
        // (see FtypBox_NeedsAPlausibleLengthAndANonAudioBrand); every video
        // and image brand passes without being enumerated.
        byte[] mp4 = { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32 }; // "mp42"
        byte[] mov = { 0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70, 0x71, 0x74, 0x20, 0x20 }; // "qt  "
        byte[] heic = { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63 }; // "heic"
        byte[] threeGp = { 0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70, 0x33, 0x67, 0x70, 0x34 }; // "3gp4"

        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(mp4));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(mov));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(heic));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(threeGp));
    }

    [Fact]
    public void Pdf_IsDocument()
    {
        // "%PDF-1.7"
        Assert.Equal(FileKind.Document, FileClassifier.ClassifyBySignature(
            Header(0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37)));
    }

    [Fact]
    public void PdfIsNotMistakenForAnMp4()
    {
        // Guards the check ORDER as much as the checks: the ftyp test reads
        // byte 4, and in a PDF byte 4 is '-'. If the ftyp test ever widened to
        // something looser, a PDF would start reporting as video.
        FileKind kind = FileClassifier.ClassifyBySignature(
            Header(0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34));

        Assert.Equal(FileKind.Document, kind);
        Assert.NotEqual(FileKind.MediaFile, kind);
    }

    // ---- Buffers that cannot be identified ---------------------------------

    [Fact]
    public void BufferShorterThanRequired_IsUnknown()
    {
        // Eleven bytes of a perfectly valid JPEG. Still Unknown, because a
        // partial header cannot be trusted - the caller's reader loop relies on
        // this to treat a truncated read as "not identified" rather than
        // guessing from whatever arrived.
        var elevenByteJpeg = new byte[11];
        elevenByteJpeg[0] = 0xFF; elevenByteJpeg[1] = 0xD8; elevenByteJpeg[2] = 0xFF;

        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyBySignature(elevenByteJpeg));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]   // enough for the "JXL codestream" test (FF 0A), but must still be rejected
    [InlineData(4)]   // enough for PNG/GIF/EBML, but must still be rejected
    [InlineData(8)]   // enough for the ftyp box-length test, but must still be rejected
    [InlineData(11)]
    public void AnyBufferBelowTwelveBytes_IsUnknown(int length)
    {
        // Each length here is long enough for at least one of the individual
        // checks to have matched if the length guard were dropped. FF 0A as
        // the whole of a 2-byte buffer is the sharpest case: it is a complete,
        // valid JPEG XL codestream signature.
        var buffer = new byte[length];
        if (length >= 2) { buffer[0] = 0xFF; buffer[1] = 0x0A; }

        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyBySignature(buffer));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 }, (int)FileKind.MediaFile)]                                     // JPEG
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, (int)FileKind.MediaFile)]             // PNG
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, (int)FileKind.MediaFile)]                         // GIF
    [InlineData(new byte[] { 0x42, 0x4D, 0x36, 0x04, 0x00, 0x00 }, (int)FileKind.MediaFile)]                         // BMP
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, (int)FileKind.MediaFile)] // RIFF WEBP
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 }, (int)FileKind.Unknown)]   // RIFF WAVE
    [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D }, (int)FileKind.MediaFile)] // ftyp isom
    [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x4D, 0x34, 0x41, 0x20 }, (int)FileKind.Unknown)]   // ftyp M4A
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, (int)FileKind.MediaFile)]                                     // TIFF LE
    [InlineData(new byte[] { 0x4D, 0x4D, 0x00, 0x2A }, (int)FileKind.MediaFile)]                                     // TIFF BE
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, (int)FileKind.MediaFile)]                                     // EBML
    [InlineData(new byte[] { 0xFF, 0x0A }, (int)FileKind.MediaFile)]                                                 // JXL codestream
    [InlineData(new byte[] { 0, 0, 0, 0x0C, 0x4A, 0x58, 0x4C, 0x20 }, (int)FileKind.MediaFile)]                      // JXL container
    [InlineData(new byte[] { 0, 0, 0, 0x0C, 0x6A, 0x50, 0x20, 0x20 }, (int)FileKind.MediaFile)]                      // JPEG 2000
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, (int)FileKind.Document)]                                // PDF
    public void EverySignature_GivesTheSameAnswerInALongBufferWithNoiseAfterTheHeader(byte[] leading, int expected)
    {
        // The caller may hand over more than SignatureBytes - a reader that
        // pulls a whole first block, say. Nothing past byte 11 may influence
        // the answer, so the tail is filled with a non-zero pattern rather than
        // the zeros a fresh array would have. A check that accidentally read
        // past its signature would start disagreeing here.
        var buffer = new byte[4096];
        Array.Fill(buffer, (byte)0xAB, FileClassifier.SignatureBytes, buffer.Length - FileClassifier.SignatureBytes);
        leading.CopyTo(buffer, 0);

        Assert.Equal((FileKind)expected, FileClassifier.ClassifyBySignature(buffer));
    }

    [Fact]
    public void EmptySpan_IsUnknown()
        => Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyBySignature(ReadOnlySpan<byte>.Empty));

    [Fact]
    public void AllZeroBuffer_IsUnknown()
    {
        // The state an unwritten buffer is in. If this ever returned MediaFile,
        // every failed read would silently become a "photo".
        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyBySignature(new byte[12]));
        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyBySignature(new byte[64]));
    }

    [Fact]
    public void BufferLongerThanRequired_IsStillClassified()
    {
        var buffer = new byte[4096];
        buffer[0] = 0x89; buffer[1] = 0x50; buffer[2] = 0x4E; buffer[3] = 0x47;

        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(buffer));
    }

    [Fact]
    public void ExactlyTwelveBytes_IsTheBoundary()
    {
        var twelve = new byte[12];
        twelve[0] = 0xFF; twelve[1] = 0xD8; twelve[2] = 0xFF;
        var eleven = twelve.AsSpan(0, 11);

        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(twelve));
        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyBySignature(eleven));
    }

    [Fact]
    public void UnrecognisedContent_IsUnknown()
    {
        // A SQLite database header and a ZIP header - both common on a phone,
        // neither a signature this classifier knows.
        byte[] sqlite = { 0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20, 0x66, 0x6F, 0x72, 0x6D, 0x61 };
        byte[] zip = { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00 };

        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyBySignature(sqlite));
        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyBySignature(zip));
    }

    // ---- Content vs. a lying extension -------------------------------------

    [Fact]
    public void SignatureSeesThroughAWrongExtension_WhenTheNameWasUnrecognised()
    {
        // The pair that makes the fallback worth its cost: "IMG_0001.xyz" is a
        // real JPEG with an extension nothing recognises. The name check must
        // decline to answer, and the bytes must then identify it as media.
        Assert.Null(FileClassifier.ClassifyByName("IMG_0001.xyz"));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(Header(0xFF, 0xD8, 0xFF, 0xE1)));

        // And the reverse: a document body under an unrecognised name.
        Assert.Null(FileClassifier.ClassifyByName("scan_0001.unknownext"));
        Assert.Equal(FileKind.Document, FileClassifier.ClassifyBySignature(Header(0x25, 0x50, 0x44, 0x46, 0x2D)));
    }

    [Fact]
    public void NonMediaContentUnderAnUnrecognisedName_IsUnknown()
    {
        // The other half of the same deal: an unrecognised extension whose
        // bytes turn out to be nothing interesting costs one device read and
        // then is correctly dropped.
        Assert.Null(FileClassifier.ClassifyByName("blob.qqq"));
        byte[] elf = { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00 };
        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyBySignature(elf));
    }

    // ---- Known weaknesses, pinned deliberately -----------------------------

    [Fact]
    public void BmpNeedsAPlausibleDeclaredSize_NotJustTheLettersBM()
    {
        // "BM" alone is two bytes, so the ASCII text "BMW service " was being
        // reported as a photo. A real BMP declares its total size at offset 2;
        // text that begins with those letters declares a nonsensical one.
        byte[] asciiText = { 0x42, 0x4D, 0x57, 0x20, 0x73, 0x65, 0x72, 0x76, 0x69, 0x63, 0x65, 0x20 };
        byte[] realBmp = { 0x42, 0x4D, 0x36, 0x04, 0x00, 0x00, 0, 0, 0, 0, 0x36, 0x00 };

        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyBySignature(asciiText));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(realBmp));
    }

    [Fact]
    public void RiffContainer_IsMediaOnlyWhenTheSubtypeAtOffsetEightSaysWebp()
    {
        // The subtype at offset 8 is what distinguishes the formats sharing the
        // RIFF container. It used to go unread, so these two were reported as
        // photos - and ".wavpack" is not one of the listed audio extensions, so
        // the bytes really were the last line of defence.
        byte[] wave = { 0x52, 0x49, 0x46, 0x46, 0x24, 0x08, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45 };
        byte[] avi  = { 0x52, 0x49, 0x46, 0x46, 0x24, 0x08, 0x00, 0x00, 0x41, 0x56, 0x49, 0x20 };
        byte[] webp = { 0x52, 0x49, 0x46, 0x46, 0x24, 0x08, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 };

        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyBySignature(wave));
        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyBySignature(avi));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(webp));
        Assert.Null(FileClassifier.ClassifyByName("voice_memo.wavpack"));
    }

    [Fact]
    public void FtypBox_NeedsAPlausibleLengthAndANonAudioBrand()
    {
        // "the ftype is" carries "ftyp" at offset 4 by coincidence; its first
        // four bytes read as a box length of 0x74686520, which is nonsense.
        byte[] asciiCoincidence = { 0x74, 0x68, 0x65, 0x20, 0x66, 0x74, 0x79, 0x70, 0x65, 0x20, 0x69, 0x73 };
        // A real MP4: 24-byte box, "isom" brand.
        byte[] mp4 = { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D };
        // Audio-only MP4 with no extension to exclude it - the brand is all we have.
        byte[] m4a = { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x4D, 0x34, 0x41, 0x20 };

        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyBySignature(asciiCoincidence));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(mp4));
        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyBySignature(m4a));
    }

    [Theory]
    [InlineData("M4A ")]
    [InlineData("M4B ")]
    [InlineData("M4P ")]
    public void FtypBox_EveryAudioOnlyBrand_IsRejected(string brand)
    {
        // Only M4A was covered; M4B (audiobooks) and M4P (iTunes-protected) are
        // the same trap with a different brand string. Note the trailing space
        // - a brand is exactly four bytes.
        var header = new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0, 0, 0, 0 };
        System.Text.Encoding.ASCII.GetBytes(brand).CopyTo(header, 8);

        Assert.Equal(FileKind.Unknown, FileClassifier.ClassifyBySignature(header));
    }

    [Theory]
    [InlineData(0x0004, (int)FileKind.Unknown)]    // below the 8-byte minimum a box header itself needs
    [InlineData(0x0008, (int)FileKind.MediaFile)]  // the minimum: header with no brands at all
    [InlineData(0x0018, (int)FileKind.MediaFile)]  // the common real value (24)
    [InlineData(0x001A, (int)FileKind.Unknown)]    // 26: in range but not 4-aligned
    [InlineData(0x0400, (int)FileKind.MediaFile)]  // 1024: the upper bound, inclusive
    [InlineData(0x0404, (int)FileKind.Unknown)]    // 1028: just over
    [InlineData(0x7468_6520, (int)FileKind.Unknown)] // "the " read as a length
    public void FtypBox_LengthBounds_ArePinnedExactly(int boxLength, int expected)
    {
        // The plausibility rule is >= 8, <= 1024, multiple of 4. Each edge is
        // pinned so that loosening any one of them - which is how the "the
        // ftype is" false positive got in - shows up as a failure here.
        var header = new byte[12];
        header[0] = (byte)(boxLength >> 24); header[1] = (byte)(boxLength >> 16);
        header[2] = (byte)(boxLength >> 8);  header[3] = (byte)boxLength;
        "ftypisom"u8.CopyTo(header.AsSpan(4));

        Assert.Equal((FileKind)expected, FileClassifier.ClassifyBySignature(header));
    }

    [Theory]
    [InlineData(0x0000_000Du, (int)FileKind.Unknown)]   // 13: one short of the 14-byte header
    [InlineData(0x0000_000Eu, (int)FileKind.MediaFile)] // 14: the minimum
    [InlineData(0x1FFF_FFFFu, (int)FileKind.MediaFile)] // just under the ceiling
    [InlineData(0x2000_0000u, (int)FileKind.Unknown)]   // the ceiling itself is out
    [InlineData(0x6573_2057u, (int)FileKind.Unknown)]   // "W se" - what "BMW service" declares
    public void Bmp_DeclaredSizeBounds_ArePinnedExactly(uint declaredSize, int expected)
    {
        // Little-endian at offset 2. Both bounds matter (see the classifier's
        // own comment); each one is pinned at its exact edge.
        var header = new byte[12];
        header[0] = 0x42; header[1] = 0x4D;
        header[2] = (byte)declaredSize;         header[3] = (byte)(declaredSize >> 8);
        header[4] = (byte)(declaredSize >> 16); header[5] = (byte)(declaredSize >> 24);

        Assert.Equal((FileKind)expected, FileClassifier.ClassifyBySignature(header));
    }

    [Fact]
    public void JpegXlInAnIsoBmffContainer_IsMediaFile()
    {
        // The other JXL form: a 12-byte "JXL " signature box, the same shape
        // as JPEG 2000's "jP  " box. It was in the classifier with no test.
        byte[] jxlBox = { 0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A };

        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(jxlBox));
    }

    [Fact]
    public void SignaturesExistForEveryRawAndContainerFormatTheExtensionListClaims()
    {
        // These extensions were all in the media list with no signature behind
        // them, so one arriving with a damaged or missing extension was
        // invisible. DNG matters most - it is what Samsung and Pixel RAW is.
        byte[] tiffLE = { 0x49, 0x49, 0x2A, 0x00, 0x08, 0, 0, 0, 0, 0, 0, 0 };
        byte[] tiffBE = { 0x4D, 0x4D, 0x00, 0x2A, 0, 0, 0, 0x08, 0, 0, 0, 0 };
        byte[] webm   = { 0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0, 0, 0, 0, 0 };
        byte[] jxl    = { 0xFF, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        byte[] jp2    = { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A };

        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(tiffLE));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(tiffBE));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(webm));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(jxl));
        Assert.Equal(FileKind.MediaFile, FileClassifier.ClassifyBySignature(jp2));
    }
}
