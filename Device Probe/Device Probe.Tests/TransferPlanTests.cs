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

    // ---- ForSources: picking part of a phone -------------------------------

    static TransferItem[] AMixedPhone() =>
    [
        Item("/P/DCIM/Camera/IMG_0001.jpg", 1000),
        Item("/P/DCIM/Camera/IMG_0002.jpg", 1000),
        Item("/P/Pictures/Screenshots/shot.png", 500),
        Item("/P/Android/media/com.whatsapp/WhatsApp/Media/a.jpg", 200),
        Item("/P/Download/manual.pdf", 300),
        Item("/P/Music/song.mp3", 400),
    ];

    [Theory]
    [InlineData("all")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NoSelection_KeepsEverything(string? sources)
    {
        // A caller that means "nothing" has no reason to be planning a transfer,
        // so the empty cases read as "unfiltered" rather than as "none".
        var plan = TransferPlan.Build(AMixedPhone());

        Assert.Equal(plan.Copies.Count, TransferPlan.ForSources(plan.Copies, sources).Count);
    }

    [Fact]
    public void OneSource_KeepsOnlyThatSource()
    {
        var plan = TransferPlan.Build(AMixedPhone());

        var kept = TransferPlan.ForSources(plan.Copies, MediaSource.Camera);

        Assert.Equal(2, kept.Count);
        Assert.All(kept, c => Assert.StartsWith("Kamera/", c.RelativePath));
    }

    [Fact]
    public void SeveralSources_KeepAllOfThem()
    {
        var plan = TransferPlan.Build(AMixedPhone());

        var kept = TransferPlan.ForSources(plan.Copies,
            $"{MediaSource.Camera},{MediaSource.Download}");

        Assert.Equal(3, kept.Count);
    }

    [Fact]
    public void AnAppIsPickedByItsPackage()
    {
        var plan = TransferPlan.Build(AMixedPhone());

        var kept = TransferPlan.ForSources(plan.Copies, MediaSource.AppPrefix + "com.whatsapp");

        Assert.Single(kept);
        Assert.StartsWith("WhatsApp/", kept[0].RelativePath);
    }

    [Fact]
    public void AnUnknownSourceKeepsNothing_RatherThanEverything()
    {
        // The dangerous failure would be the other way round: a typo in a
        // selection quietly transferring the whole phone onto a disk chosen for
        // a fraction of it.
        var plan = TransferPlan.Build(AMixedPhone());

        Assert.Empty(TransferPlan.ForSources(plan.Copies, "app:com.nosuchapp"));
    }

    [Fact]
    public void SpacesAndCasingInTheSelection_DoNotChangeIt()
    {
        // It arrives as a command line argument, so it has been through a shell,
        // a JSON message and a join before it gets here.
        var plan = TransferPlan.Build(AMixedPhone());

        Assert.Equal(3, TransferPlan.ForSources(plan.Copies, " CAMERA , download ,, ").Count);
    }

    [Fact]
    public void FilteringNeverRenamesAFile()
    {
        // The whole reason ForSources takes a finished plan. If it renumbered,
        // transferring the camera alone and then the camera again as part of
        // everything would produce two copies of each photo under two names.
        var plan = TransferPlan.Build(AMixedPhone());
        var whole = plan.Copies.ToDictionary(c => c.Item.DevicePath, c => c.RelativePath);

        foreach (string pick in new[] { MediaSource.Camera, MediaSource.Screenshot, "all" })
        {
            foreach (var c in TransferPlan.ForSources(plan.Copies, pick))
            {
                Assert.Equal(whole[c.Item.DevicePath], c.RelativePath);
            }
        }
    }

    [Fact]
    public void ClashingNamesKeepTheirNumbers_WhenTheOtherSourceIsLeftOut()
    {
        // Two files that clash INSIDE one folder keep whatever the full plan
        // gave them, so a selection can never move a photo from "IMG (2).jpg"
        // back to "IMG.jpg" and land on top of a different one.
        var items = new[]
        {
            Item("/P/DCIM/Camera/a/IMG.jpg", 1000),
            Item("/P/DCIM/Camera/b/IMG.jpg", 1000),
            Item("/P/Download/other.pdf", 100),
        };
        var plan = TransferPlan.Build(items);

        var kept = TransferPlan.ForSources(plan.Copies, MediaSource.Camera);

        Assert.Equal(2, kept.Count);
        Assert.Equal(2, kept.Select(c => c.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ---- NextFreeName: clashes against the world outside the plan ----------

    [Fact]
    public void AFreeNameIsTheOriginalWithATwo_WhenNothingElseIsTaken()
    {
        // Numbering starts at 2 because the file keeping the place is the 1.
        Assert.Equal(
            Path.Combine(@"D:\out\Kamera", "IMG_0001 (2).jpg"),
            TransferPlan.NextFreeName(@"D:\out\Kamera\IMG_0001.jpg", _ => false));
    }

    [Fact]
    public void ItKeepsCountingPastNamesThatAreAlreadyTaken()
    {
        // Three earlier versions of the same photo are a real thing to find in a
        // folder that has been backed up repeatedly.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(@"D:\out", "a (2).jpg"),
            Path.Combine(@"D:\out", "a (3).jpg"),
            Path.Combine(@"D:\out", "a (4).jpg"),
        };

        Assert.Equal(
            Path.Combine(@"D:\out", "a (5).jpg"),
            TransferPlan.NextFreeName(@"D:\out\a.jpg", taken.Contains));
    }

    [Fact]
    public void TwoFilesAskingInTheSamePass_DoNotBothGetTheSameName()
    {
        // The bug this exists to prevent. Both are decided before either is
        // written, so a check that only asks the disk answers "free" twice and
        // the second copy lands on top of the first.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string first = TransferPlan.NextFreeName(@"D:\out\a.jpg", claimed.Contains);
        claimed.Add(first);
        string second = TransferPlan.NextFreeName(@"D:\out\a.jpg", claimed.Contains);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TheExtensionStaysAnExtension()
    {
        // "a.jpg (2)" would be a file Windows opens with nothing.
        string free = TransferPlan.NextFreeName(@"D:\out\holiday.tar.gz", _ => false);

        Assert.Equal(".gz", Path.GetExtension(free));
        Assert.EndsWith("holiday.tar (2).gz", free);
    }

    [Fact]
    public void AFileWithNoExtension_GetsANumberAndNothingElse()
    {
        Assert.Equal(
            Path.Combine(@"D:\out", "README (2)"),
            TransferPlan.NextFreeName(@"D:\out\README", _ => false));
    }

    [Fact]
    public void TheFolderIsNeverChanged()
    {
        // A renamed file must not quietly move: the destination folder is the
        // one thing the plan already decided.
        string free = TransferPlan.NextFreeName(@"D:\out\WhatsApp\a.jpg", _ => false);

        Assert.Equal(@"D:\out\WhatsApp", Path.GetDirectoryName(free));
    }

    // ---- What the adversarial pass found -----------------------------------

    [Fact]
    public void TheWordAll_IsRecognisedWhateverItsCasing()
    {
        // Every other token in a selection is matched case-insensitively, and
        // this one arrives having been through a shell, a JSON message and a
        // join. "ALL" matching nothing made the copier print "Nothing to do"
        // and the setup page report a comfortable fit, over a phone with
        // nothing backed up.
        var plan = TransferPlan.Build(AMixedPhone());

        Assert.Equal(plan.Copies.Count, TransferPlan.ForSources(plan.Copies, "ALL").Count);
        Assert.Equal(plan.Copies.Count, TransferPlan.ForSources(plan.Copies, " All ").Count);
    }

    [Fact]
    public void TwoObjectsSharingOnePath_GetTheSameNamesWhateverOrderTheyArriveIn()
    {
        // MTP keys on object handles, not names, so one folder can hold two
        // different objects called the same thing - and the scan deliberately
        // keeps both rows rather than collapsing them. Their path and their name
        // both tie, and the query that reads them back has no ORDER BY, so a
        // stable sort left the pair in whatever order SQLite happened to return.
        // Which one got "a.jpg" and which got "a (2).jpg" could then change
        // between runs, and a resumed transfer would copy both again under each
        // other's names.
        var rows = new[]
        {
            new TransferItem("o:11", "/P/DCIM/Camera/a.jpg", "a.jpg", 1000, "2026-09-22T10:00:00"),
            new TransferItem("o:22", "/P/DCIM/Camera/a.jpg", "a.jpg", 2000, "2026-09-22T11:00:00"),
        };

        var forwards = TransferPlan.Build(rows);
        var backwards = TransferPlan.Build(rows.Reverse());

        Assert.Equal(
            forwards.Copies.Single(c => c.Item.ObjectId == "o:11").RelativePath,
            backwards.Copies.Single(c => c.Item.ObjectId == "o:11").RelativePath);
        Assert.Equal(
            forwards.Copies.Single(c => c.Item.ObjectId == "o:22").RelativePath,
            backwards.Copies.Single(c => c.Item.ObjectId == "o:22").RelativePath);
    }

    [Fact]
    public void APlannedName_LeavesRoomForTheCopiersPartSuffix()
    {
        // NTFS refuses any path component over 255 characters and the long-path
        // prefix does not lift it. Every file is staged as "<name>.part" first,
        // so a 255-character name the destination would accept became a
        // 260-character one it refused - and the failure was deterministic, so
        // the file failed identically on every retry and could never be backed
        // up at all. Android's download manager truncates titles to exactly this
        // boundary, so such names are ordinary.
        string name = new string('a', 251) + ".jpg";   // 255 characters

        var plan = TransferPlan.Build([Item("/P/DCIM/Camera/x", name: name)]);
        string planned = plan.Copies[0].RelativePath["Kamera/".Length..];

        Assert.True(planned.Length + ".part".Length <= 255,
            $"planned name is {planned.Length} characters; staged as .part it is " +
            $"{planned.Length + 5}, which NTFS refuses");
    }

    [Fact]
    public void ATruncatedName_KeepsItsExtension()
    {
        // The extension is how the file opens. Cutting it off to fit would
        // hand back something Windows treats as a document of no kind.
        string name = new string('b', 300) + ".jpg";

        var plan = TransferPlan.Build([Item("/P/DCIM/Camera/x", name: name)]);

        Assert.EndsWith(".jpg", plan.Copies[0].RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public void ATruncatedName_NeverSplitsASurrogatePair()
    {
        // Cutting between the halves of a pair produces an unpaired surrogate,
        // which is the exact thing SafeFileName exists to repair - so the fix
        // for one problem must not manufacture the other.
        string name = new string('c', 248) + "\U0001F600\U0001F600\U0001F600.jpg";

        string safe = TransferPlan.SafeFileName(name);

        Assert.DoesNotContain(safe, char.IsSurrogate(safe[^1]) ? "_" : "\uFFFF");
        for (int i = 0; i < safe.Length; i++)
        {
            if (char.IsHighSurrogate(safe[i]))
            {
                Assert.True(i + 1 < safe.Length && char.IsLowSurrogate(safe[i + 1]),
                    "a high surrogate was left without its pair");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(safe[i]), "a lone low surrogate survived");
            }
        }
    }

    [Fact]
    public void TruncationDoesNotCostAFile_WhenTwoLongNamesCollide()
    {
        // Cutting names to fit creates collisions that were not there before.
        // Unique has to absorb them, or two photographs share one destination.
        string a = new string('d', 260) + "-first.jpg";
        string b = new string('d', 260) + "-second.jpg";

        var plan = TransferPlan.Build([
            Item("/P/DCIM/Camera/1", name: a),
            Item("/P/DCIM/Camera/2", name: b),
        ]);

        Assert.Equal(2, Paths(plan).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
