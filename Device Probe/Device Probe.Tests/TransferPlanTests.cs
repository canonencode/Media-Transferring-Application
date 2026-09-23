/// <summary>
/// Tests for <c>TransferPlan</c>, which decides where each file lands before
/// any byte is copied.
///
/// Two properties matter more than the rest. It must be deterministic, because
/// a resumed transfer re-plans from the same rows and matches the result
/// against what is already on disk - a plan that renumbered itself would copy
/// everything again under new names. And it must never drop a file: a name the
/// filesystem refuses is repaired, not skipped, because a skipped file is
/// exactly the silent loss this project exists to prevent.
/// </summary>
public class TransferPlanTests
{
    static TransferItem Item(string devicePath, long size = 1000, string? name = null) =>
        new("o:" + devicePath, devicePath, name ?? devicePath[(devicePath.LastIndexOf('/') + 1)..],
            size, "2026-09-22T10:00:00");

    static string[] Paths(TransferPlanResult r) =>
        r.Copies.Select(c => c.RelativePath).ToArray();

    // ---- Where files land ---------------------------------------------------

    [Fact]
    public void FilesLandUnderTheirSource_NotThePhonesFolderTree()
    {
        // Mirroring the phone would carry the scattered-media problem onto the
        // PC, which is half of why this project exists.
        var plan = TransferPlan.Build([
            Item("/Dahili depolama/DCIM/Camera/IMG_0001.jpg"),
            Item("/Dahili depolama/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/Sent/a.jpg"),
            Item("/Dahili depolama/DCIM/Screenshots/Screenshot_1.png"),
            Item("/Dahili depolama/Download/notes.pdf"),
        ]);

        Assert.Contains("Kamera/IMG_0001.jpg", Paths(plan));
        Assert.Contains("WhatsApp/a.jpg", Paths(plan));
        Assert.Contains("Ekran görüntüsü/Screenshot_1.png", Paths(plan));
        Assert.Contains("İndirilenler/notes.pdf", Paths(plan));
    }

    [Fact]
    public void SentAndReceived_ShareOneFolder()
    {
        var plan = TransferPlan.Build([
            Item("/P/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/a.jpg"),
            Item("/P/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/Sent/b.jpg"),
        ]);

        Assert.All(Paths(plan), p => Assert.StartsWith("WhatsApp/", p));
    }

    // ---- Determinism --------------------------------------------------------

    [Fact]
    public void PlanningTheSameFilesTwice_GivesTheSameDestinations()
    {
        // The property a resumed transfer depends on.
        var items = new[]
        {
            Item("/P/DCIM/Camera/IMG_1.jpg"),
            Item("/P/Download/IMG_1.jpg"),
            Item("/P/Pictures/IMG_1.jpg"),
        };

        Assert.Equal(Paths(TransferPlan.Build(items)), Paths(TransferPlan.Build(items)));
    }

    [Fact]
    public void TheInputOrder_DoesNotChangeTheResult()
    {
        // Rows come back from SQLite in whatever order the query gives. If that
        // decided which clashing file got the plain name, a resume after a
        // schema change or an index rebuild would rename half the transfer.
        var items = new[]
        {
            Item("/P/A/x.jpg"), Item("/P/B/x.jpg"), Item("/P/C/x.jpg"),
        };
        var forwards = TransferPlan.Build(items);
        var backwards = TransferPlan.Build(items.Reverse());

        Assert.Equal(Paths(forwards), Paths(backwards));
    }

    // ---- Clashes ------------------------------------------------------------

    [Fact]
    public void TwoFilesWithOneName_BothSurvive_Numbered()
    {
        // Measured on a real phone: four such pairs inside WhatsApp alone.
        var plan = TransferPlan.Build([
            Item("/P/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/IMG.jpg"),
            Item("/P/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/Sent/IMG.jpg"),
        ]);

        Assert.Equal(["WhatsApp/IMG.jpg", "WhatsApp/IMG (2).jpg"], Paths(plan));
        Assert.Equal(2, plan.Copies.Count);
    }

    [Fact]
    public void ManyFilesWithOneName_AllSurvive()
    {
        var items = Enumerable.Range(0, 5).Select(i => Item($"/P/DCIM/Camera/sub{i}/a.jpg")).ToArray();

        var plan = TransferPlan.Build(items);

        Assert.Equal(5, Paths(plan).Distinct().Count());
        Assert.Contains("Kamera/a.jpg", Paths(plan));
        Assert.Contains("Kamera/a (5).jpg", Paths(plan));
    }

