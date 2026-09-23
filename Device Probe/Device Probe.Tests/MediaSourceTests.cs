/// <summary>
/// Tests for <c>MediaSource</c>, which decides which group a file appears under.
///
/// The classification is a guess made from a path, and the tests are written
/// around that: what it must never do is more important than what it usually
/// does. It must not drop a file, must not invent a group from a folder nobody
/// would recognise, and must not depend on a fixed list of apps - which app a
/// phone has is the thing that varies most between devices.
/// </summary>
public class MediaSourceTests
{
    static string Id(string path) => MediaSource.Classify(path).Id;
    static string Label(string path) => MediaSource.Classify(path).Label;

    // ---- The two groups the phone itself defines ---------------------------

    [Theory]
    [InlineData("/Dahili depolama/DCIM/Camera/IMG_0001.jpg")]
    [InlineData("/Phone/DCIM/Camera/20240101_120000.jpg")]
    [InlineData("/Dahili depolama birimi/DCIM/Camera/x.jpg")]
    public void TheCameraFolder_IsTheCameraGroup_WhateverTheStorageIsCalled(string path)
    {
        // Storage names are localised: three different ones across three
        // measured devices. Matching on the DCIM segment rather than the whole
        // path is what makes that irrelevant.
        Assert.Equal(MediaSource.Camera, Id(path));
    }

    [Theory]
    [InlineData("/Dahili depolama/DCIM/Screenshots/Screenshot_1.png")]
    [InlineData("/Dahili depolama/Pictures/Screenshots/a.png")]
    [InlineData("/Phone/Screenshots/b.png")]
    public void ScreenshotFolders_AreFoundWhereverTheVendorPutThem(string path)
    {
        // Seen under DCIM on one device and Pictures on another, which is why
        // this matches a fragment rather than a path.
        Assert.Equal(MediaSource.Screenshot, Id(path));
    }

    [Fact]
    public void CameraWins_WhenAPathCouldReadAsBoth()
    {
        // A screenshot stored inside the camera folder is a real arrangement on
        // some vendors. Order decides it, and this pins which way.
        Assert.Equal(MediaSource.Camera, Id("/P/DCIM/Camera/Screenshot_1.png"));
    }

    // ---- Apps come from the path, not from a list --------------------------

    [Fact]
    public void AKnownApp_GetsItsRealName()
    {
        var (id, label) = MediaSource.Classify(
            "/Dahili depolama/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/IMG.jpg");

        Assert.Equal("app:com.whatsapp", id);
        Assert.Equal("WhatsApp", label);
    }

    [Fact]
    public void SentAndReceived_LandInTheSameGroup()
    {
        // They were separate groups once. A photo the user took and sent is
        // still their photo, and splitting the app in two made the list harder
        // to read without making any decision easier.
        string received = "/P/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/a.jpg";
        string sent = "/P/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/Sent/b.jpg";

        Assert.Equal(Id(received), Id(sent));
    }

    [Fact]
    public void AnUnknownApp_StillGetsAGroupAndAReadableName()
    {
        // The important half: it is NOT swept into "Diğer", where a user would
        // never find it. A phone with an app this code has never heard of must
        // still show that app.
        var (id, label) = MediaSource.Classify("/P/Android/media/com.zzz.coolgallery/Pictures/a.jpg");

        Assert.Equal("app:com.zzz.coolgallery", id);
        Assert.Equal("Coolgallery", label);
    }

    [Theory]
    [InlineData("org.telegram.messenger", "Telegram")]
    [InlineData("com.instagram.android", "Instagram")]
    [InlineData("com.example.photoeditor", "Photoeditor")]
    [InlineData("com.example.app", "Example")]
    [InlineData("standalone", "Standalone")]
    public void AppNames_AreReadableWhetherOrNotTheyAreKnown(string package, string expected)
    {
        Assert.Equal(expected, MediaSource.AppLabel(package));
    }

    [Fact]
    public void AFolderUnderAndroidMediaThatIsNotAPackage_DoesNotBecomeAGroup()
    {
        // Without the dot check, a stray folder name would appear as an app in
        // the tab strip, which reads as a bug to anyone who owns the phone.
        Assert.Equal(MediaSource.Other, Id("/P/Android/media/legacyfiles/a.jpg"));
    }

    [Fact]
    public void AScreenshotFolderInsideAnApp_IsStillAScreenshot()
    {
        // The screenshot rule is checked before the package rule, so this is a
        // decision rather than an accident: a folder called Screenshots holds
        // screenshots whoever wrote it. Pinned so the ordering is not swapped
        // by someone who assumes the app should win.
        Assert.Equal(MediaSource.Screenshot,
            Id("/P/Android/media/com.example.app/Screenshots/a.png"));
    }

    [Fact]
    public void WhatsAppAtTheStorageRoot_IsTheSameAppAsUnderAndroidMedia()
    {
        // Measured on a J7 Prime2: older Android puts it at the root. One phone
        // showing two WhatsApp groups would be wrong, and a comparison against
        // the other layout would read every file as new.
        Assert.Equal(
            Id("/P/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/a.jpg"),
            Id("/Phone/WhatsApp/Media/WhatsApp Images/a.jpg"));
    }

    // ---- Everything else ---------------------------------------------------

    [Theory]
    [InlineData("/P/Download/file.pdf")]
    [InlineData("/P/Downloads/file.pdf")]
    public void TheDownloadFolder_IsItsOwnGroup(string path)
    {
        Assert.Equal(MediaSource.Download, Id(path));
    }

    [Theory]
    [InlineData("/P/Pictures/x.jpg")]
    [InlineData("/P/Movies/x.mp4")]
    [InlineData("/P/x.jpg")]
    [InlineData("")]
    public void AnythingUnrecognised_LandsInOther_RatherThanNowhere(string path)
    {
        // The rule that matters most: every path gets a group. A file with no
        // group would be a file the user cannot reach.
        Assert.Equal(MediaSource.Other, Id(path));
        Assert.Equal("Diğer", Label(path));
    }

    [Fact]
    public void BackslashesAreTreatedAsSeparators()
    {
        // MTP paths use forward slashes, but a path that has been through a
        // Windows API on its way here may not.
        Assert.Equal(MediaSource.Camera, Id(@"\P\DCIM\Camera\a.jpg"));
    }

    [Fact]
    public void ClassificationIsCaseInsensitive()
    {
        // Android storage is case-sensitive, but vendors are not consistent
        // about how they capitalise these particular folders.
        Assert.Equal(MediaSource.Camera, Id("/p/dcim/camera/a.jpg"));
        Assert.Equal("app:com.whatsapp", Id("/P/ANDROID/MEDIA/com.whatsapp/x.jpg"));
    }

    [Fact]
    public void EveryClassificationReturnsANonEmptyLabel()
    {
        // A group with no name is a tab with no name.
        string[] paths =
        [
            "/P/DCIM/Camera/a.jpg", "/P/DCIM/Screenshots/a.png",
            "/P/Android/media/com.whatsapp/a.jpg", "/P/Download/a.pdf",
            "/P/Music/a.mp3", "", "/", "///"
        ];
        foreach (string p in paths)
        {
            Assert.False(string.IsNullOrWhiteSpace(Label(p)), "boş etiket: " + p);
        }
    }
}
