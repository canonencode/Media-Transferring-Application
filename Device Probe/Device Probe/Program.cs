using PortableDeviceApiLib;

// --- Step 1: find the device (same as the previous milestone) ---
IPortableDeviceManager deviceManager = new PortableDeviceManagerClass();
string? deviceId = null;
uint deviceCount = 1;
deviceManager.GetDevices(ref deviceId, ref deviceCount);

if (deviceId is null)
{
    Console.WriteLine("No portable device found.");
    Console.WriteLine("Check: is the phone connected via USB, and is 'File transfer (MTP)' mode selected on the phone?");
    return;
}

// Confirm which device we're talking to (useful when more than one phone has
// been connected today - not just a debug leftover, kept intentionally).
var nameBuffer0 = new ushort[260];
uint nameLength0 = (uint)nameBuffer0.Length;
deviceManager.GetDeviceFriendlyName(deviceId, ref nameBuffer0[0], ref nameLength0);
Console.WriteLine($"Connecting to: {new string(Array.ConvertAll(nameBuffer0, c => (char)c)).TrimEnd('\0')}");

// --- Step 2: open a real connection to the device ---
IPortableDevice device = new PortableDeviceClass();
IPortableDeviceValues clientInfo = (IPortableDeviceValues)new PortableDeviceTypesLib.PortableDeviceValuesClass();
device.Open(deviceId, clientInfo);

device.Content(out IPortableDeviceContent content);
content.Properties(out IPortableDeviceProperties properties);
content.Transfer(out IPortableDeviceResources resources);

// WPD_RESOURCE_DEFAULT: identifies "the object's main content stream" - what we
// need to open to read a file's actual bytes (as opposed to just its metadata).
var resourceDefaultKey = new _tagpropertykey
{
    fmtid = new Guid(0xE81E79BE, 0x34F0, 0x41BF, 0xB5, 0x3F, 0xF1, 0xA0, 0x6A, 0xE8, 0x78, 0x42),
    pid = 0
};

// PROPERTYKEYs for the two things we actually need to read on every object.
var nameKey = new _tagpropertykey
{
    fmtid = new Guid(0xEF6B490D, 0x5CD8, 0x437A, 0xAF, 0xFC, 0xDA, 0x8B, 0x60, 0xEE, 0x4A, 0x3C),
    pid = 4 // WPD_OBJECT_NAME
};
var contentTypeKey = new _tagpropertykey
{
    fmtid = new Guid(0xEF6B490D, 0x5CD8, 0x437A, 0xAF, 0xFC, 0xDA, 0x8B, 0x60, 0xEE, 0x4A, 0x3C),
    pid = 7 // WPD_OBJECT_CONTENT_TYPE
};

// OPTIMIZATION: build the list of properties we want ONCE, up front, instead of
// calling GetSupportedProperties() for every single object. GetSupportedProperties
// asks the device "what CAN you tell me about this object" - a full extra round
// trip we don't need, since we always want exactly these same two things. This
// halves the number of device round trips for every object in the tree.
IPortableDeviceKeyCollection wantedProperties = (IPortableDeviceKeyCollection)new PortableDeviceTypesLib.PortableDeviceKeyCollectionClass();
wantedProperties.Add(ref nameKey);
wantedProperties.Add(ref contentTypeKey);

// WPD_CONTENT_TYPE_FOLDER: a real folder (e.g. "DCIM", "Pictures").
var folderType = new Guid(0x27E2E392, 0xA111, 0x48E0, 0xAB, 0x0C, 0xE1, 0x77, 0x05, 0xA0, 0x5F, 0x85);
// WPD_CONTENT_TYPE_FUNCTIONAL_OBJECT: a storage unit at the top of the tree
// ("Phone" = internal storage, "Card" = SD card) - not a folder, but still
// something we need to step into to reach the real folders underneath.
var functionalObjectType = new Guid(0x99ED0160, 0x17FF, 0x4C44, 0x9D, 0x98, 0x1D, 0x7A, 0x6F, 0x94, 0x19, 0x21);

