/// <summary>
/// Tests for <c>FolderPolicy.ShouldSkip</c>.
///
/// Every false positive here costs the user real photos - a folder that is
/// skipped is never walked, and nothing downstream ever learns it existed.
/// Every false negative costs scan time: .thumbnails alone held 7,955 derived
/// files on one measured device.
///
/// Paths are shaped the way the walker builds them: PrintTree starts with
/// parentPath "" and each level appends "/{name}", so a folder's parent path is
/// always slash-separated and always begins with a slash.
/// </summary>
public class FolderPolicyTests
{
    static bool Skip(string parentPath, string name) =>
        FolderPolicy.ShouldSkip(parentPath, name, out _);

    static string Reason(string parentPath, string name)
    {
        FolderPolicy.ShouldSkip(parentPath, name, out string reason);
        return reason;
    }

    // ---- Android/data and Android/obb --------------------------------------

    [Theory]
    [InlineData("/Internal storage/Android", "data")]
    [InlineData("/Internal storage/Android", "obb")]
    [InlineData("/Android", "data")]
    [InlineData("/Android", "obb")]
    [InlineData("/SD card/Android", "data")]
    public void AndroidDataAndObb_AreSkipped(string parentPath, string name)
    {
        Assert.True(Skip(parentPath, name));
        Assert.Contains("Android", Reason(parentPath, name));
    }

    [Theory]
    [InlineData("/Internal storage/ANDROID", "DATA")]
    [InlineData("/Internal storage/android", "data")]
    [InlineData("/Internal storage/AnDrOiD", "ObB")]
    [InlineData("/Internal storage/Android", "Data")]
    public void AndroidDataAndObb_MatchCaseInsensitively_OnBothPathAndName(string parentPath, string name)
        => Assert.True(Skip(parentPath, name));

    [Fact]
    public void AndroidMedia_IsNotSkipped()
    {
        // The one that must never be lost: Android/media is where WhatsApp and
        // friends put real user photos on newer devices. It sits right beside
        // data and obb, so a rule that skipped "everything under Android"
        // would take it too.
        Assert.False(Skip("/Internal storage/Android", "media"));
        Assert.False(Skip("/Internal storage/Android", "obb_extra"));
        Assert.False(Skip("/Internal storage/Android", "databases"));
    }

    [Fact]
    public void DataAndObb_OutsideAndroid_AreNotSkipped()
    {
        // This is what the path-based rule bought: before it, the check looked
        // at the bare name, so any folder called "data" or "obb" anywhere in
        // the tree was dropped along with everything under it.
        Assert.False(Skip("/Internal storage/DCIM", "data"));
        Assert.False(Skip("/Internal storage/Pictures/MyApp", "data"));
        Assert.False(Skip("/Internal storage", "obb"));
        Assert.False(Skip("", "data"));
    }

    [Fact]
    public void FolderWhoseNameMerelyEndsInAndroid_DoesNotTriggerTheRule()
    {
        // "/Internal storage/MyAndroid" must not be read as ".../Android".
        // The rule compares the last path SEGMENT for equality (it used to be
        // an EndsWith on "/Android", where the leading slash did this job), so
        // this test is really guarding that it stays a whole-segment match and
        // never degrades to a suffix or substring one.
        Assert.False(Skip("/Internal storage/MyAndroid", "data"));
        Assert.False(Skip("/Internal storage/NotAndroid", "obb"));
        Assert.False(Skip("/Internal storage/Androids", "data"));
    }

    [Fact]
    public void TrailingOrDoubledSeparators_DoNotDefeatTheAndroidRule()
    {
        // Empty segments are dropped before counting, so a stray separator
        // cannot push the real Android folder to "three segments deep" and
        // make it look like somebody's own album.
        Assert.True(Skip("/Internal storage/Android/", "data"));
        Assert.True(Skip("//Internal storage//Android", "obb"));
        Assert.True(Skip("/Internal storage/Android//", "data"));
    }

    [Fact]
    public void NullArguments_AreNotSkipped_AndLeaveReasonEmpty()
    {
        // The policy guards null explicitly rather than throwing. Failing OPEN
        // is the right direction here: an unexpected null from the device
        // layer should cost a walk, never silently lose a subtree.
        Assert.False(FolderPolicy.ShouldSkip(null!, "data", out string reason1));
        Assert.Equal("", reason1);
        Assert.False(FolderPolicy.ShouldSkip("/Internal storage/Android", null!, out string reason2));
        Assert.Equal("", reason2);
    }

