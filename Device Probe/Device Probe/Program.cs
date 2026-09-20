using System.Text;
using PortableDeviceApiLib;

// Windows gives a REDIRECTED stdout the OEM code page (CP437 on this machine),
// and .NET then silently best-fit-folds anything that page can't represent:
// "ağıt.jpg" is written as "agit.jpg" with no error and no '?' to notice.
// Measured, not assumed - this quietly invalidated a Turkish-filename test,
// because the characters that fold (ğ ı ş) leave no trace at all while the
// ones that don't (ç ö ü) come through as high bytes.
Console.OutputEncoding = new UTF8Encoding(false);

// --- Step 1: find the device (same as the previous milestone) ---
IPortableDeviceManager deviceManager = new PortableDeviceManagerClass();

// KNOWN LIMITATION: this asks for exactly one device and scans whichever the
// driver returns first. If a phone and a camera are both plugged in, the user
// is not told which one was chosen.
//
// The documented way to learn the device count is to call GetDevices with a
// NULL array pointer. That is not expressible through this interop: tlbimp
// emits `ref string`, which always passes the address of a valid slot, never
// NULL - so the device reads it as "caller has room for 0 entries", returns
// nothing and reports a count of zero. Tried it; it broke device detection
// outright, which is how we know rather than assume.
//
// Doing this properly needs a hand-written [ComImport] declaration that
// marshals the array as an IntPtr - the same piece of work as batching
// IEnumPortableDeviceObjectIDs::Next. Both belong to the real device layer,
// not to this probe, and choosing between devices is a UI concern anyway.
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

// The API writes the real length back into nameLength0, so use it instead of
// converting all 260 slots and trimming the nulls off afterwards. Same result,
// but it stops depending on a trailing null that a buffer-filling name would
// not have. (This runs once per process - it is a correctness tidy-up, not a
// performance fix, whatever it might look like.)
int nameLength = (int)Math.Min(nameLength0, (uint)nameBuffer0.Length);
Console.WriteLine($"Connecting to: {new string(Array.ConvertAll(nameBuffer0[..nameLength], c => (char)c)).TrimEnd('\0')}");

// --- Step 2: open a real connection to the device ---
IPortableDevice device = new PortableDeviceClass();
IPortableDeviceValues clientInfo = (IPortableDeviceValues)new PortableDeviceTypesLib.PortableDeviceValuesClass();

// This used to be an empty values collection, which is legal but leaves two
// things to chance. WPD's docs say drivers use the client information to
// optimise, and - the part that matters here - an unspecified desired access
// makes Open() ask for read AND write, which takes a heavier lock on the
// device than a read-only session needs. This app is read-only by design
// (see the safety guardrails: it must never be able to damage a phone), so
// saying GENERIC_READ out loud is both more honest and less intrusive.
var clientNameKey = new _tagpropertykey
{
    fmtid = new Guid(0x204D9F0C, 0x2292, 0x4080, 0x9F, 0x42, 0x40, 0x66, 0x4E, 0x70, 0xF8, 0x59),
    pid = 2 // WPD_CLIENT_NAME
};
var clientMajorKey = clientNameKey with { pid = 3 };          // WPD_CLIENT_MAJOR_VERSION
var clientMinorKey = clientNameKey with { pid = 4 };          // WPD_CLIENT_MINOR_VERSION
var clientRevisionKey = clientNameKey with { pid = 5 };       // WPD_CLIENT_REVISION
var clientDesiredAccessKey = clientNameKey with { pid = 9 };  // WPD_CLIENT_DESIRED_ACCESS

const uint GENERIC_READ = 0x80000000;
clientInfo.SetStringValue(ref clientNameKey, "Media Transfer App - Device Probe");
clientInfo.SetUnsignedIntegerValue(ref clientMajorKey, 1);
clientInfo.SetUnsignedIntegerValue(ref clientMinorKey, 0);
clientInfo.SetUnsignedIntegerValue(ref clientRevisionKey, 0);
clientInfo.SetUnsignedIntegerValue(ref clientDesiredAccessKey, GENERIC_READ);

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

// WPD_OBJECT_NAME is documented as the DISPLAY name; the real filename lives in
// WPD_OBJECT_ORIGINAL_FILE_NAME. They usually agree on Android, but "usually" is
// not a basis for naming a file we write to the user's disk.
var originalFileNameKey = new _tagpropertykey
{
    fmtid = new Guid(0xEF6B490D, 0x5CD8, 0x437A, 0xAF, 0xFC, 0xDA, 0x8B, 0x60, 0xEE, 0x4A, 0x3C),
    pid = 12 // WPD_OBJECT_ORIGINAL_FILE_NAME
};

// What the SQLite manifest needs in order to answer "have I already copied
// this file?" without re-reading the phone. WPD_OBJECT_ID is documented as NOT
// stable across sessions, so it cannot be the identity key; PERSISTENT_UNIQUE_ID
// is the property meant for that job.
//
// SETTLED, by measuring per-call cost inside the process with the cache in a
// known state - back-to-back runs, 2 keys then 6: 0.17 ms vs 0.20 ms per
// GetValues call, 18.0s vs 18.6s overall. Extra keys are effectively free.
//
// Getting here took four wrong answers, all from the same mistake: comparing
// wall-clock between runs whose driver cache state differed. What actually
// dominates is that state. The first scan after the phone is connected costs
// ~20 ms per call; every scan after it costs ~0.2 ms - a hundredfold gap that
// swamps every other variable we tried to tune. Since the scan a real user
// experiences is always the cold one, the answer is not fewer properties but
// not rescanning from scratch every time, which is what the SQLite index is for.
var sizeKey = new _tagpropertykey
{
    fmtid = new Guid(0xEF6B490D, 0x5CD8, 0x437A, 0xAF, 0xFC, 0xDA, 0x8B, 0x60, 0xEE, 0x4A, 0x3C),
    pid = 11 // WPD_OBJECT_SIZE
};
var persistentIdKey = new _tagpropertykey
{
    fmtid = new Guid(0xEF6B490D, 0x5CD8, 0x437A, 0xAF, 0xFC, 0xDA, 0x8B, 0x60, 0xEE, 0x4A, 0x3C),
    pid = 5 // WPD_OBJECT_PERSISTENT_UNIQUE_ID
};
var dateModifiedKey = new _tagpropertykey
{
    fmtid = new Guid(0xEF6B490D, 0x5CD8, 0x437A, 0xAF, 0xFC, 0xDA, 0x8B, 0x60, 0xEE, 0x4A, 0x3C),
    pid = 19 // WPD_OBJECT_DATE_MODIFIED
};
// WPD_DEVICE_TYPE, asked of the device object itself rather than of a file.
// This is how we tell a phone in file-transfer mode from the same phone in
// camera mode - see ReportConnectionMode for why that distinction matters.
var deviceTypeKey = new _tagpropertykey
{
    fmtid = new Guid(0x26D4979A, 0xE643, 0x4626, 0x9E, 0x2B, 0x73, 0x6D, 0xC0, 0xC9, 0x2F, 0xDC),
    pid = 15
};