// Extensions we count as "media" - this is how we decide relevance, NOT folder
// names. WhatsApp, Telegram, screenshot tools etc. all use their own non-standard
// folder names, so trusting folder names would silently miss real photos/videos.
// A file's own extension doesn't lie about what it is, wherever it happens to sit.
var mediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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

// Known audio formats that share MP4-family "ftyp" container bytes with real
// video/photo formats - excluded up front so the signature fallback never has
// to guess at them (see the note in LooksLikeMediaBySignature).
var knownAudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".m4a", ".m4b", ".m4p", ".mp3", ".aac", ".wav", ".ogg", ".opus", ".amr", ".flac"
};

// Extensions we're already CERTAIN aren't photos/videos - built from what actually
// showed up as false candidates during real-device testing (.pag = CamScanner's own
// document cache format, .nomedia = Android's literal "don't index this" marker
// file, .crypt14 = WhatsApp's encrypted backups) plus common non-media types.
// This is the fix for the scaling problem: every file listed here skips the
// expensive signature check entirely, so that check only ever runs on files whose
// extension is GENUINELY unknown - which, on a real phone, is a small remainder,
// not "everything we don't already recognize."
var knownNonMediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".nomedia", ".crypt14", ".crypt12", ".pag", ".chck",
    ".json", ".xml", ".txt",
    ".db", ".db-wal", ".db-shm", ".log", ".dat", ".tmp", ".lock",
    ".zip", ".apk", ".bak", ".cfg", ".ini", ".key", ".properties", ".ttf"
};

// Documents - a second "wanted" category alongside photos/videos, tracked and
// counted separately so we can see the breakdown.
var documentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx"
};

int mediaFilesFound = 0;
int documentFilesFound = 0;
int totalFilesSeen = 0;
int caughtBySignatureOnly = 0;
int signatureChecksActuallyRun = 0;
int skippedDueToErrors = 0;
// Each failed object is stored WITH its parent's name, not just its own ID -
// needed so a retry can correctly re-run the Android/data,obb skip check if
// the recovered object turns out to be a folder we recurse back into.
var failedObjectIds = new List<(string ObjectId, string ParentName)>();
int signatureCheckErrors = 0;
var routineErrorSamples = new List<string>();

// CODE REVIEW FIX: when the retry pass recovers a folder that failed
// mid-enumeration, it re-walks that folder from scratch (WPD has no
// "resume from here" concept) - without this, children already counted
// before the original failure got counted and printed a second time.
// Every object that reaches classification goes through this set first;
// re-visits (only possible via a retry re-walk) become harmless no-ops
// instead of duplicates.
var countedObjectIds = new HashSet<string>();

// --- Session health monitoring ---
// A WPD session's stream-reading capability can silently break while property
// queries keep working fine (confirmed by testing: 1606/1607 signature checks
// failed with "device unreachable" after a prior run didn't close its session
// cleanly). A scan can "complete successfully" while quietly missing every
// file that needed the signature fallback, with nothing visibly wrong. We
// watch the error rate in batches and react instead of trusting silence.
const int healthCheckBatchSize = 20;
const double healthCheckErrorThreshold = 0.75; // 75%+ errors in a batch = broken session, not "no matches"
int checksInBatch = 0;
int errorsInBatch = 0;
bool signatureCheckingDisabled = false;

Console.WriteLine("Connected to device. Scanning file tree...\n");

// SAFETY NET: everything we've hit so far has failed *fast* (an error comes
// back in milliseconds), never hung. But nothing guarantees that forever - a
// truly wedged USB/driver state could block a call with no error and no
// timeout of its own. Run the whole scan on a background thread and give up
// after a generous ceiling instead of risking an indefinite freeze; a real
// app with a UI can't just sit frozen with no way out. The threshold is
// generous (30 min) so it only ever fires on an actual hang, not a merely
// slow-but-progressing large scan (our slowest real run was ~15 min).
// Safe to hand these COM objects to a Task.Run thread: both the console
// app's main thread and .NET's thread-pool threads default to MTA (no
// [STAThread] attribute here), and MTA-to-MTA COM access needs no marshaling.
var scanTask = Task.Run(() =>
{
    PrintTree("DEVICE", parentName: "", depth: 0);
    RunRetryPass();
});