    [Fact]
    public void AndroidRuleIsAnchoredToTheStorageRoot_SoUserAlbumsAreNotPruned()
    {
        // The OS-blocked folder is always <storage>/Android. Matching merely on
        // a path ENDING in "/Android" also pruned anyone's own album called
        // Android - real photo loss, and completely silent.
        Assert.True(Skip("/Internal storage/Android", "data"));
        Assert.True(Skip("/Phone/Android", "obb"));

        Assert.False(Skip("/Phone/DCIM/Android", "data"));
        Assert.False(Skip("/Internal storage/Pictures/Holiday/Android", "obb"));
    }

    [Fact]
    public void BackslashSeparatedParentPath_StillMatchesTheAndroidRule()
    {
        // The rule used to be hard-coded to the literal "/Android", so a path
        // built with Windows separators made it stop firing - failing OPEN,
        // which here means walking into a folder Android itself refuses.
        Assert.True(Skip(@"\Internal storage\Android", "data"));
        Assert.False(Skip(@"\Phone\DCIM\Android", "data"));
    }

    [Fact]
    public void StickerFolders_AreSkipped_ButRealPhotoFoldersAreNot()
    {
        // The aim is what is worth backing up. On a real phone stickers
        // outnumbered actual camera photos two to one in some folders, and
        // WhatsApp names one of them "Backup Excluded Stickers" itself.
        Assert.True(Skip("/Phone/WhatsApp/Media", "WhatsApp Stickers"));
        Assert.True(Skip("/Phone/WhatsApp/Media", "WhatsApp Backup Excluded Stickers"));
        Assert.True(Skip("/Phone/Telegram", "Telegram Stickers"));
        Assert.True(Skip("/Phone/Pictures", "stickers"));

        // Screenshots and received images stay - the owner asked for both.
        Assert.False(Skip("/Phone/DCIM", "Screenshots"));
        Assert.False(Skip("/Phone/WhatsApp/Media", "WhatsApp Images"));
        Assert.False(Skip("/Phone/DCIM", "Camera"));
    }