// OPTIMIZATION: build the list of properties we want ONCE, up front, instead of
// calling GetSupportedProperties() for every single object. GetSupportedProperties
// asks the device "what CAN you tell me about this object" - a full extra round
// trip we don't need, since we always want exactly these same two things. This
// halves the number of device round trips for every object in the tree.
IPortableDeviceKeyCollection wantedProperties = (IPortableDeviceKeyCollection)new PortableDeviceTypesLib.PortableDeviceKeyCollectionClass();
wantedProperties.Add(ref nameKey);
wantedProperties.Add(ref contentTypeKey);
wantedProperties.Add(ref originalFileNameKey);
wantedProperties.Add(ref sizeKey);
wantedProperties.Add(ref persistentIdKey);
wantedProperties.Add(ref dateModifiedKey);
// WPD_OBJECT_PARENT_ID is deliberately NOT requested: we walk the tree
// ourselves, so every object's parent is already known. Asking the device for
// something we can already answer is waste regardless of what it costs.

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

// Folders holding only DERIVED copies of files we already scan elsewhere.
// Measured on a real phone: .thumbnails alone held 7,955 of the 17,478 files a
// scan classified as "media" - 46% of the result was miniatures of photos the
// scan had already found, inflating the count and, far worse, using up the
// device's stamina. MTP devices degrade under sustained request volume (every
// error we see is from the documented "device is hung" family), and this phone
// died before reaching DCIM - the one folder that actually holds the camera
// roll. .Links is the same story: 108 usable files but roughly 3,000 objects,
// and 2,990 of the scan's 3,012 errors came from inside it.
//
// Named explicitly rather than by the leading dot. Dot-prefixed only means
// "hidden" on Android, and two of the hidden folders on this very phone -
// .Statuses (WhatsApp statuses) and .Trash (the gallery's recycle bin) - can
// hold real photos a user would want back.
var skippableFolderNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    [".thumbnails"] = "cache folder - holds only derived copies of files scanned elsewhere",
    [".Links"] = "cache folder - holds only derived copies of files scanned elsewhere",
    [".wamocache"] = "cache folder - holds only derived copies of files scanned elsewhere",
    // The gallery's recycle bin. Its contents are photos the user deliberately
    // deleted, so surfacing them in a flattened "all your photos" view would
    // read as a bug rather than a feature. One line to revisit if a "rescue
    // deleted photos" feature is ever wanted.
    [".Trash"] = "the gallery's recycle bin - these are photos the user deleted"
};

// Skipped folders are reported, never dropped silently - the user has to be
// able to see what the scan chose not to look at.
var skippedFolders = new List<string>();

int mediaFilesFound = 0;
int documentFilesFound = 0;
int totalFilesSeen = 0;
int caughtBySignatureOnly = 0;
int signatureChecksActuallyRun = 0;
int signatureCheckErrors = 0;
// Files that reached the signature fallback AFTER the circuit breaker had
// already disabled it. Each one is a file we genuinely did not classify - a
// potential missed photo. Previously the summary said the feature had been
// switched off but never said how much went unchecked because of it.
int skippedBecauseSignatureDisabled = 0;

// Every failure records WHICH call failed, not just that something did. The
// old single counter conflated two very different losses: an EnumObjects/Next
// failure drops an entire subtree, a GetValues failure drops one object. A
// report of "1440 skipped" could therefore mean 1440 files or 40,000, and
// there was no way to tell which - or where in the tree the hole was.
var scanErrors = new List<ScanError>();

// Failed objects get one retry after the main walk. The parent PATH is stored
// rather than just the parent's name, so the retry can re-run the
// Android/data,obb check correctly and so an error can say where it happened.
//
// The stage is stored too because it changes what the path MEANS. A Properties
// failure happens while resolving a child, so the path is the parent's and the
// object's own name is still unknown. An Enumerate failure happens after the
// object was already resolved and entered, so the path is the object's OWN.
// Without this distinction the retry appended the name a second time and
// reported losses at "/Dahili depolama/DCIM/DCIM".
var failedObjects = new List<(string ObjectId, string Path, ScanStage Stage)>();

// Two sets, two distinct jobs. Collapsing them into one is exactly what caused
// the double-counting bug: the main walk claimed an object's ID before it knew
// whether the object was even readable, while the retry pass classified that
// same object through a path that never consulted the set.
var classifiedObjectIds = new HashSet<string>();  // files already counted
var walkedContainerIds = new HashSet<string>();   // folders already enumerated; also breaks cycles

// Guards CloseSession(), which is reachable from three places at once. An int
// rather than a bool because Interlocked has no bool overload.
int sessionClosed = 0;

// Watchdog state. Declared up here because the tree walk writes to it and is
// handed to Task.Run before the watchdog itself is built.
long lastProgressTicks = DateTime.UtcNow.Ticks;
bool stallDetected = false;