const int overallScanTimeoutMs = 30 * 60 * 1000;
bool scanFinishedInTime;
try
{
    // CODE REVIEW FIX: Task.Wait(int) throws the task's own AggregateException
    // immediately if it faults within the timeout window - it does NOT return
    // true and let a fault be discovered afterwards. The old `if
    // (scanTask.IsFaulted)` check below this could never run: reaching it
    // required Wait() to return true, which only happens on RanToCompletion.
    // Catching AggregateException here and unwrapping it is the only place
    // that check could ever actually do anything.
    scanFinishedInTime = scanTask.Wait(overallScanTimeoutMs);
}
catch (AggregateException ex)
{
    throw ex.InnerException ?? ex;
}

if (!scanFinishedInTime)
{
    Console.WriteLine($"\n[WARNING] The scan has not finished after {overallScanTimeoutMs / 60000} minutes " +
        "and looks genuinely stuck (not just slow - every real scan so far has finished well within this). " +
        "Exiting rather than freezing indefinitely. Results printed above are partial; " +
        "unplug/replug the phone and try again.");

    // CODE REVIEW FIX: exiting here used to skip the session-cleanup block
    // below entirely, reproducing the exact "device left locked, every
    // signature check on the NEXT run fails with 'device unreachable'" bug
    // that cleanup exists to prevent - precisely because a timeout is when
    // the session is most likely to already be unhealthy. The scan thread
    // may still be blocked inside a live COM call using these same objects,
    // so this is a best-effort attempt, not a guaranteed-safe one - wrapped
    // in try/catch and itself time-boxed so a stuck cleanup can't turn one
    // hang into two.
    try
    {
        var cleanupTask = Task.Run(() =>
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(wantedProperties);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(resources);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(properties);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(content);
            device.Close();
            System.Runtime.InteropServices.Marshal.ReleaseComObject(device);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(clientInfo);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(deviceManager);
        });
        cleanupTask.Wait(5000);
    }
    catch
    {
        // Best-effort only - the hung call may still own these objects, so
        // failure here is expected sometimes. Still better than never trying.
    }
    Environment.Exit(1);
}

void RunRetryPass()
{
// --- Post-scan retry pass ---
// Must run here, BEFORE the session cleanup below - GetNameAndType/
// DetectKindBySignature need `properties`/`resources` still alive.
// (First attempt put this after cleanup by mistake: crashed immediately with
// "COM object that has been separated from its underlying RCW cannot be
// used" - a real, sharp reminder that release-order matters.)
// The main walk is fully done now, so it's safe to retry the objects that
// failed earlier - unlike retrying INSIDE the walk (which corrupted results,
// see the note in CheckSignatureWithHealthMonitoring), there's no in-flight
// enumerator state left to break. Only re-attempts the specific objects that
// actually failed, not the whole tree. Folders that failed are re-confirmed
// but their children aren't re-listed in this pass - full resumability is a
// job for the SQLite-backed version, this is just cheap, safe recovery of
// what we can get back right now.
if (failedObjectIds.Count > 0)
{
    // Snapshot both the list and its count up front: retrying appends to the
    // live failedObjectIds again on a repeat failure, so reading
    // failedObjectIds.Count *after* the loop would double-count.
    var idsToRetry = failedObjectIds.ToList();
    int originalFailedCount = idsToRetry.Count;
    Console.WriteLine($"\nRetrying {originalFailedCount} previously-failed object(s)...");
    int recovered = 0;
    foreach (var (objectId, parentName) in idsToRetry)
    {
        var (name, isContainer) = GetNameAndType(objectId, parentName);
        if (name == "(unreadable)") continue; // still failing, leave it

        recovered++;
        if (isContainer)
        {
            // CODE REVIEW FIX: this used to recurse unconditionally, with no
            // equivalent of PrintTree's own Android/data,obb guard - a
            // recovered "data" or "obb" object would get fully walked,
            // defeating the one thing `parentName` was stored for. Now uses
            // the exact same shared check PrintTree uses.
            if (ShouldSkipAsBlockedAndroidSubfolder(parentName, name))
            {
                Console.WriteLine($"  [DIR]  {name} (recovered, but blocked - Android/data or Android/obb)");
                continue;
            }

            // Recovering a folder previously meant only confirming it exists -
            // its contents were never (re-)listed, so a failure on an early,
            // high-up folder (e.g. the whole internal storage root) could
            // silently zero out an entire scan even though the retry reported
            // it as "recovered". Walk back into it now that we know it's
            // readable again - safe here since the main walk is fully
            // finished, same reasoning as the rest of this retry pass.
            // (countedObjectIds makes this re-walk idempotent even though it
            // starts from scratch: any child already classified in the main
            // pass before the original failure is skipped, not double-counted.)
            Console.WriteLine($"  [DIR]  {name} (recovered, now walking its contents)");
            PrintTree(objectId, name, depth: 1);
        }
        else
        {
            // CODE REVIEW FIX: this used to duplicate PrintTree's own
            // classification block with two real divergences - it called
            // DetectKindBySignature directly (bypassing the session-health
            // circuit breaker) and never routed through the shared counted-
            // object dedup. Both fixed by calling the same helper PrintTree
            // uses, just with the "recovered" tag prefixes.
            ClassifyAndReportFile(objectId, name, indent: "  ", mediaTag: "[RECOVERED-MEDIA]", docTag: "[RECOVERED-DOC]");
        }
    }
    Console.WriteLine($"Recovered {recovered} of {originalFailedCount} previously-failed object(s) ({originalFailedCount - recovered} still unreadable).");
}
}