    [Fact]
    public void TheSameNameInDifferentFolders_IsNotAClash()
    {
        var plan = TransferPlan.Build([
            Item("/P/DCIM/Camera/a.jpg"),
            Item("/P/Download/a.jpg"),
        ]);

        Assert.Equal(["Kamera/a.jpg", "İndirilenler/a.jpg"], Paths(plan).OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void AFileNamedLikeAnAlreadyNumberedOne_StillGetsItsOwnName()
    {
        // "a (2).jpg" arriving alongside "a.jpg" and a second "a.jpg" must not
        // have its name taken by the numbering.
        var plan = TransferPlan.Build([
            Item("/P/DCIM/Camera/x/a.jpg"),
            Item("/P/DCIM/Camera/y/a.jpg"),
            Item("/P/DCIM/Camera/z/a (2).jpg"),
        ]);

        Assert.Equal(3, Paths(plan).Distinct().Count());
    }

    // ---- Names the filesystem refuses ---------------------------------------

    [Theory]
    [InlineData("a<b.jpg", "a_b.jpg")]
    [InlineData("a:b.jpg", "a_b.jpg")]
    [InlineData("a\"b.jpg", "a_b.jpg")]
    [InlineData("a|b?c*.jpg", "a_b_c_.jpg")]
    [InlineData("a\tb.jpg", "a_b.jpg")]
    public void IllegalCharacters_AreReplacedRatherThanTheFileDropped(string name, string expected)
    {
        var plan = TransferPlan.Build([Item("/P/DCIM/Camera/" + name.Replace('/', '_'), name: name)]);

        Assert.Equal("Kamera/" + expected, plan.Copies[0].RelativePath);
        Assert.Equal(name, plan.Copies[0].RenamedFrom);
        Assert.Equal(1, plan.Renamed);
    }

    [Fact]
    public void AnUnpairedSurrogate_IsRepaired_AndTheRealPairIsLeftAlone()
    {
        // The scanner met this on real hardware: MTP hands back raw UTF-16 and
        // nothing validates it. An emoji is a legitimate pair and must survive.
        var plan = TransferPlan.Build([
            Item("/P/DCIM/Camera/1", name: "lone\uD83Dsurrogate.jpg"),
            Item("/P/DCIM/Camera/2", name: "tatil\U0001F3D6.jpg"),
        ]);

        Assert.Contains("Kamera/lone_surrogate.jpg", Paths(plan));
        Assert.Contains("Kamera/tatil\U0001F3D6.jpg", Paths(plan));
    }

    [Theory]
    [InlineData("photo.jpg.", "photo.jpg")]
    [InlineData("photo.jpg ", "photo.jpg")]
    [InlineData("photo.jpg . ", "photo.jpg")]
    public void TrailingDotsAndSpaces_AreRemoved(string name, string expected)
    {
        // Windows drops them silently, so the file would be created under a
        // name other than the one recorded - and the record is what a resumed
        // transfer matches against.
        var plan = TransferPlan.Build([Item("/P/DCIM/Camera/x", name: name)]);

        Assert.Equal("Kamera/" + expected, plan.Copies[0].RelativePath);
    }

    [Theory]
    [InlineData("CON.jpg")]
    [InlineData("nul.png")]
    [InlineData("COM1.mp4")]
    public void ReservedWindowsNames_AreMadeCreatable(string name)
    {
        // These cannot be created at all, and the failure arrives as a bare
        // access error with nothing to explain it.
        var plan = TransferPlan.Build([Item("/P/DCIM/Camera/x", name: name)]);

        Assert.Equal("Kamera/_" + name, plan.Copies[0].RelativePath);
        Assert.Equal(name, plan.Copies[0].RenamedFrom);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("???")]
    public void ANameThatSanitisesToNothing_StillGetsAFile(string name)
    {
        // Dropping it would lose a file the phone really has.
        var plan = TransferPlan.Build([Item("/P/DCIM/Camera/x", name: name)]);

        Assert.Single(plan.Copies);
        Assert.False(string.IsNullOrWhiteSpace(plan.Copies[0].RelativePath));
        Assert.DoesNotContain("//", plan.Copies[0].RelativePath);
    }

    [Fact]
    public void RenamedFrom_IsOnlySetWhenTheNameActuallyChanged()
    {
        var plan = TransferPlan.Build([Item("/P/DCIM/Camera/IMG_0001.jpg")]);

        Assert.Null(plan.Copies[0].RenamedFrom);
        Assert.Equal(0, plan.Renamed);
    }

    // ---- Totals -------------------------------------------------------------

    [Fact]
    public void TotalBytes_IsWhatThePreflightNeeds()
    {
        // The number checked against free space before anything is copied.
        // Getting it wrong means running out of disk partway, which is the
        // failure the check exists to prevent.
        var plan = TransferPlan.Build([
            Item("/P/DCIM/Camera/a.jpg", size: 1500),
            Item("/P/DCIM/Camera/b.jpg", size: 2500),
        ]);

        Assert.Equal(4000, plan.TotalBytes);
    }

    [Fact]
    public void AnUnknownSize_CountsAsZeroRatherThanBreakingTheTotal()
    {
        // The device reports no size for some objects. A negative or missing
        // value must not make the estimate smaller than the truth by more than
        // that file.
        var plan = TransferPlan.Build([
            Item("/P/DCIM/Camera/a.jpg", size: -1),
            Item("/P/DCIM/Camera/b.jpg", size: 1000),
        ]);

        Assert.Equal(1000, plan.TotalBytes);
        Assert.Equal(2, plan.Copies.Count);
    }

    [Fact]
    public void AnEmptyPlan_IsEmptyRatherThanNull()
    {
        var plan = TransferPlan.Build([]);

        Assert.Empty(plan.Copies);
        Assert.Equal(0, plan.TotalBytes);
        Assert.Equal(0, plan.Renamed);
    }

    [Fact]
    public void EveryFileGetsExactlyOneDestination_AndNoTwoShareIt()
    {
        // The whole contract in one test: nothing lost, nothing overwritten.
        var items = Enumerable.Range(0, 200)
            .Select(i => Item($"/P/Android/media/com.whatsapp/WhatsApp/Media/f{i % 7}/IMG_{i % 11}.jpg"))
            .ToArray();

        var plan = TransferPlan.Build(items);

        Assert.Equal(items.Length, plan.Copies.Count);
        Assert.Equal(items.Length, Paths(plan).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