// SCAFFOLDING (remove once the timing question is closed): answers "where does
// a scan's time actually go?"
// Where the time actually goes. Added because three separate attempts to
// explain this scan's duration by comparing wall-clock between runs all
// reached different, confident, wrong conclusions - the runs differed in
// driver cache state, not in what we asked for. Measuring inside the process
// removes the guesswork: one run now says how much time went into property
// reads versus folder listing versus content streams.
var timeInGetValues = new System.Diagnostics.Stopwatch();
var timeInEnumObjects = new System.Diagnostics.Stopwatch();
var timeInGetStream = new System.Diagnostics.Stopwatch();
int getValuesCalls = 0;
int enumObjectsCalls = 0;

// SCAFFOLDING (fold into the scan_errors table when SQLite lands).
// A property the device refuses is NOT the same as a property it does not have,
// and collapsing both into null hid real driver failures behind "no data".
// Coverage on the A56 is 100%, so in practice any of these now means something
// genuinely went wrong and deserves to be visible.
int suppressedPropertyErrors = 0;

// SCAFFOLDING (remove when the SQLite store lands): answers "which optional
// properties does Android MTP actually populate?" - measured 100% for all of
// them on the A56. Once every object is a row, this is a COUNT query, not a
// set of hand-kept counters.
// How often the device actually supplies each optional property. Published
// research could not say which of these Android MTP populates reliably, so we
// measure it here rather than design the SQLite schema around an assumption.
int objectsWithOriginalFileName = 0;
int objectsWithSize = 0;
int objectsWithPersistentId = 0;
int objectsWithDateModified = 0;
int namesThatDisagree = 0;
int objectsResolved = 0;
// SCAFFOLDING (remove with the coverage counters).
var fieldSamples = new List<DeviceObject>();
// SCAFFOLDING (delete with identity-dump.txt once the persistent-id question
// is settled): the only reason this list exists is to diff two runs.
var identityLines = new List<string>();

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

ReportConnectionMode();

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
// Cleanup is registered for every abnormal exit too, not just the normal one.
// Previously it sat in straight-line code at the end, so ANY escape that
// wasn't a clean finish - a non-COMException from the scan, a failure inside
// a finally block, Ctrl+C - skipped device.Close() and left the session locked,
// which is precisely the bug that costs the NEXT run its signature checks.
AppDomain.CurrentDomain.ProcessExit += (_, _) => CloseSession();
Console.CancelKeyPress += (_, _) => CloseSession();

var scanTask = Task.Run(() =>
{
    PrintTree("DEVICE", parentPath: "", depth: 0);
    RunRetryPass();
});

// STALL WATCHDOG.
//
// Measured, and it invalidates every timing theory that came before it: a run
// that appeared to take 15.5 minutes used 4.9 seconds of CPU. The process was
// not working slowly, it was blocked inside a single COM call, at 0.00s CPU
// over a 20-second sample, waiting for a phone that had stopped answering
// partway through and never started again - unplugging was the only cure.
//
// So scan duration is not a function of how much we ask for; it is a function
// of when, if ever, the device wedges. That makes the whole-scan timeout the
// wrong instrument: it cannot tell "still working, large library" apart from
// "died twenty minutes ago", and nobody will sit through 30 minutes to find
// out. What distinguishes the two is PROGRESS, so that is what to watch.
//
// device.Cancel() is documented as callable from another thread to abort
// operations in flight - the blocked call then returns an error, our existing
// per-object error handling records it, and the walk unwinds normally instead
// of the process having to be killed.
const int stallSeconds = 45;
var watchdog = new Thread(() =>
{
    while (!scanTask.IsCompleted)
    {
        Thread.Sleep(2000);
        long idleTicks = DateTime.UtcNow.Ticks - Interlocked.Read(ref lastProgressTicks);
        if (TimeSpan.FromTicks(idleTicks).TotalSeconds < stallSeconds) continue;

        stallDetected = true;
        Console.WriteLine($"\n[WARNING] No progress for {stallSeconds} seconds. The device has stopped " +
            "responding mid-scan - this is a wedged MTP session, not a slow one. Asking it to cancel so " +
            "the partial results below are at least reported rather than waiting indefinitely.");
        try { device.Cancel(); } catch (System.Runtime.InteropServices.COMException) { }
        return;
    }
}) { IsBackground = true, Name = "wpd-stall-watchdog" };
watchdog.Start();