// IMPORTANT: without this, the device's session (specifically the "Resources"
// stream-reading channel used by DetectKindBySignature) can stay locked after
// this process exits, causing every signature check on the NEXT run to fail
// with "the device is unreachable" - confirmed by testing (1606/1607 checks
// failed on a second run; 174 real files were silently missed as a result).
// Explicitly closing releases the session cleanly for the next connection.
//
// FOUND ON REVIEW: this alone leaves content/properties/resources/
// wantedProperties still holding live, unreleased COM references AFTER
// Close() - the same class of bug as the per-iteration `values`/`childIds`
// leaks, just at the top level instead of inside the loop. Release every
// session-derived interface (children first, then the device itself) so
// nothing outlives the session we're trying to close.
System.Runtime.InteropServices.Marshal.ReleaseComObject(wantedProperties);
System.Runtime.InteropServices.Marshal.ReleaseComObject(resources);
System.Runtime.InteropServices.Marshal.ReleaseComObject(properties);
System.Runtime.InteropServices.Marshal.ReleaseComObject(content);
device.Close();
System.Runtime.InteropServices.Marshal.ReleaseComObject(device);
System.Runtime.InteropServices.Marshal.ReleaseComObject(clientInfo);
System.Runtime.InteropServices.Marshal.ReleaseComObject(deviceManager);

Console.WriteLine($"\nDone. {mediaFilesFound} media file(s) and {documentFilesFound} document(s) found out of {totalFilesSeen} file(s) seen.");
Console.WriteLine($"({caughtBySignatureOnly} of those were caught only by file signature - their extension wasn't recognized.)");
Console.WriteLine($"Expensive signature check actually ran on {signatureChecksActuallyRun} file(s) (out of {totalFilesSeen} total).");
Console.WriteLine($"{skippedDueToErrors} object(s) skipped after a device error (connection hiccup) instead of crashing the whole scan.");
Console.WriteLine($"Signature check itself errored (not just 'no match') on {signatureCheckErrors} file(s).");
if (routineErrorSamples.Count > 0)
{
    Console.WriteLine("\nSample of routine device errors seen during this scan:");
    foreach (var sample in routineErrorSamples)
    {
        Console.WriteLine($"  {sample}");
    }
}
Console.WriteLine(signatureCheckingDisabled
    ? "Session health: signature checking was DISABLED partway through this scan (see warning above)."
    : "Session health: OK, signature checking ran normally for the whole scan.");