    [Theory]
    [InlineData("WhatsApp Stickers")]
    [InlineData("STICKERS")]
    [InlineData("MyStickerPack")]   // substring, mid-word
    public void StickerFolders_ReasonSaysStickers_NotCacheOrAndroid(string name)
    {
        // The reason is printed beside every [SKIP] line. A user seeing their
        // sticker folder skipped must read "stickers", not one of the other
        // two explanations - each rule has its own message and they must not
        // bleed into each other.
        string reason = Reason("/Phone/WhatsApp/Media", name);

        Assert.Contains("stickers", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cache", reason);
        Assert.DoesNotContain("Android", reason);
    }

    [Fact]
    public void EachRule_HasItsOwnDistinctReason()
    {
        // Three rules, three messages. If two ever collapsed into one string
        // the user would lose the ability to tell "deleted on purpose" from
        // "derived copies" from "the OS blocks this".
        string android = Reason("/Phone/Android", "data");
        string cache = Reason("/Phone/DCIM", ".thumbnails");
        string trash = Reason("/Phone", ".Trash");
        string sticker = Reason("/Phone/WhatsApp", "Stickers");

        Assert.Equal(4, new[] { android, cache, trash, sticker }.Distinct().Count());
    }

    [Fact]
    public void MatchingSurvivesTurkishCulture()
    {
        // Every one of the three rules has an 'I' in its match string
        // ("Android", ".Links", "Sticker"). Under tr-TR a culture-sensitive
        // ignore-case comparison maps 'I' to dotless U+0131 and none of them
        // would match their uppercase form, so on a Turkish machine
        // /ANDROID/DATA would be walked and .LINKS would be enumerated at
        // ~118ms per object. This is what proves the comparer is ordinal.
        System.Globalization.CultureInfo original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");

            Assert.True(Skip("/Internal storage/ANDROID", "DATA"));
            Assert.True(Skip("/Internal storage", ".LINKS"));
            Assert.True(Skip("/Internal storage", ".THUMBNAILS"));
            Assert.True(Skip("/Internal storage/WhatsApp", "STICKERS"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    // ---- Cache / derived-copy folders --------------------------------------

    [Theory]
    [InlineData(".thumbnails")]
    [InlineData(".Links")]
    [InlineData(".wamocache")]
    public void CacheFolders_AreSkipped(string name)
    {
        Assert.True(Skip("/Internal storage/DCIM", name));
        Assert.Contains("cache folder", Reason("/Internal storage/DCIM", name));
    }

    [Theory]
    [InlineData(".THUMBNAILS")]
    [InlineData(".Thumbnails")]
    [InlineData(".tHuMbNaIlS")]
    [InlineData(".links")]
    [InlineData(".LINKS")]
    [InlineData(".WaMoCaChE")]
    public void CacheFolders_MatchCaseInsensitively(string name)
        => Assert.True(Skip("/Internal storage/DCIM", name));

    [Fact]
    public void CacheFolders_AreSkippedWhereverTheyAppear()
    {
        // Unlike the Android rule, these are matched by name alone, on purpose:
        // .thumbnails turns up under DCIM, under Pictures, under app folders.
        Assert.True(Skip("", ".thumbnails"));
        Assert.True(Skip("/Internal storage", ".thumbnails"));
        Assert.True(Skip("/Internal storage/Pictures/Screenshots", ".thumbnails"));
        Assert.True(Skip("/Internal storage/Android/media/com.whatsapp", ".thumbnails"));
    }

    [Fact]
    public void Trash_IsSkipped_ForADifferentStatedReason()
    {
        Assert.True(Skip("/Internal storage", ".Trash"));
        // The reason string is not decoration - it is printed to the user, and
        // "deleted on purpose" is a different message from "derived copies".
        string reason = Reason("/Internal storage", ".Trash");
        Assert.Contains("deleted", reason);
        Assert.DoesNotContain("cache folder", reason);
    }

    // ---- Folders that must survive -----------------------------------------

    [Fact]
    public void HiddenFoldersThatHoldRealPhotos_AreNotSkipped()
    {
        // The reason the skip list is named explicitly instead of matching a
        // leading dot: on Android a dot only means "hidden", and .Statuses
        // holds WhatsApp status images the user may well want.
        Assert.False(Skip("/Internal storage/WhatsApp/Media", ".Statuses"));
        Assert.False(Skip("/Internal storage", ".hidden_album"));
        Assert.False(Skip("/Internal storage", ".nomedia_folder"));
    }

    [Theory]
    [InlineData("DCIM")]
    [InlineData("Camera")]
    [InlineData("Pictures")]
    [InlineData("Screenshots")]
    [InlineData("Movies")]
    [InlineData("Download")]
    [InlineData("WhatsApp")]
    [InlineData("thumbnails")]     // no leading dot - a user folder, not the cache
    [InlineData("Links")]          // no leading dot
    [InlineData("Trash")]          // no leading dot
    [InlineData("data")]           // bare name, outside Android
    public void OrdinaryFolders_AreNotSkipped(string name)
    {
        Assert.False(Skip("/Internal storage", name));
        Assert.Equal("", Reason("/Internal storage", name));
    }

    // ---- Contract details ---------------------------------------------------

    [Fact]
    public void ReasonIsEmptyWhenNotSkipped_AndNonEmptyWhenSkipped()
    {
        Assert.Equal("", Reason("/Internal storage/DCIM", "Camera"));
        Assert.NotEqual("", Reason("/Internal storage/DCIM", ".thumbnails"));
        Assert.NotEqual("", Reason("/Internal storage/Android", "data"));
    }

    [Fact]
    public void NamesARealPhoneCannotProduce_AreHandledWithoutThrowing()
    {
        // Folder names with emoji (U+1F4F7 camera), Turkish letters
        // (Fotograflarim with g-breve and dotless i), an empty name, and a
        // name that is only whitespace. Android's own UI would refuse most of
        // these, so they were untestable before this class was pulled out.
        Assert.False(Skip("/Internal storage/DCIM", "\U0001F4F7 Holiday"));
        Assert.False(Skip("/Internal storage/DCIM", "Fotoğraflarım"));
        Assert.False(Skip("/Internal storage/DCIM", ""));
        Assert.False(Skip("/Internal storage/DCIM", "   "));
        Assert.False(Skip("", ""));
        // A folder whose name merely contains a skippable one.
        Assert.False(Skip("/Internal storage", "my.thumbnails.backup"));
        Assert.False(Skip("/Internal storage", ".thumbnails2"));
        // Trailing whitespace stops the exact-name match, and that is the
        // intended behaviour, not an oversight: the rule fails OPEN so that a
        // real album named ".thumbnails " is never skipped. See the note on
        // FolderPolicy.ShouldSkip. Over-scanning costs time, under-scanning
        // costs photos.
        Assert.False(Skip("/Internal storage", ".thumbnails "));
        Assert.False(Skip("/Internal storage/Android", "data "));
    }

    [Fact]
    public void VeryLongPathAndName_AreHandled()
    {
        string deep = string.Concat(Enumerable.Repeat("/folder", 500));

        Assert.False(Skip(deep, "Camera"));
        Assert.True(Skip(deep, ".thumbnails"));
        Assert.False(Skip(deep + "/Android", "data")); // 500 levels deep is nobody's OS folder
        Assert.False(Skip("/Internal storage", new string('a', 3000)));
    }
}