const int overallScanTimeoutMs = 30 * 60 * 1000;
try
{
    // Task.Wait(int) throws the task's own AggregateException immediately if
    // it faults within the timeout window - it does NOT return true and let a
    // fault be discovered afterwards.
    if (!scanTask.Wait(overallScanTimeoutMs))
    {
        Console.WriteLine($"\n[WARNING] The scan has not finished after {overallScanTimeoutMs / 60000} minutes " +
            "and looks genuinely stuck (not just slow - every real scan so far has finished well within this). " +
            "Asking the device to cancel the in-flight operation.");

        // DO NOT release the COM objects from this thread. The scan thread may
        // still be inside a live call on them, and the CLR does not hold a
        // reference for the duration of a call - dropping the last reference
        // under a running call can destroy the object mid-use. Best case that
        // throws InvalidComObjectException; worst case it's an access violation
        // inside PortableDeviceApi.dll, which since .NET 4.0 is NOT deliverable
        // as a catchable managed exception, so the try/catch that used to wrap
        // this could never have helped.
        //
        // WPD provides the correct tool: Cancel() is documented as callable
        // from another thread to abort operations in flight. The blocked call
        // then returns an error, the scan thread unwinds normally, and cleanup
        // happens on that thread where nothing is in flight.
        try { device.Cancel(); } catch (System.Runtime.InteropServices.COMException) { }

        if (!scanTask.Wait(10_000))
        {
            // The device ignored Cancel() and the thread is still wedged inside
            // a COM call. There is no safe way to release these objects now, and
            // Environment.Exit would run a graceful CLR shutdown that can itself
            // block on the stuck apartment. Leave immediately instead; the OS
            // reclaims the session when the process dies.
            Console.WriteLine("[FATAL] The device did not respond to Cancel(). Exiting immediately without " +
                "touching the device session. Unplug/replug the phone before the next run.");
            Environment.FailFast("WPD scan thread wedged inside a COM call after Cancel() was ignored.");
        }
    }
}
catch (AggregateException ex)
{
    // Report rather than rethrow: rethrowing here skipped the summary AND the
    // session cleanup, turning one bad scan into a device that the next run
    // also can't read properly.
    var inner = ex.InnerException ?? ex;
    Console.WriteLine($"\n[FATAL] The scan stopped early: {inner.GetType().Name}: {inner.Message}");
    Console.WriteLine("Partial results are printed above and summarised below.");
}
finally
{
    CloseSession();
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
if (failedObjects.Count > 0)
{
    // Snapshot the list up front: a repeat failure during the retry appends to
    // the live list again, so reading its count afterwards would double-count.
    var toRetry = failedObjects.ToList();
    int originalFailedCount = toRetry.Count;
    Console.WriteLine($"\nRetrying {originalFailedCount} previously-failed object(s)...");

    int recoveredFiles = 0;
    int recoveredFolders = 0;
    int stillUnreadable = 0;
    int hiddenSubtrees = 0;

    foreach (var (objectId, path, stage) in toRetry)
    {
        // A Properties failure stored the PARENT's path (the object's own name
        // was never resolved); an Enumerate failure stored the object's OWN
        // path (it had already been resolved and entered). Reconstructing both
        // from the stage is what stops the retry reporting "/DCIM/DCIM".
        bool pathIsParent = stage == ScanStage.Properties;
        string parentPath = pathIsParent
            ? path
            : path[..Math.Max(0, path.LastIndexOf('/'))];

        var retried = GetObjectInfo(objectId, parentPath);
        if (retried is null)
        {
            stillUnreadable++;

            // DIAGNOSTIC: a failed GetValues tells us nothing about WHAT the
            // object was, and the code defaults to "not a container" - so an
            // unreadable folder and everything beneath it disappears with no
            // trace at all. Enumerating it is a second, independent way to ask
            // the same question: if this succeeds, the object was a folder and
            // we silently lost its whole subtree. This is the measurement that
            // tells us whether missing folders are the device's doing or ours.
            try
            {
                content.EnumObjects(0, objectId, null, out IEnumPortableDeviceObjectIDs? probe);
                if (probe is not null)
                {
                    uint probeFetched = 0;
                    probe.Next(1, out string _, ref probeFetched);
                    if (probeFetched > 0) hiddenSubtrees++;
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(probe);
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Genuinely unreachable both ways - nothing more to learn.
            }
            continue;
        }

        objectsResolved++;
        string name = retried.Name;

        if (retried.IsContainer)
        {
            if (ShouldSkipFolder(parentPath, name, out string skipReason))
            {
                Console.WriteLine($"  [SKIP] {name} (recovered, but skipped - {skipReason})");
                skippedFolders.Add($"{parentPath}/{name} - {skipReason}");
                continue;
            }

            // Recovering a folder used to mean only confirming it still exists;
            // its contents were never re-listed, so a failure on a high-up
            // folder could silently zero out an entire branch while the retry
            // cheerfully reported it as "recovered". Walk it properly - safe
            // here because the main walk has fully finished, so no enumerator
            // is still in flight on the stack.
            Console.WriteLine($"  [DIR]  {name} (recovered, now walking its contents)");
            PrintTree(objectId, pathIsParent ? $"{parentPath}/{name}" : path, depth: 1);
            recoveredFolders++;
        }
        else
        {
            ClassifyAndReportFile(retried, parentPath, indent: "  ", mediaTag: "[RECOVERED-MEDIA]", docTag: "[RECOVERED-DOC]");
            recoveredFiles++;
        }
    }

    // Counted only after the work actually happened. The old version
    // incremented before the Android check and before the re-walk, so blocked
    // folders and folders that failed again both still reported as "recovered".
    Console.WriteLine($"Retry result: {recoveredFiles} file(s) and {recoveredFolders} folder(s) recovered, " +
        $"{stillUnreadable} still unreadable.");
    if (hiddenSubtrees > 0)
    {
        Console.WriteLine($"[IMPORTANT] {hiddenSubtrees} of the unreadable object(s) could still be ENUMERATED, " +
            "which means they are folders whose entire contents were silently lost from this scan.");
    }
    int newFailures = failedObjects.Count - originalFailedCount;
    if (newFailures > 0)
    {
        Console.WriteLine($"({newFailures} new failure(s) occurred during the retry pass itself and were not retried again.)");
    }
}
}

Console.WriteLine($"\nDone. {mediaFilesFound} media file(s) and {documentFilesFound} document(s) found out of {totalFilesSeen} file(s) seen.");
Console.WriteLine($"({caughtBySignatureOnly} of those were caught only by file signature - their extension wasn't recognized.)");
Console.WriteLine($"Expensive signature check actually ran on {signatureChecksActuallyRun} file(s) (out of {totalFilesSeen} total).");
Console.WriteLine($"Signature check itself errored (not just 'no match') on {signatureCheckErrors} file(s).");
if (skippedBecauseSignatureDisabled > 0)
{
    Console.WriteLine($"[IMPORTANT] {skippedBecauseSignatureDisabled} file(s) with an unrecognized extension went " +
        "UNCHECKED because the circuit breaker had already disabled signature checking. Any of them could be a " +
        "real photo or video that this scan did not count.");
}
Console.WriteLine(signatureCheckingDisabled
    ? "Session health: signature checking was DISABLED partway through this scan (see warning above)."
    : "Session health: OK, signature checking ran normally for the whole scan.");

// SCAFFOLDING (remove with the stopwatches above).
// Where the scan's time actually went, measured inside the process rather than
// inferred by comparing one run's wall clock against another's. This is the
// only honest way to answer "are extra properties expensive?" - the runs we
// were comparing differed in driver cache state, not in request shape.
long totalMs = timeInGetValues.ElapsedMilliseconds + timeInEnumObjects.ElapsedMilliseconds
    + timeInGetStream.ElapsedMilliseconds;
if (stallDetected)
{
    Console.WriteLine($"\n[IMPORTANT] This scan was CUT SHORT: the device stopped responding and did not " +
        "recover. Everything above is partial - an unknown number of files were never reached. Unplug and " +
        "replug the phone, then scan again; the results of this run should not be treated as a complete " +
        "picture of what is on the device.");
}

Console.WriteLine($"\nTime spent inside device calls ({totalMs:N0} ms total):");
Console.WriteLine($"  {timeInGetValues.ElapsedMilliseconds,9:N0} ms  GetValues      ({getValuesCalls:N0} calls, " +
    $"{(getValuesCalls > 0 ? timeInGetValues.Elapsed.TotalMilliseconds / getValuesCalls : 0):F2} ms each)");
Console.WriteLine($"  {timeInEnumObjects.ElapsedMilliseconds,9:N0} ms  EnumObjects    ({enumObjectsCalls:N0} calls, " +
    $"{(enumObjectsCalls > 0 ? timeInEnumObjects.Elapsed.TotalMilliseconds / enumObjectsCalls : 0):F2} ms each)");
Console.WriteLine($"  {timeInGetStream.ElapsedMilliseconds,9:N0} ms  content streams ({signatureChecksActuallyRun:N0} signature checks)");
Console.WriteLine("  (EnumObjects excludes the per-item Next() calls, which are part of the walk itself.)");

if (suppressedPropertyErrors > 0)
{
    Console.WriteLine($"\n[NOTE] {suppressedPropertyErrors} optional property read(s) failed and were treated as " +
        "\"not supplied\". Coverage is normally 100% on this device, so these are likely real driver errors " +
        "rather than missing data.");
}

// SCAFFOLDING (remove with the coverage counters above).
// Which optional properties this device actually supplies. The SQLite manifest
// is meant to key on size + modified date + a persistent id, so whether those
// arrive is not a detail - it decides whether the "have I copied this already?"
// guarantee can be built on them at all, or needs a content hash instead.
if (objectsResolved > 0)
{
    Console.WriteLine($"\nProperty coverage across {objectsResolved} resolved object(s):");
    void Coverage(string label, int count) =>
        Console.WriteLine($"  {count,7} / {objectsResolved}  ({(double)count / objectsResolved:P1})  {label}");

    Coverage("WPD_OBJECT_ORIGINAL_FILE_NAME (real filename)", objectsWithOriginalFileName);
    Coverage("WPD_OBJECT_SIZE", objectsWithSize);
    Coverage("WPD_OBJECT_DATE_MODIFIED", objectsWithDateModified);
    Coverage("WPD_OBJECT_PERSISTENT_UNIQUE_ID (identity across sessions)", objectsWithPersistentId);
    Console.WriteLine($"  {namesThatDisagree,7} object(s) where the real filename differs from the display name.");

    if (fieldSamples.Count > 0)
    {
        Console.WriteLine("\nSample of what the device returns per file:");
        foreach (var sample in fieldSamples)
        {
            Console.WriteLine($"  name={sample.Name}");
            Console.WriteLine($"    displayName={sample.DisplayName}");
            Console.WriteLine($"    size={(sample.Size?.ToString() ?? "(not supplied)")}  " +
                $"modified={(sample.ModifiedRaw ?? "(not supplied)")}");
            Console.WriteLine($"    persistentId={(sample.PersistentId ?? "(not supplied)")}");
            Console.WriteLine($"    objectId={sample.ObjectId}");
        }
    }
}

// SCAFFOLDING: goes away with identityLines.
File.WriteAllLines("identity-dump.txt", identityLines);
Console.WriteLine($"\n[DIAGNOSTIC] Wrote {identityLines.Count} identity line(s) to identity-dump.txt");

if (skippedFolders.Count > 0)
{
    Console.WriteLine($"\n{skippedFolders.Count} folder(s) deliberately not walked:");
    foreach (var skipped in skippedFolders)
    {
        Console.WriteLine($"  {skipped}");
    }
}

// Errors grouped by which call failed and why, instead of a flat count plus
// the first ten messages. The breakdown is the point: an Enumerate/Next
// failure loses a whole subtree while a Properties failure loses one object,
// and one HResult repeated 1,400 times is a very different problem from five
// different HResults - neither of which the old summary could express.
if (scanErrors.Count > 0)
{
    Console.WriteLine($"\n{scanErrors.Count} device error(s) during this scan, by call site and cause:");
    foreach (var group in scanErrors
        .GroupBy(e => (e.Stage, e.HResult))
        .OrderByDescending(g => g.Count()))
    {
        var (stage, hresult) = group.Key;
        string impact = stage switch
        {
            ScanStage.Enumerate => "lost the folder's entire contents",
            ScanStage.EnumerateNext => "lost the rest of that folder's contents",
            ScanStage.Properties => "lost one object (and its subtree, if it was a folder)",
            _ => "lost one file's signature check"
        };
        Console.WriteLine($"  {group.Count(),6} x [{stage}] 0x{hresult:X8} - {impact}");
        Console.WriteLine($"         e.g. \"{group.First().Message.Trim()}\" at {group.First().ParentPath}/");
    }

    int subtreeLosses = scanErrors.Count(e => e.Stage is ScanStage.Enumerate or ScanStage.EnumerateNext);
    if (subtreeLosses > 0)
    {
        Console.WriteLine($"\n[IMPORTANT] {subtreeLosses} of those errors happened while LISTING a folder, so an " +
            "unknown number of files below those points were never seen at all. Affected folders:");
        foreach (var path in scanErrors
            .Where(e => e.Stage is ScanStage.Enumerate or ScanStage.EnumerateNext)
            .Select(e => e.ParentPath)
            .Distinct()
            .Take(20))
        {
            Console.WriteLine($"  {(path.Length == 0 ? "/ (device root)" : path)}");
        }
    }
}

// Releases the WPD session. Idempotent because it runs from the scan's
// finally, from ProcessExit, and from Ctrl+C - whichever happens first wins
// and the rest are no-ops. Without a reliable Close() the session can stay
// locked after the process exits, and then every signature check on the NEXT
// run fails with "the device is unreachable" (confirmed by testing: 1606/1607
// checks failed on a second run, silently missing 174 real files).
//
// Release order matters: every session-derived interface first, then Close(),
// then the device, then the manager - so nothing outlives the session being
// closed. Getting this wrong crashes with "COM object that has been separated
// from its underlying RCW cannot be used".
void CloseSession()
{
    // Interlocked, not a plain bool: this is reachable from the scan's finally,
    // from ProcessExit and from Ctrl+C, and "read the flag, then set it" leaves
    // a window where two threads both see false and both start releasing. The
    // second release of an already-released RCW throws, but the worse case is
    // one thread inside device.Close() while another is freeing the objects it
    // is using. Exchange makes exactly one caller the winner.
    if (Interlocked.Exchange(ref sessionClosed, 1) == 1) return;
    try
    {
        System.Runtime.InteropServices.Marshal.ReleaseComObject(wantedProperties);
        System.Runtime.InteropServices.Marshal.ReleaseComObject(resources);
        System.Runtime.InteropServices.Marshal.ReleaseComObject(properties);
        System.Runtime.InteropServices.Marshal.ReleaseComObject(content);
        device.Close();
        System.Runtime.InteropServices.Marshal.ReleaseComObject(device);
        System.Runtime.InteropServices.Marshal.ReleaseComObject(clientInfo);
        System.Runtime.InteropServices.Marshal.ReleaseComObject(deviceManager);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[WARNING] Could not close the device session cleanly: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine("Unplug/replug the phone before the next run, or the session may stay locked.");
    }
}

// Recursive walk: for every child of objectId, print it, and if it's a container
// (folder or storage unit), recurse into it - UNLESS it's Android/data or
// Android/obb specifically. Those two are blocked from outside access by Android
// itself since Android 11 (not our choice to skip them - the OS already refuses),
// and in practice they're also where huge irrelevant per-app caches live.
// We do NOT skip the rest of "Android" (e.g. Android/media), since some apps
// still use it for real shareable media.
void PrintTree(string objectId, string parentPath, int depth)
{
    IEnumPortableDeviceObjectIDs? childIds = null;
    try
    {
        enumObjectsCalls++;
        timeInEnumObjects.Start();
        try { content.EnumObjects(0, objectId, null, out childIds); }
        finally { timeInEnumObjects.Stop(); }
    }
    catch (System.Runtime.InteropServices.COMException ex)
    {
        scanErrors.Add(new ScanError(objectId, parentPath, ScanStage.Enumerate, ex.HResult, ex.Message));
        failedObjects.Add((objectId, parentPath, ScanStage.Enumerate));
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
                fetched = 0;
                childIds.Next(1, out childId, ref fetched);
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                // This loses every REMAINING sibling in this folder, not just
                // one object - the enumerator can't be resumed from where it
                // stopped. Recorded as its own stage so the summary can say so
                // out loud instead of burying it in a generic skip count.
                scanErrors.Add(new ScanError(objectId, parentPath, ScanStage.EnumerateNext, ex.HResult, ex.Message));
                failedObjects.Add((objectId, parentPath, ScanStage.EnumerateNext));
                break;
            }
            if (fetched == 0) break;

            // One timestamp write per object tells the watchdog we are alive.
            // Placed after Next() returns rather than after the object is fully
            // handled, so a single very slow object cannot be mistaken for a
            // dead device - progress means the device answered, not that we
            // finished with it.
            Interlocked.Exchange(ref lastProgressTicks, DateTime.UtcNow.Ticks);

            // Resolve the object BEFORE claiming its ID. The previous version
            // claimed the ID first, so an object whose property read then
            // failed was permanently marked "already handled" while never
            // having been counted - and the retry pass, which classifies
            // directly, had no way to notice.
            var obj = GetObjectInfo(childId, parentPath);
            if (obj is null) continue; // recorded in failedObjects; the retry pass owns it now

            objectsResolved++;
            if (!obj.IsContainer && fieldSamples.Count < 5) fieldSamples.Add(obj);

            string name = obj.Name;
            string indent = new string(' ', depth * 2);

            if (obj.IsContainer)
            {
                // Claiming folders in their own set also breaks any cycle the
                // device might report, since a repeated folder is never
                // enumerated twice.
                if (!walkedContainerIds.Add(childId)) continue;

                if (ShouldSkipFolder(parentPath, name, out string skipReason))
                {
                    Console.WriteLine($"{indent}[SKIP] {name} ({skipReason})");
                    skippedFolders.Add($"{parentPath}/{name} - {skipReason}");
                    continue;
                }

                Console.WriteLine($"{indent}[DIR]  {name}");
                PrintTree(childId, $"{parentPath}/{name}", depth + 1);
            }
            else
            {
                ClassifyAndReportFile(obj, parentPath, indent, mediaTag: "[MEDIA]", docTag: "[DOC]");
            }
        } while (fetched > 0);
    }
    finally
    {
        // LEAK FIX: one of these gets created per folder in the tree - never
        // releasing it meant hundreds of unreleased COM references per scan.
        // The null check matters more than it looks: a driver returning a
        // success HResult without writing the out parameter would make this
        // throw NullReferenceException (measured on .NET 10) from inside a
        // finally, which both discards whatever exception was already
        // unwinding and escapes every COMException handler in the program.
        if (childIds is not null)
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(childIds);
        }
    }
}