// Recursive walk: for every child of objectId, print it, and if it's a container
// (folder or storage unit), recurse into it - UNLESS it's Android/data or
// Android/obb specifically. Those two are blocked from outside access by Android
// itself since Android 11 (not our choice to skip them - the OS already refuses),
// and in practice they're also where huge irrelevant per-app caches live.
// We do NOT skip the rest of "Android" (e.g. Android/media), since some apps
// still use it for real shareable media.
void PrintTree(string objectId, string parentName, int depth)
{
    IEnumPortableDeviceObjectIDs? childIds = null;
    try
    {
        content.EnumObjects(0, objectId, null, out childIds);
    }
    catch (System.Runtime.InteropServices.COMException ex)
    {
        skippedDueToErrors++;
        failedObjectIds.Add((objectId, parentName));
        if (routineErrorSamples.Count < 10)
        {
            routineErrorSamples.Add($"[EnumObjects] HResult=0x{ex.HResult:X8} {ex.Message}");
        }
        return;
    }

    try
    {
    uint fetched = 0;
    string childId = "";
    do
    {
        try
        {
            childIds.Next(1, out childId, ref fetched);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            skippedDueToErrors++;
            failedObjectIds.Add((objectId, parentName));
            if (routineErrorSamples.Count < 10)
            {
                routineErrorSamples.Add($"[Next] HResult=0x{ex.HResult:X8} {ex.Message}");
            }
            break; // can't reliably continue enumerating this folder past a hiccup
        }
        if (fetched == 0) break;

        // CODE REVIEW FIX: a folder recovered by the retry pass gets re-walked
        // via a fresh EnumObjects call (WPD has no "resume" concept), which
        // would otherwise re-count and re-print every child already handled
        // before the original mid-enumeration failure. Skipping known IDs
        // before even fetching their properties also saves a round trip.
        if (!countedObjectIds.Add(childId))
        {
            continue;
        }

        var (name, isContainer) = GetNameAndType(childId, parentName);
        string indent = new string(' ', depth * 2);

        if (isContainer)
        {
            Console.WriteLine($"{indent}[DIR]  {name}");
            if (!ShouldSkipAsBlockedAndroidSubfolder(parentName, name))
            {
                PrintTree(childId, name, depth + 1);
            }
        }
        else
        {
            ClassifyAndReportFile(childId, name, indent, mediaTag: "[MEDIA]", docTag: "[DOC]");
        }
    } while (fetched > 0);
    }
    finally
    {
        // LEAK FIX: one of these gets created per folder in the tree - never
        // releasing it meant hundreds of unreleased COM references per scan,
        // on top of the per-file `values` leak fixed in GetNameAndType.
        System.Runtime.InteropServices.Marshal.ReleaseComObject(childIds);
    }
}

// Android/data and Android/obb are blocked from external access by Android
// itself since Android 11, and are where huge irrelevant per-app caches
// live. Shared by PrintTree and RunRetryPass so a recovered object can't
// bypass this the way it used to (the retry pass had no equivalent check).
//
// KNOWN LIMITATION: parentName here is objectId's real parent's name in the
// common case (the value threaded through PrintTree's own recursion, and
// through GetNameAndType's failure path). The one case it can be wrong: if
// PrintTree's OWN EnumObjects/Next call fails while enumerating "data" or
// "obb" itself (not a sibling), failedObjectIds stores that object's *own*
// name in the ParentName slot instead of its true parent's name (see
// PrintTree's catch blocks), so a retry on that specific object could miss
// the block. Narrow in practice: Android/data and Android/obb are OS-blocked,
// so GetNameAndType typically fails while first resolving them as a child
// (correctly recorded) well before EnumObjects would ever get a chance to
// fail on them directly.
bool ShouldSkipAsBlockedAndroidSubfolder(string parentName, string name) =>
    parentName == "Android" &&
    (string.Equals(name, "data", StringComparison.OrdinalIgnoreCase) ||
     string.Equals(name, "obb", StringComparison.OrdinalIgnoreCase));

// Shared by PrintTree and RunRetryPass so classification logic (extension
// check -> signature fallback -> count -> print) can't silently drift
// between the two the way it did before this fix (the retry-pass's own copy
// skipped the health-monitoring wrapper and had no de-duplication).
void ClassifyAndReportFile(string objectId, string name, string indent, string mediaTag, string docTag)
{
    totalFilesSeen++;

    // Fast path: trust the extension if it's already a known type - no
    // need to touch the device's actual bytes for the common case.
    string extension = Path.GetExtension(name);
    bool isMedia = mediaExtensions.Contains(extension);
    bool isDocument = documentExtensions.Contains(extension);

    // Fallback path: the extension is unrecognized (missing, wrong, or an
    // app-invented one) AND it isn't already something we know for certain
    // is irrelevant. Read just the file's first few bytes and check them
    // against known signatures - the only place we touch real file
    // content during the scan, deliberately rare since it's a real
    // device round trip, not just a metadata lookup. Always routed through
    // the health-monitoring wrapper (never DetectKindBySignature directly),
    // so a session that's broken during a retry pass trips the same
    // circuit breaker the main walk relies on.
    bool isKnownNonMedia = knownAudioExtensions.Contains(extension) || knownNonMediaExtensions.Contains(extension);
    if (!isMedia && !isDocument && !isKnownNonMedia)
    {
        var kind = CheckSignatureWithHealthMonitoring(objectId);
        if (kind == FileKind.MediaFile) { isMedia = true; caughtBySignatureOnly++; }
        else if (kind == FileKind.Document) { isDocument = true; caughtBySignatureOnly++; }
    }

    if (isMedia)
    {
        mediaFilesFound++;
        Console.WriteLine($"{indent}{mediaTag} {name}");
    }
    else if (isDocument)
    {
        documentFilesFound++;
        Console.WriteLine($"{indent}{docTag} {name}");
    }
}

// Wraps DetectKindBySignature with session-health monitoring: tracks the error
// rate in rolling batches of 20 checks, and if it looks like the session
// itself is broken (not just "these files aren't media"), tries reconnecting
// before giving up and falling back to extension-only classification for the
// rest of the scan.
FileKind CheckSignatureWithHealthMonitoring(string objectId)
{
    if (signatureCheckingDisabled) return FileKind.Unknown;

    // Counted here, not at the call site - this only increments when we're
    // actually about to touch the device, not for calls short-circuited above.
    signatureChecksActuallyRun++;

    int errorsBefore = signatureCheckErrors;
    FileKind kind = DetectKindBySignature(objectId);
    bool errored = signatureCheckErrors > errorsBefore;

    checksInBatch++;
    if (errored) errorsInBatch++;

    if (checksInBatch >= healthCheckBatchSize)
    {
        double errorRate = (double)errorsInBatch / checksInBatch;
        if (errorRate >= healthCheckErrorThreshold)
        {
            // TRIED, DOESN'T WORK: reconnecting mid-scan (device.Close() +
            // Open() + re-fetching content/properties/resources) seemed like
            // the obvious fix, but testing showed it corrupts the walk instead
            // of recovering it - PrintTree's recursion has folder enumerators
            // (IEnumPortableDeviceObjectIDs) from the OLD session still "in
            // flight" on the call stack above the point where we reconnect,
            // and swapping the session out from under them broke enumeration
            // for the rest of the tree (a run that should see ~19,000 files
            // only saw ~17,500 after a mid-scan reconnect). A live reconnect
            // is only safe between separate top-level scans, not inside one.
            // So: stop trying to be clever mid-scan, just stop doing the
            // (now known-unreliable) signature checks and say so clearly.
            signatureCheckingDisabled = true;
            Console.WriteLine($"\n[WARNING] Signature checks are failing at {errorRate:P0} - the device session's " +
                "stream-reading capability looks broken, not just 'these files aren't media'. Disabling the " +
                "signature fallback for the rest of THIS scan (extension-based detection is unaffected and " +
                "continues normally). For full accuracy including unrecognized-extension files, unplug/replug " +
                "the phone (or restart the 'Portable Device Enumerator Service' / WPDBusEnum) and run again.\n");
        }
        checksInBatch = 0;
        errorsInBatch = 0;
    }

    return kind;
}