// Android/data and Android/obb are blocked from external access by Android
// itself since Android 11, and are where huge irrelevant per-app caches
// live. Shared by PrintTree and RunRetryPass so a recovered object can't
// bypass this the way it used to (the retry pass had no equivalent check).
//
// Asks the device which USB mode it is actually in, and warns when that mode
// is one that hides files.
//
// Measured on a Galaxy J7 Prime2, same cable, same code, only the phone's USB
// setting changed:
//   "File transfer" (MTP) -> 289 files
//   "Image transfer" (PTP) ->  222 files
// The 67 missing ones were every .mp4 on the device (including camera videos),
// 42 .webp and 3 .pdf. PTP is the Picture Transfer Protocol - the phone itself
// filters the object list down to still images, so we never even get to ask
// about the rest.
//
// What makes this worth special-casing: the PTP scan reported ZERO errors and
// a healthy session. By every signal the code has, it was a perfect scan. A
// user would see a plausible-looking result and never learn that all of their
// videos were missing from it - and in a flattened "all your photos" view
// there is no folder structure left to notice the absence against. A silently
// incomplete answer is worse here than a loud failure.
void ReportConnectionMode()
{
    IPortableDeviceKeyCollection? deviceKeys = null;
    IPortableDeviceValues? deviceValues = null;
    try
    {
        deviceKeys = (IPortableDeviceKeyCollection)new PortableDeviceTypesLib.PortableDeviceKeyCollectionClass();
        deviceKeys.Add(ref deviceTypeKey);
        properties.GetValues("DEVICE", deviceKeys, out deviceValues);
        deviceValues.GetUnsignedIntegerValue(ref deviceTypeKey, out uint deviceType);

        const uint WPD_DEVICE_TYPE_CAMERA = 1;
        if (deviceType == WPD_DEVICE_TYPE_CAMERA)
        {
            Console.WriteLine("\n[WARNING] This device is connected in CAMERA (PTP) mode, which exposes still " +
                "images only. Videos, documents and some image formats are hidden by the phone itself, so this " +
                "scan cannot see them and will look complete while missing them.");
            Console.WriteLine("To scan everything: on the phone, tap the USB notification and choose " +
                "\"File transfer\" (also shown as MTP, Dosya aktarimi, or Android Auto), then run this again.\n");
        }
        else
        {
            Console.WriteLine($"Connection mode looks right (WPD device type {deviceType}, not camera/PTP).");
        }
    }
    catch (System.Runtime.InteropServices.COMException)
    {
        // Not every driver reports this property. Staying quiet is correct:
        // inventing a warning we can't support would train the user to ignore
        // the one case where it is real.
    }
    finally
    {
        if (deviceValues is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(deviceValues);
        if (deviceKeys is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(deviceKeys);
    }
}

// The two reasons a folder is not worth walking, in one place so the main walk
// and the retry pass can never disagree about them.
//
// Android/data and Android/obb are matched on the parent's PATH, not its bare
// name. Matching the name alone meant any folder called "Android" anywhere in
// the tree - a backup copy, a downloaded archive - silently lost its data/obb
// children, and the parent comparison was case-sensitive while the child
// comparison was not, for no reason.
bool ShouldSkipFolder(string parentPath, string name, out string reason)
{
    if (parentPath.EndsWith("/Android", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(name, "data", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(name, "obb", StringComparison.OrdinalIgnoreCase)))
    {
        reason = "blocked from external access by Android itself since Android 11";
        return true;
    }

    if (skippableFolderNames.TryGetValue(name, out string? knownReason))
    {
        reason = knownReason;
        return true;
    }

    reason = "";
    return false;
}

// Shared by PrintTree and RunRetryPass so classification logic (extension
// check -> signature fallback -> count -> print) can't silently drift
// between the two the way it did before this fix (the retry-pass's own copy
// skipped the health-monitoring wrapper and had no de-duplication).
void ClassifyAndReportFile(DeviceObject obj, string parentPath, string indent, string mediaTag, string docTag)
{
    string objectId = obj.ObjectId;
    string name = obj.Name;

    // The dedup guard lives HERE, at the one place a file actually gets
    // counted, rather than up in the walk. Both callers - the main walk and
    // the retry pass - now pass through it, which they did not before: the
    // retry pass counted every object it recovered a second time.
    if (!classifiedObjectIds.Add(objectId)) return;

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
    else
    {
        return;
    }

    // TEMPORARY DIAGNOSTIC: one machine-readable identity line per kept file,
    // so two runs separated by an unplug/replug can be diffed. This answers the
    // question the SQLite schema hinges on - does PERSISTENT_UNIQUE_ID actually
    // survive a reconnect, or is it just the session's object handle wearing a
    // GUID costume? Remove once that is settled.
    identityLines.Add(string.Join("|",
        $"{parentPath}/{name}",
        obj.PersistentId ?? "-",
        obj.Size?.ToString() ?? "-",
        obj.ModifiedRaw ?? "-",
        obj.ObjectId));
}

// Wraps DetectKindBySignature with session-health monitoring: tracks the error
// rate in rolling batches of 20 checks, and if it looks like the session
// itself is broken (not just "these files aren't media"), tries reconnecting
// before giving up and falling back to extension-only classification for the
// rest of the scan.
FileKind CheckSignatureWithHealthMonitoring(string objectId)
{
    if (signatureCheckingDisabled)
    {
        // Counted, because each of these is a file we did not classify and
        // could not classify - a potential missed photo. The summary used to
        // report only that the feature had switched off, never how much slipped
        // past because of it.
        skippedBecauseSignatureDisabled++;
        return FileKind.Unknown;
    }

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
    IntPtr bytesReadPtr = IntPtr.Zero;
    try
    {
        timeInGetStream.Start();
        try { resources.GetStream(objectId, ref resourceDefaultKey, 0 /* STGM_READ */, ref optimalTransferSize, out wpdStream); }
        finally { timeInGetStream.Stop(); }

        var stream = (System.Runtime.InteropServices.ComTypes.IStream)wpdStream;

        // IStream::Read may legally return FEWER bytes than asked for and still
        // report success. Passing IntPtr.Zero (discarding the count) made a
        // short read indistinguishable from a full one, and since the untouched
        // tail stays zero it matched no signature - so a real photo over a weak
        // cable became a silent "not media". Now we read until we have all 12
        // bytes or the stream genuinely ends.
        byte[] header = new byte[12];
        bytesReadPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(int));
        int filled = 0;
        while (filled < header.Length)
        {
            byte[] chunk = new byte[header.Length - filled];
            timeInGetStream.Start();
            try { stream.Read(chunk, chunk.Length, bytesReadPtr); }
            finally { timeInGetStream.Stop(); }

            int got = System.Runtime.InteropServices.Marshal.ReadInt32(bytesReadPtr);
            if (got <= 0) break; // end of stream - the file is simply shorter than 12 bytes
            Buffer.BlockCopy(chunk, 0, header, filled, got);
            filled += got;
        }

        // Too short to identify. Not an error, and deliberately not counted as
        // one: a 4-byte file is a real answer, not a device failure.
        if (filled < header.Length) return FileKind.Unknown;

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
    catch (System.Runtime.InteropServices.COMException)
    {
        // Distinguish "cleanly checked, no match" from "the stream read itself
        // failed" - testing showed this was almost always the real cause of
        // missed catches on repeated runs, not files genuinely failing to match.
        //
        // Narrowed from a bare catch on purpose: that version also swallowed
        // InvalidCastException and InvalidComObjectException - interop faults
        // that mean something is wrong with our own plumbing - and counted them
        // as ordinary device errors, feeding the circuit breaker a reason to
        // shut down that had nothing to do with the device.
        signatureCheckErrors++;
        return FileKind.Unknown;
    }
    finally
    {
        if (bytesReadPtr != IntPtr.Zero)
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(bytesReadPtr);
        }
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
// Returns ok=false on failure rather than a placeholder name. The old version
// returned ("(unreadable)", false), and that `false` meant "not a container",
// so the caller treated every failed object as a FILE: it got counted in
// totalFilesSeen, and since "(unreadable)" has no extension it fell all the way
// through to the signature check - opening a content stream on an object whose
// property read had just failed. Those guaranteed failures then fed the health
// monitor, which concluded the session was broken and switched off signature
// checking for the rest of the scan. A self-inflicted wound that silently cost
// real photos: every unrecognized-extension file after that point went
// unclassified. The placeholder was also a real filename a device could return.
// A property the device chose not to supply comes back as a COMException from
// the individual getter, not from GetValues itself. Absence is data, not an
// error, so it is never recorded in scanErrors.
string? TryReadString(IPortableDeviceValues values, ref _tagpropertykey key)
{
    try
    {
        values.GetStringValue(ref key, out string value);
        return string.IsNullOrEmpty(value) ? null : value;
    }
    catch (System.Runtime.InteropServices.COMException) { suppressedPropertyErrors++; return null; }
    catch (InvalidCastException) { suppressedPropertyErrors++; return null; } // stored as another VARTYPE
}

ulong? TryReadSize(IPortableDeviceValues values, ref _tagpropertykey key)
{
    try
    {
        values.GetUnsignedLargeIntegerValue(ref key, out ulong value);
        return value;
    }
    catch (System.Runtime.InteropServices.COMException) { suppressedPropertyErrors++; return null; }
    catch (InvalidCastException) { suppressedPropertyErrors++; return null; }
}

DeviceObject? GetObjectInfo(string objectId, string parentPath)
{
    IPortableDeviceValues? values = null;
    try
    {
        getValuesCalls++;
        timeInGetValues.Start();
        try { properties.GetValues(objectId, wantedProperties, out values); }
        finally { timeInGetValues.Stop(); }

        // Only these two are required. GetValues reports per-property failures
        // inside the returned collection rather than failing the whole call, so
        // each optional field is read separately and allowed to be absent -
        // which is exactly what we want to measure.
        values.GetStringValue(ref nameKey, out string displayName);
        values.GetGuidValue(ref contentTypeKey, out Guid contentType);
        bool isContainer = contentType == folderType || contentType == functionalObjectType;

        string? originalFileName = TryReadString(values, ref originalFileNameKey);
        string? persistentId = TryReadString(values, ref persistentIdKey);
        string? modified = TryReadString(values, ref dateModifiedKey);
        ulong? size = TryReadSize(values, ref sizeKey);

        if (originalFileName is not null) objectsWithOriginalFileName++;
        if (persistentId is not null) objectsWithPersistentId++;
        if (modified is not null) objectsWithDateModified++;
        if (size is not null) objectsWithSize++;
        if (originalFileName is not null && originalFileName != displayName) namesThatDisagree++;

        return new DeviceObject(
            objectId,
            // The real filename when the device gives one, the display name
            // otherwise. This is the string a transferred file gets named with.
            originalFileName ?? displayName,
            displayName,
            isContainer,
            size,
            persistentId,
            modified);
    }
    catch (System.Runtime.InteropServices.COMException ex)
    {
        scanErrors.Add(new ScanError(objectId, parentPath, ScanStage.Properties, ex.HResult, ex.Message));
        failedObjects.Add((objectId, parentPath, ScanStage.Properties));
        return null;
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

// One object as the device describes it. Only ObjectId, Name and IsContainer
// are guaranteed; the rest are nullable because "the device did not tell us"
// is a real and common answer, and pretending otherwise (0 for an unknown
// size, DateTime.MinValue for an unknown date) would let missing data pass
// silently into the manifest as though it were measured.
record DeviceObject(
    string ObjectId,
    string Name,
    string DisplayName,
    bool IsContainer,
    ulong? Size,
    string? PersistentId,
    string? ModifiedRaw);

enum FileKind { Unknown, MediaFile, Document }

// Which WPD call failed. The distinction is the whole point: Enumerate and
// EnumerateNext lose a folder's contents (an unknown number of files, possibly
// thousands), while Properties loses a single object. A flat "N objects
// skipped" count cannot tell those apart, so it cannot tell you whether a scan
// that finished "successfully" actually saw your photos.
enum ScanStage { Enumerate, EnumerateNext, Properties }

record ScanError(string ObjectId, string ParentPath, ScanStage Stage, int HResult, string Message);