// Reads the first bytes of an object's content and checks them against known
// file signatures ("magic bytes") - the bytes a format's own encoder always
// writes at the start of the file, which can't be faked by a wrong extension
// the way a filename can.
FileKind DetectKindBySignature(string objectId)
{
    uint optimalTransferSize = 0;
    IStream? wpdStream = null;
    try
    {
        resources.GetStream(objectId, ref resourceDefaultKey, 0 /* STGM_READ */, ref optimalTransferSize, out wpdStream);
        var stream = (System.Runtime.InteropServices.ComTypes.IStream)wpdStream;

        byte[] header = new byte[12];
        stream.Read(header, header.Length, IntPtr.Zero);

        // JPEG
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return FileKind.MediaFile;
        // PNG
        if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) return FileKind.MediaFile;
        // GIF ("GIF8")
        if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38) return FileKind.MediaFile;
        // BMP ("BM")
        if (header[0] == 0x42 && header[1] == 0x4D) return FileKind.MediaFile;
        // WEBP ("RIFF"....'WEBP' - we only check the "RIFF" part here for simplicity)
        if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46) return FileKind.MediaFile;
        // HEIC/MP4/MOV/3GP/M4A all share the same container: an "ftyp" box at byte
        // offset 4. Tried distinguishing audio-only variants (M4A/M4B) by reading
        // the "major brand" field right after it - in practice this isn't
        // reliable, since different encoders put different brand strings there
        // for the same audio-only content (confirmed by testing against a real
        // file). Excluding known audio extensions before we even get here is the
        // simpler, verified-correct fix.
        if (header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70) return FileKind.MediaFile;
        // PDF ("%PDF-")
        if (header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46 && header[4] == 0x2D) return FileKind.Document;

        return FileKind.Unknown;
    }
    catch
    {
        // Distinguish "cleanly checked, no match" from "the stream read itself
        // failed" - found via testing that this was almost always the real
        // cause of missed catches on repeated runs (see the note on
        // device.Close() below), not files genuinely failing to match.
        signatureCheckErrors++;
        return FileKind.Unknown;
    }
    finally
    {
        if (wpdStream is not null)
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(wpdStream);
        }
    }
}

// Object IDs don't carry a name or type by themselves - we ask for their
// "properties" separately, reusing the fixed `wantedProperties` list built above.
//
// RESILIENCE: without this, ANY single device error kills the entire scan and
// every result gathered so far is lost.
//
// LESSON FROM TESTING: we first tried retrying transient errors inline (4
// attempts, growing delay - the pattern a similar real project uses). On this
// device it made things dramatically WORSE - a scan that completed cleanly in
// 5m51s with immediate skip-and-continue didn't finish in 20 minutes with
// inline retry. Reason: some failing objects aren't transient at all, they're
// permanently unreadable, so every retry against them is pure wasted wait time
// (up to ~1.5s per object) multiplied across ~1,440 failures. The fix: skip
// immediately here (fast, proven), and only retry as a SEPARATE pass at the
// end against the much smaller list of objects that actually failed - so a
// slow recovery attempt can never block the main scan's completion.
(string name, bool isContainer) GetNameAndType(string objectId, string parentName)
{
    IPortableDeviceValues? values = null;
    try
    {
        properties.GetValues(objectId, wantedProperties, out values);
        values.GetStringValue(ref nameKey, out string name);
        values.GetGuidValue(ref contentTypeKey, out Guid contentType);
        bool isContainer = contentType == folderType || contentType == functionalObjectType;
        return (name, isContainer);
    }
    catch (System.Runtime.InteropServices.COMException ex)
    {
        skippedDueToErrors++;
        failedObjectIds.Add((objectId, parentName));
        // DIAGNOSTIC (temporary): we've never actually looked at what these
        // "routine" errors are - only the signature-check ones. Sample the
        // first few so we know what we're dealing with before trying to fix it.
        if (routineErrorSamples.Count < 10)
        {
            routineErrorSamples.Add($"[GetValues] HResult=0x{ex.HResult:X8} {ex.Message}");
        }
        return ("(unreadable)", false);
    }
    finally
    {
        // LEAK FIX: this call runs on EVERY object in the tree (19,000+ times
        // on a real phone) - never releasing it meant thousands of unreleased
        // COM references piling up per scan. Strongly suspected contributor to
        // the "device unreachable" session degradation found earlier.
        if (values is not null)
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(values);
        }
    }
}

enum FileKind { Unknown, MediaFile, Document }
