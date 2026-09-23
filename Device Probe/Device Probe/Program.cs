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
string friendlyName = new string(Array.ConvertAll(nameBuffer0[..nameLength], c => (char)c)).TrimEnd('\0');
Console.WriteLine($"Connecting to: {friendlyName}");

// Printed because this string is the candidate key for the manifest's device
// table, and the whole "have I already copied this file?" guarantee rests on
// it being the same string for the same phone tomorrow. A WPD device id is a
// PnP path; whether it stays stable across USB ports and reconnects is not
// something the documentation settles, so it gets measured.
Console.WriteLine($"Device id: {deviceId}");

// The serial number is the identity that should key the manifest, not the WPD
// device id above. That id is a PnP path whose middle section is a generated
// instance string for a function without its own serial; it happened to match
// across two USB ports on this machine, but "happened to" is not a guarantee
// to hang "have I already copied this file?" on. Windows itself records the
// real serial on the parent device, so the phone is publishing one - ask for
// it directly rather than parsing it back out of a path.
var serialKey = new _tagpropertykey
{
    fmtid = new Guid(0x26D4979A, 0xE643, 0x4626, 0x9E, 0x2B, 0x73, 0x6D, 0xC0, 0xC9, 0x2F, 0xDC),
    pid = 9 // WPD_DEVICE_SERIAL_NUMBER
};
var modelKey = serialKey with { pid = 8 };        // WPD_DEVICE_MODEL
var manufacturerKey = serialKey with { pid = 7 }; // WPD_DEVICE_MANUFACTURER

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
// camera mode - see IsCameraMode and ConsoleScanSink for why that matters.
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

// Guards CloseSession(), which is reachable from three places at once. An int
// rather than a bool because Interlocked has no bool overload. Declared up here
// because the measurement mode below closes the session and returns without
// ever reaching the scan.
int sessionClosed = 0;

// --- Measurement mode ----------------------------------------------------
// Not a feature: a way to answer three questions with numbers instead of
// guesses, before the copier is designed around any of them.
//
//   1. Are stored object ids still valid in a later session? WPD documents
//      them as NOT stable across sessions, and a copier that trusts them would
//      work on one device and quietly fail on another.
//
//      What this actually answers, and the distinction cost a real transfer:
//      it tests a NEW PROCESS against a phone that has stayed plugged in, and
//      there the ids hold. They do NOT survive the cable coming out - a copy
//      run straight after a replug failed on every file with 0x80042009,
//      "invalid object handle". So a scan describes a connection, not a phone,
//      and the copier now says so when it sees three of those in a row.
//   2. What does opening a stream cost per file? Measured at ~13 ms on this
//      phone and 278 ms on another, and 13,630 of them is either three minutes
//      or an hour.
//   3. What throughput do many small files get? 31.99 MB/s was measured on ONE
//      large file, which says little about the real mix.
if (args.Length > 0 && args[0] == "--measure-copy")
{
    MeasureCopy(
        args.Length > 1 && int.TryParse(args[1], out int measureCount) ? measureCount : 40,
        args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "mt-measure"));
    CloseSession();
    return;
}

// Both are stored on the scan row now, as signature_checks_run and
// caught_by_signature_only.
//
// GAP: they would be better as COUNT queries over a per-file classified_by
// column, which still cannot be filled. OnFile reports WHAT a file is, never
// HOW that was decided, so no sink can tell an extension match from a signature
// match. The column needs that argument added to the interface first.
int caughtBySignatureOnly = 0;
int signatureChecksActuallyRun = 0;
// Does two jobs. As a statistic it reaches the scan row as
// signature_check_errors; as the circuit breaker's error signal it is read by
// diffing this global before and after each call.
//
// GAP: a failed signature check still produces no scan_error row, so the
// database records how many failed but never which file or why. That needs a
// Stream member on ScanStage - there is none - and a sink.OnError call from
// DetectKindBySignature's catch, which today reports nothing at all. The
// breaker's own signal would also be cleaner as a bool return than a global
// diff, which is the kind of coupling that makes a counter hard to move.
int signatureCheckErrors = 0;

// The walk no longer prints anything. It reports to a sink, which is what lets
// the SQLite writer consume the same scan without the walk knowing it exists.
//
// Storage is allowed to fail; the scan is not. A database that cannot be opened
// - a full disk, a file another process has locked - must not cost the user a
// scan of their phone, so it is reported and dropped rather than thrown. The
// same rule inside CompositeScanSink covers a sink that breaks mid-scan.
// PROBE_NO_DB=1 scans without recording. Two honest uses, both of which need
// the SAME build back to back: measuring what recording actually costs, and
// exercising the console-only fallback on real hardware. Comparing against a
// timing from an older build is how this project produced four wrong theories
// about where its time went.
bool recordingDisabled = Environment.GetEnvironmentVariable("PROBE_NO_DB") == "1";

SqliteScanSink? sqliteSink = null;
if (recordingDisabled)
{
    Console.WriteLine("PROBE_NO_DB=1: this scan will not be recorded.");
}
else
{
    try
    {
        sqliteSink = new SqliteScanSink();
        // Not printed in copy mode, which reaches this line before it knows it
        // is a copy - and no scan is being recorded there.
        if (args.Length == 0 || args[0] != "--copy")
        {
            Console.WriteLine($"Recording this scan to: {SqliteScanSink.DefaultDatabasePath}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[SINK FAILED] SQLite could not be opened, so this scan will not be recorded: {ex.Message}");
    }
}

using var sinks = sqliteSink is null
    ? new CompositeScanSink(new ConsoleScanSink())
    : new CompositeScanSink(new ConsoleScanSink(), sqliteSink);

// Deliberately typed as the interface. The walk used to hold the console sink's
// concrete type and read its counters back to build the outcome, which quietly
// made that one sink the scan's memory and left no room for a second. Declaring
// it this way is what stops that from creeping back: nothing below can reach
// past IScanSink, so anything the outcome needs has to be counted here.
IScanSink sink = sinks;

var health = new SessionHealthMonitor();

// The census the walk reports on its own behalf, counted at the single point
// where a file is classified. It lives in the library rather than as four ints
// here so it can be tested without a phone - these are the numbers the user
// reads to decide whether a scan can be trusted, and everything in this file
// needs real hardware to exercise.
var tally = new ScanTally();

// One door for errors, so the walk's record and the sinks' can never disagree.
// They could before: the outcome was built from the console sink's list, so an
// error the walk knew about but had not forwarded simply would not have counted.
void ReportError(ScanError error)
{
    tally.RecordError(error);
    sink.OnError(error);
}


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
// The database does not make this set redundant, despite appearances. The file
// table deliberately has no uniqueness constraint and no upsert - collapsing
// two objects that share a path would be silent data loss, see the schema
// comment in SqliteScanSink - but even if it did dedupe writes, the set stops
// the walk from re-READING an object it has already handled, and that guard
// sits in front of GetObjectInfo and the signature check, both device round
// trips. A database cannot prevent work that happens before the row is ever
// produced. walkedContainerIds is the same story plus cycle-breaking.
var classifiedObjectIds = new HashSet<string>();  // files already counted
var walkedContainerIds = new HashSet<string>();   // folders already enumerated; also breaks cycles


// Same guard for the verdict. The watchdog reports just before FailFast, and
// the main thread reports in its finally; if the scan unwedges in the moments
// between the watchdog's Wait timing out and the process dying, both fire.
//
// It serialises OnScanFinished against OnScanFinished and NOTHING ELSE. An
// earlier version of this comment claimed it also kept two threads off the
// sinks' lists; it does not, and saying so hid a real race. When the watchdog
// reports, the scan thread is by construction still alive - the watchdog only
// gets there because Wait() timed out - so it may still be inside OnFile or
// ReportError while the watchdog walks the tally's error list, the console
// sink's lists and the SQLite connection. Known and unfixed: section C of
// docs/INCELEME-SQLITE-2026-09-22.md.
int outcomeReported = 0;

// Folders the retry pass got back now live on the tally, which is also what
// computes SubtreeLosses from them - see ScanTally.MarkContainerRecovered.
// Keeping the set and the count in one place is the point: they were apart
// before, and the verdict reported recovered folders as lost, telling the user
// photographs were gone when they were listed on the screen above.

// Watchdog state. Declared up here because the tree walk writes to it and is
// handed to Task.Run before the watchdog itself is built.
long lastProgressTicks = DateTime.UtcNow.Ticks;
bool stallDetected = false;
bool scanFaulted = false;

// Set by the watchdog, read by the walk. An int because Volatile/Interlocked
// have no bool overloads; 1 means "stop walking, the device is gone".
int scanAborted = 0;

// Stored on the scan row as file_property_misses, and deliberately NOT as a
// scan_error row: a missing optional property is absence, not failure - the
// same reason TryReadString does not report one - and an error row would
// pollute the "lost one object" group with objects that were read perfectly
// well. It is also derivable from the file rows, where a NULL size or date on a
// non-container IS the miss; the counter earns its place by making that
// readable without a query.
// Counts a FILE lacking a property the device otherwise supplies for every file.
// Containers are excluded on purpose: the storage root has no filename, size or
// modified date, and counting it produced a constant "3 errors" on every scan
// of every device - which the summary then called "likely real driver errors".
// It was one object, missing the three things a container never has.
int filePropertyMisses = 0;


// The device table's row, assembled once and handed over whole. These used to
// be three loose reads that were printed and then dropped: OnScanStarted
// received only the id and the friendly name, so a sink meant to key on the
// serial could not actually see the serial.
var identity = new DeviceIdentity(
    WpdId: deviceId,
    FriendlyName: friendlyName,
    SerialNumber: ReadDeviceString(serialKey),
    Manufacturer: ReadDeviceString(manufacturerKey),
    Model: ReadDeviceString(modelKey));

Console.WriteLine($"Serial number: {identity.SerialNumber ?? "(not supplied)"}");
Console.WriteLine($"Manufacturer/model: {identity.Manufacturer ?? "?"} / {identity.Model ?? "?"}");
if (identity.KeyIsFallback)
{
    Console.WriteLine("[NOTE] This device reports no serial number, so its history is filed under the WPD id, " +
        "which embeds the USB port. Plugged into a different port it will look like a different device.");
}

// --- Copy mode -----------------------------------------------------------
// The transfer itself, and deliberately not part of the scan. They answer
// different questions - the scan asks what the phone holds, this asks for the
// bytes of what a scan already found - and joining them would mean a transfer
// could only ever start from a scan taken seconds earlier, when the useful case
// is the opposite: look on Monday, copy on Tuesday.
//
// It runs before OnScanStarted on purpose. A copy must not open a scan row: an
// unfinished scan is a real state this database records, and writing one that
// nothing will ever finish would leave a permanent 'running' row lying about
// what happened.
//
// PROBE_NO_DB is not honoured here. Scanning without recording is a legitimate
// thing to want; copying without recording is the exact failure this project
// exists to prevent, so the ledger is not optional.
if (args.Length > 1 && args[0] == "--copy")
{
    RunCopy(
        args[1],
        args.Length > 2 && long.TryParse(args[2], out long wantedScan) ? wantedScan : 0,
        args.Length > 3 ? args[3] : "all",
        args.Length > 4 && args[4] is "all" or "hepsi");
    CloseSession();
    return;
}


bool cameraMode = IsCameraMode();
sink.OnScanStarted(identity, cameraMode);

// Built in one place because it is reported from two - the watchdog just before
// it kills the process, and the normal finally. Those two used to assemble the
// record separately, which is how they came to disagree: only one of them
// excluded folders the retry pass had recovered, so the same scan could be
// called lossy or clean depending on which thread got there first.
//
// Declared here rather than beside the other helpers because a local function
// can only capture what is already in scope, and every figure below is.
ScanOutcome BuildOutcome(bool completed, bool stalled, bool faulted) =>
    tally.BuildOutcome(completed, stalled, faulted, cameraMode,
        new SignatureStats(
            ChecksRun: signatureChecksActuallyRun,
            CaughtBySignatureOnly: caughtBySignatureOnly,
            CheckErrors: signatureCheckErrors,
            SkippedByBreaker: health.SkippedBecauseDisabled,
            CheckingDisabled: health.CheckingDisabled),
        filePropertyMisses);

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

// Ctrl+C does NOT close the session itself, deliberately. The handler runs on a
// thread-pool thread while the scan thread is very likely inside a COM call, so
// releasing the objects here is the same use-after-release hazard the timeout
// path was rewritten to avoid: worst case an access violation inside
// PortableDeviceApi.dll, which .NET cannot catch. Instead it does what the
// watchdog does - raise the abort flag so the walk stops at the next object,
// ask the device to cancel whatever is in flight, and cancel the termination so
// the main thread's finally can close the session on a thread that is idle.
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Volatile.Write(ref scanAborted, 1);
    Console.WriteLine("\n[CANCELLED] Stopping the scan. Partial results will be reported below.");
    try { device.Cancel(); } catch (Exception) { }
};

var scanTask = Task.Run(() =>
{
    PrintTree("DEVICE", parentPath: "");
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
        // PROBE_FORCE_STALL exists because the abort path cannot otherwise be
        // exercised without waiting for a phone to genuinely wedge, and that
        // path is where the damage happens: a scan cut short must still report
        // itself as partial rather than look complete. Setting it made the
        // walk abort on a healthy device and proved the reporting end to end -
        // and the first run of that test is what revealed Cancel() alone does
        // not stop anything. Off unless deliberately set.
        long idleTicks = DateTime.UtcNow.Ticks - Interlocked.Read(ref lastProgressTicks);
        bool forced = Environment.GetEnvironmentVariable("PROBE_FORCE_STALL") is not null;
        if (!forced && TimeSpan.FromTicks(idleTicks).TotalSeconds < stallSeconds) continue;

        // The flag comes FIRST, and it is what actually stops the walk.
        //
        // Cancel() alone does not, and a forced test proved it: the watchdog
        // fired, Cancel() was called, and the scan carried on for another 4,838
        // objects before wedging anyway, never reaching the summary. The reason
        // is obvious in hindsight - Cancel() aborts operations IN FLIGHT, and
        // this scan is not one long operation but ~14,500 tiny ones. Cancelling
        // the current call just makes that one call fail; the loop moves to the
        // next object. Something has to tell the LOOP to stop.
        // stallDetected is written BEFORE the Volatile.Write, not after. A plain
        // bool has no ordering guarantee of its own; publishing it ahead of the
        // volatile store means any thread that observes scanAborted==1 also sees
        // stallDetected==true. Written the other way round there is no formal
        // happens-before edge to the read that builds ScanOutcome.
        stallDetected = true;
        Volatile.Write(ref scanAborted, 1);

        Console.WriteLine($"\n[WARNING] No progress for {stallSeconds} seconds. The device has stopped " +
            "responding mid-scan - this is a wedged MTP session, not a slow one. Abandoning the walk so the " +
            "partial results are reported rather than waiting indefinitely.");

        // Flushed explicitly. Console output is buffered when redirected to a
        // file, and a wedged scan produces nothing further to push the buffer
        // out - so this warning sat invisible in memory for 22 minutes while
        // the user watched a log that appeared to have simply stopped.
        Console.Out.Flush();

        // Still worth calling: it can unblock a thread already stuck inside a
        // COM call, which the flag on its own cannot reach.
        //
        // Catches Exception, not just COMException. On a soft stall the walk can
        // unwind in milliseconds, the main thread's finally then releases the
        // device, and this call lands on a severed RCW - which throws
        // InvalidComObjectException, not a COMException. Unhandled on a plain
        // Thread that kills the process.
        try { device.Cancel(); } catch (Exception) { }

        // ESCALATION, and the reason this is not just a nicety: measured on a
        // real wedge, the scan thread was blocked INSIDE a COM call at 0.00s
        // CPU for 22 minutes. Cancel() did not unblock it, and the abort flag
        // could not help either - the flag is checked between objects, and a
        // thread stuck inside a call never gets back to the check. Nothing in
        // process can recover that thread.
        //
        // So: give it a short grace period to unwind, and if it does not,
        // leave. Waiting out the 30-minute overall timeout is not a safety
        // net, it is a hang with a longer name.
        // Wait rethrows the task's AggregateException if the scan faulted
        // inside the grace window. Unhandled on a plain Thread that kills the
        // process before the main thread's finally can close the session or
        // report - so a fault is treated as "it stopped", which is true.
        bool unwound;
        try { unwound = scanTask.Wait(20_000); }
        catch (AggregateException) { unwound = true; }

        if (!unwound)
        {
            Console.WriteLine("[FATAL] The device is not responding and the scan thread cannot be recovered - " +
                "it is blocked inside a driver call that ignored Cancel(). Exiting now rather than waiting. " +
                "Unplug and replug the phone before scanning again.");

            // Report what WAS found before leaving. FailFast skips every
            // finally, every handler and the whole summary, so without this
            // the user sees a process that simply died - no counts, no verdict,
            // no indication that 9,440 files had already been listed above.
            // "Here is what I found and it is incomplete" is a far better
            // answer than silence, and silence is exactly the failure this
            // project exists to prevent.
            if (Interlocked.Exchange(ref outcomeReported, 1) == 0)
            {
                // The outcome now carries the census itself, so the sinks print
                // and store the full summary here. This used to be a bare
                // "found before the device stopped answering" line, because the
                // counts lived in the console sink and nothing else could see
                // them - which meant a scan that died this way was never
                // recorded anywhere but the screen.
                sink.OnScanFinished(BuildOutcome(completed: false, stalled: true, faulted: false));
            }
            Console.Out.Flush();
            Environment.FailFast("WPD scan thread unrecoverable: wedged inside a COM call, Cancel() ignored.");
        }
        return;
    }
}) { IsBackground = true, Name = "wpd-stall-watchdog" };
watchdog.Start();

// No overall timeout here any more. There used to be a 30-minute one that
// called Cancel() without raising the abort flag - a stale copy of the
// watchdog written before we measured that Cancel() alone stops nothing. It
// could only ever fire on a scan that was PROGRESSING for over 30 minutes
// (the watchdog catches a wedge in 65 seconds), and it would then kill that
// healthy scan with no summary at all. Two mechanisms for one job means a
// standing debt to keep them in step, and that debt is exactly what produced
// the bug. The watchdog is the single mechanism now.
try
{
    // Task.Wait() throws the task's own AggregateException if the scan faulted.
    scanTask.Wait();
}
catch (AggregateException ex)
{
    // Report rather than rethrow: rethrowing here skipped the summary AND the
    // session cleanup, turning one bad scan into a device that the next run
    // also can't read properly.
    scanFaulted = true;
    var inner = ex.InnerException ?? ex;
    Console.WriteLine($"\n[FATAL] The scan stopped early: {inner.GetType().Name}: {inner.Message}");
    Console.WriteLine("Partial results are printed above and summarised below.");
}
finally
{
    CloseSession();

    // Told once, explicitly, whatever happened. Without this a sink has no way
    // to distinguish a complete census from a scan the watchdog cut short -
    // and a store that records a partial scan as complete will later conclude
    // that everything it did not see has been deleted from the phone.
    if (Interlocked.Exchange(ref outcomeReported, 1) == 0)
    {
        sink.OnScanFinished(BuildOutcome(
            // NOT just IsCompletedSuccessfully. A walk stopped by the abort flag
            // returns normally, so the task "succeeds" - and ScanOutcome documents
            // Completed as "reached the end under its own power". A sink trusting
            // that alone (the SQLite one does) would record a cut-short scan as a
            // full census and later conclude everything it missed was deleted.
            completed: scanTask.IsCompletedSuccessfully && Volatile.Read(ref scanAborted) == 0,
            stalled: stallDetected,
            faulted: scanFaulted));
    }
}

// Last, after the summary, because it is housekeeping rather than a finding -
// but said out loud all the same. This is the user's own scan history being
// deleted, and a tool that removes data quietly is a tool nobody can audit.
if (sqliteSink is { PrunedScanIds.Count: > 0 } pruned)
{
    Console.WriteLine($"\nRetention: removed {pruned.PrunedScanIds.Count} older scan(s) of this device " +
        $"(#{string.Join(", #", pruned.PrunedScanIds)}), keeping the most recent " +
        $"{SqliteScanSink.DefaultRetainedScansPerDevice} and the newest complete one.");
}

void RunRetryPass()
{
// --- Post-scan retry pass ---
// Runs while the session is still open - GetObjectInfo and
// DetectKindBySignature need `properties`/`resources` alive. An early version
// ran it after the session had been closed and crashed immediately with "COM
// object that has been separated from its underlying RCW cannot be used": a
// sharp reminder that release order matters.
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
    if (Volatile.Read(ref scanAborted) == 1)
    {
        sink.OnRetryFinished(RetryOutcome.WasSkipped(
            failedObjects.Count,
            $"the device stopped responding, so re-asking it for {failedObjects.Count} object(s) " +
            "would only wait for answers that are not coming."));
        return;
    }

    var toRetry = failedObjects.ToList();
    int originalFailedCount = toRetry.Count;
    sink.OnRetryStarted(originalFailedCount);

    int recoveredFiles = 0;
    int recoveredFolders = 0;
    int stillUnreadable = 0;
    int hiddenSubtrees = 0;

    foreach (var (objectId, path, stage) in toRetry)
    {
        // The retry pass has to report progress and honour the abort flag just
        // like the main walk, and it did neither.
        //
        // It makes device calls per object - a GetValues, sometimes an
        // EnumObjects probe - and a permanently unreadable object can cost
        // around 1.5 seconds. With 1,500-3,000 failures to retry, a perfectly
        // healthy retry pass could easily go 45 seconds without touching
        // lastProgressTicks, at which point the watchdog would declare the
        // device wedged and FailFast a scan that was working fine. The mirror
        // image was just as bad: a REAL wedge here could not be stopped,
        // because the flag was only checked once before the loop.
        Interlocked.Exchange(ref lastProgressTicks, DateTime.UtcNow.Ticks);
        if (Volatile.Read(ref scanAborted) == 1) break;

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
                try
                {
                    if (probe is not null)
                    {
                        uint probeFetched = 0;
                        probe.Next(1, out string _, ref probeFetched);
                        if (probeFetched > 0) hiddenSubtrees++;
                    }
                }
                finally
                {
                    // In a finally because Next() can throw - and on a failing
                    // device it very often does. Released inline, this leaked
                    // an enumerator per probe exactly when probes fail most.
                    if (probe is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(probe);
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Genuinely unreachable both ways - nothing more to learn.
            }
            continue;
        }

        // Its properties were readable this time, so the Properties-stage
        // failure recorded against it during the main walk no longer hides
        // anything. Separate from the container recovery below: reading an
        // object's properties and being able to list it are different
        // questions, and a folder can answer the first and fail the second.
        tally.MarkObjectResolved(objectId);

        string name = retried.Name;

        if (retried.IsContainer)
        {
            if (FolderPolicy.ShouldSkip(parentPath, name, out string skipReason))
            {
                sink.OnFolderSkipped($"{parentPath}/{name}", $"recovered, but skipped - {skipReason}");
                continue;
            }

            // Recovering a folder used to mean only confirming it still exists;
            // its contents were never re-listed, so a failure on a high-up
            // folder could silently zero out an entire branch while the retry
            // cheerfully reported it as "recovered". Walk it properly - safe
            // here because the main walk has fully finished, so no enumerator
            // is still in flight on the stack.
            // walkedContainerIds is consulted here too. Without it a folder
            // that failed at the Enumerate stage was announced twice: once by
            // the main walk, once again by this pass. Files were already
            // deduped; folders were not.
            string recoveredPath = pathIsParent ? $"{parentPath}/{name}" : path;
            if (walkedContainerIds.Add(objectId))
            {
                sink.OnFolder(retried, recoveredPath, recovered: true);
            }
            // PrintTree does not report failure to its caller - it records an
            // error and returns - so whether the re-walk actually worked has to
            // be read back out of the tally. Any NEW listing failure logged
            // against this same object id means the folder still could not be
            // listed; children log against their own ids and are somebody
            // else's problem. The abort flag is checked too, because PrintTree
            // returns immediately and silently when the device has gone.
            //
            // This mattered: the old code counted the folder as recovered
            // regardless, and told the tally so - which erased the fresh
            // failure along with the original one. A scan that provably lost a
            // subtree could then print "every folder was listed" and be stored
            // as status 'complete'.
            int errorsBeforeRewalk = tally.Errors.Count;
            PrintTree(objectId, recoveredPath);

            bool listedThisTime =
                Volatile.Read(ref scanAborted) == 0 &&
                !tally.Errors.Skip(errorsBeforeRewalk).Any(e =>
                    e.ObjectId == objectId &&
                    e.Stage is ScanStage.Enumerate or ScanStage.EnumerateNext);

            if (!listedThisTime)
            {
                stillUnreadable++;
                continue;
            }

            recoveredFolders++;

            // Only Enumerate/EnumerateNext failures inflate SubtreeLosses, so
            // only those are worth un-counting here.
            if (stage is ScanStage.Enumerate or ScanStage.EnumerateNext)
            {
                tally.MarkContainerRecovered(objectId);
            }
        }
        else
        {
            ClassifyAndReportFile(retried, parentPath, recovered: true);
            recoveredFiles++;
        }
    }

    // recoveredFolders is counted after the Android check, which an older
    // version got wrong - but NOT after a check that the re-walk succeeded,
    // because there is none. A folder that failed again still reports as
    // recovered here and in the scan row's recovered_folders. Same root cause
    // as the note above MarkContainerRecovered; one fix closes both.
    //
    // How much of a scan was recovered versus lost is trust-relevant, so it
    // goes to the sinks rather than only to the screen: it belongs on the scan
    // row next to the verdict it qualifies.
    sink.OnRetryFinished(new RetryOutcome(
        Skipped: false,
        SkipReason: null,
        Attempted: originalFailedCount,
        RecoveredFiles: recoveredFiles,
        RecoveredFolders: recoveredFolders,
        StillUnreadable: stillUnreadable,
        HiddenSubtrees: hiddenSubtrees,
        // Counted against the snapshot taken up front: a repeat failure during
        // the pass appends to the live list, so reading its count afterwards
        // would include the objects the pass started with.
        NewFailures: failedObjects.Count - originalFailedCount));
}
}

// The summary used to be printed from here. It now travels on ScanOutcome
// and is rendered by ConsoleScanSink, which is what lets the SQLite sink
// store the same figures instead of a second copy of them being computed.
// Reporting is a sink's job; this file's job is the walk.

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
void PrintTree(string objectId, string parentPath)
{
    if (Volatile.Read(ref scanAborted) == 1) return;

    IEnumPortableDeviceObjectIDs? childIds = null;
    try
    {
        content.EnumObjects(0, objectId, null, out childIds);
    }
    catch (System.Runtime.InteropServices.COMException ex)
    {
        ReportError(new ScanError(objectId, parentPath, ScanStage.Enumerate, ex.HResult, ex.Message));
        failedObjects.Add((objectId, parentPath, ScanStage.Enumerate));
        return;
    }

    try
    {
        uint fetched = 0;
        string childId = "";
        do
        {
            // Checked BEFORE Next(), not after. When a nested PrintTree returns
            // because of the abort flag, this parent loop would otherwise issue
            // one more Next() on the way out - and it does that at every level
            // of the recursion. On a wedged device each of those is a fresh
            // call that can block, which is precisely what the flag exists to
            // prevent; Cancel() has already been spent by then.
            if (Volatile.Read(ref scanAborted) == 1) break;

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
                ReportError(new ScanError(objectId, parentPath, ScanStage.EnumerateNext, ex.HResult, ex.Message));
                failedObjects.Add((objectId, parentPath, ScanStage.EnumerateNext));
                break;
            }
            if (fetched == 0) break;

            if (Volatile.Read(ref scanAborted) == 1) break;

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
            // Already-seen objects are skipped BEFORE the property read, not
            // after. When the retry pass re-walks a folder whose listing failed
            // part-way, every child it already classified comes back round;
            // reading their properties again is a wasted device round trip, and
            // it also re-counted their missing properties and re-registered
            // them as failures, inflating both filePropertyMisses and the
            // "new failures during retry" figure.
            if (classifiedObjectIds.Contains(childId) || walkedContainerIds.Contains(childId)) continue;

            var obj = GetObjectInfo(childId, parentPath);
            if (obj is null) continue; // recorded in failedObjects; the retry pass owns it now


            string name = obj.Name;

            if (obj.IsContainer)
            {
                // Claiming folders in their own set also breaks any cycle the
                // device might report, since a repeated folder is never
                // enumerated twice.
                if (!walkedContainerIds.Add(childId)) continue;

                if (FolderPolicy.ShouldSkip(parentPath, name, out string skipReason))
                {
                    sink.OnFolderSkipped($"{parentPath}/{name}", skipReason);
                    continue;
                }

                sink.OnFolder(obj, $"{parentPath}/{name}", recovered: false);
                PrintTree(childId, $"{parentPath}/{name}");
            }
            else
            {
                ClassifyAndReportFile(obj, parentPath, recovered: false);
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

/// Reads a device-level string property, or null if the driver does not supply it.
///
/// (The Android/data,obb rules this comment used to describe moved to
/// FolderPolicy; what follows belongs to IsCameraMode, further down.)
//
// PTP-mode rationale, for IsCameraMode below.
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
string? ReadDeviceString(_tagpropertykey key)
{
    IPortableDeviceKeyCollection? keys = null;
    IPortableDeviceValues? values = null;
    try
    {
        keys = (IPortableDeviceKeyCollection)new PortableDeviceTypesLib.PortableDeviceKeyCollectionClass();
        keys.Add(ref key);
        properties.GetValues("DEVICE", keys, out values);
        values.GetStringValue(ref key, out string value);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
    catch (System.Runtime.InteropServices.COMException) { return null; }
    catch (InvalidCastException) { return null; }
    finally
    {
        if (values is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(values);
        if (keys is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(keys);
    }
}

bool IsCameraMode()
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
        return deviceType == WPD_DEVICE_TYPE_CAMERA;
    }
    catch (System.Runtime.InteropServices.COMException)
    {
        // Not every driver reports this property. Assuming "not a camera" is
        // the honest default: inventing a warning we cannot support would
        // train the user to ignore the one case where it is real.
        return false;
    }
    finally
    {
        if (deviceValues is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(deviceValues);
        if (deviceKeys is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(deviceKeys);
    }
}

// Shared by PrintTree and RunRetryPass so classification logic (extension
// check -> signature fallback -> count -> print) can't silently drift
// between the two the way it did before this fix (the retry-pass's own copy
// skipped the health-monitoring wrapper and had no de-duplication).
void ClassifyAndReportFile(DeviceObject obj, string parentPath, bool recovered)
{
    // The dedup guard lives HERE, at the one place a file is actually counted,
    // rather than up in the walk. Both callers - the main walk and the retry
    // pass - pass through it, which they did not before: the retry pass counted
    // every object it recovered a second time.
    if (!classifiedObjectIds.Add(obj.ObjectId)) return;

    // Fast path: the extension is recognised, so there is no reason to touch
    // the file's actual bytes. Null means "unrecognised AND not already known
    // to be irrelevant" - the only case worth a device round trip.
    FileKind kind = FileClassifier.ClassifyByName(obj.Name)
        ?? CheckSignatureWithHealthMonitoring(obj.ObjectId);

    // Counted here, at the single point where a file is resolved, rather than
    // inside a sink. The outcome has to be true no matter which sinks are
    // attached - including none at all.
    tally.CountFile(kind);

    sink.OnFile(obj, $"{parentPath}/{obj.Name}", kind, recovered);
}

// Wraps DetectKindBySignature with session-health monitoring: tracks the error
// rate in rolling batches of 20 checks, and if it looks like the session
// itself is broken (not just "these files aren't media"), tries reconnecting
// before giving up and falling back to extension-only classification for the
// rest of the scan.
FileKind CheckSignatureWithHealthMonitoring(string objectId)
{
    if (health.CheckingDisabled)
    {
        health.RecordSkippedCheck();
        return FileKind.Undetermined;
    }

    // Counted here, not at the call site, so it only counts checks that
    // actually touched the device rather than ones short-circuited above.
    signatureChecksActuallyRun++;

    int errorsBefore = signatureCheckErrors;

    // Timed because the breaker budgets time as well as errors, and the cost of
    // one read is a property of the device, not something that can be assumed:
    // measured at ~13 ms on one phone and 278 ms on another.
    var readClock = System.Diagnostics.Stopwatch.StartNew();
    FileKind kind = DetectKindBySignature(objectId);
    readClock.Stop();

    bool identified = kind is FileKind.MediaFile or FileKind.AudioFile or FileKind.Document;
    if (identified) caughtBySignatureOnly++;

    var trip = health.RecordResult(
        errored: signatureCheckErrors > errorsBefore,
        cost: readClock.Elapsed,
        identifiedFile: identified);

    if (trip is not null)
    {
        // GAP: the scan row records THAT the breaker tripped
        // (signature_checking_disabled) but not when, at what rate, or for
        // which of the two reasons. All three say how much of the scan happened
        // with the fallback switched off, which is exactly how trustworthy the
        // result is.
        Console.WriteLine(trip.Cause == BreakerCause.Errors
            ? $"\n[WARNING] Signature checks are failing at {trip.ErrorRate:P0} - the device session's " +
              "stream-reading capability looks broken, not just 'these files aren't media'. Disabling the " +
              "signature fallback for the rest of THIS scan (extension-based detection is unaffected and " +
              "continues normally). For full accuracy including unrecognized-extension files, unplug/replug " +
              "the phone (or restart the 'Portable Device Enumerator Service' / WPDBusEnum) and run again.\n"
            : $"\n[WARNING] Reading file contents has cost {trip.Wasted.TotalSeconds:F0} seconds without " +
              "identifying anything, so it is being stopped for the rest of THIS scan. The reads are " +
              "working - they are simply not finding media on this device, and on slow hardware they can " +
              "cost hundreds of milliseconds each. Extension-based detection is unaffected and continues " +
              "normally; files with an unrecognized extension are now reported as [UNCHECKED] rather than " +
              "guessed at.\n");
    }

    return kind;
}

// Takes the bytes of the files a scan found and writes them into a folder.
//
// Sequential and single threaded, which is a measured decision rather than a
// simplification. A real mix of files read at 29.9 MB/s against 31.99 MB/s for
// one large file, so per-file overhead was costing about six percent and
// parallel streams had almost nothing to win. What the time actually goes on is
// opening streams - 10.1 ms each on this phone, 278 ms on another - and that is
// not something more threads through one USB endpoint would fix.
//
// Every file is written under a .part name and renamed only once its bytes are
// on disk and verified. A rename within a volume is atomic, so a file under its
// real name is a file that arrived whole: there is no moment where a half
// written photo wears the name of a finished one.
/// <param name="sources">
/// Comma separated MediaSource ids ("camera", "app:com.whatsapp"), or "all".
/// The ids rather than the labels, because a label is Turkish text that a
/// vendor's folder naming can change underneath it, and a command line argument
/// that shifts with the device is one nothing can be scripted against.
/// </param>
/// <param name="everyKind">
/// Take everything the walk saw, not just what it called media or a document.
///
/// The default is the narrower set because a transfer is usually about
/// photographs. But "media" here means pictures and video: audio was
/// deliberately excluded so that an .m4a would not be mistaken for a video by
/// its container bytes, and the effect is that voice recordings, WhatsApp voice
/// notes, the contacts .vcf and WhatsApp's own encrypted message database all
/// land in "Unknown" and are left on the phone. On a phone that is about to be
/// given away, that is the wrong default to be stuck with - measured on the test
/// device, it was 162 files including 62 voice notes and the chat history.
/// </param>
void RunCopy(string destinationRoot, long requestedScanId, string sources, bool everyKind)
{
    destinationRoot = Path.GetFullPath(destinationRoot);



    long scanId = requestedScanId;
    string? scanDeviceKey = null;
    var items = new List<TransferItem>();
    var leftBehind = new List<(string Kind, int Count, long Bytes)>();

    using (var db = new Microsoft.Data.Sqlite.SqliteConnection(
        $"Data Source={SqliteScanSink.DefaultDatabasePath};Mode=ReadOnly;Pooling=False"))
    {
        db.Open();

        if (scanId <= 0)
        {
            using var pick = db.CreateCommand();
            // Complete only. A partial scan is one that does not know what it
            // missed, and copying from it would report a finished transfer of an
            // unknown fraction of the phone.
            pick.CommandText = "SELECT MAX(scan_id) FROM scan WHERE status = 'complete';";
            scanId = pick.ExecuteScalar() is long id ? id : 0;
        }
        if (scanId <= 0)
        {
            Console.WriteLine("No completed scan to copy from. Run a scan first.");
            return;
        }

        using (var owner = db.CreateCommand())
        {
            owner.CommandText = "SELECT device_key FROM scan WHERE scan_id = $s;";
            owner.Parameters.AddWithValue("$s", scanId);
            scanDeviceKey = owner.ExecuteScalar() as string;
        }

        using var q = db.CreateCommand();
        // The same two kinds the setup page counted. App data stays where it is:
        // it belongs to the app that wrote it and means nothing on a PC.
        q.CommandText = everyKind
            ? """
              SELECT object_id, path, name, size, modified_raw FROM file WHERE scan_id = $s;
              """
            : """
              SELECT object_id, path, name, size, modified_raw
              FROM file WHERE scan_id = $s AND kind IN ('MediaFile', 'AudioFile', 'Document');
              """;
        q.Parameters.AddWithValue("$s", scanId);
        using (var r = q.ExecuteReader())
        {
            while (r.Read())
            {
                items.Add(new TransferItem(r.GetString(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? 0 : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4)));
            }
        }

        // What the scan saw and this transfer will NOT carry.
        //
        // Read on purpose, and reported at the end even when the run is a clean
        // success, because the alternative has already happened: a transfer said
        // "13,630 files, complete, every byte verified" and was telling the
        // truth about everything it had SELECTED. 162 files sat outside that
        // selection - 107 recordings among them - and no screen in this program
        // mentioned their existence. The phone was wiped three hours later.
        //
        // A total is only honest next to what it excludes.
        if (!everyKind)
        {
            using var rest = db.CreateCommand();
            rest.CommandText = """
                SELECT kind, COUNT(*), COALESCE(SUM(size), 0) FROM file
                WHERE scan_id = $s AND kind NOT IN ('MediaFile', 'AudioFile', 'Document')
                GROUP BY kind ORDER BY COUNT(*) DESC;
                """;
            rest.Parameters.AddWithValue("$s", scanId);
            using var rr = rest.ExecuteReader();
            while (rr.Read())
            {
                leftBehind.Add((rr.GetString(0), rr.GetInt32(1), rr.GetInt64(2)));
            }
        }
    }

    // Object ids are this device's own handles and mean nothing on another
    // phone. Without this check, plugging in the wrong device and running a
    // transfer would fail on every file - or, if the ids happened to resolve,
    // quietly write one phone's photos into a folder named after another.
    if (scanDeviceKey is null)
    {
        Console.WriteLine($"Scan {scanId} is not in the database.");
        return;
    }
    if (!string.Equals(scanDeviceKey, identity.DeviceKey, StringComparison.Ordinal))
    {
        Console.WriteLine($"Scan {scanId} belongs to a different device ({scanDeviceKey}); the one " +
            $"plugged in is {identity.DeviceKey}. Scan this phone before copying from it.");
        return;
    }
    if (items.Count == 0)
    {
        Console.WriteLine($"Scan {scanId} found no media or documents to copy.");
        return;
    }

    var plan = TransferPlan.Build(items);
    using var ledger = new TransferLedger(SqliteScanSink.DefaultDatabasePath);

    // Read in one query rather than one per file. Thirteen thousand round trips
    // to ask about an indexed column is work for nothing, and it happens before
    // the first byte moves, where the user is watching a blank screen.
    var remembered = ledger.AllFor(identity.DeviceKey);

    // Decided in full before anything is written, for two reasons: the totals
    // printed below have to be the real ones rather than the whole plan's, and a
    // destination that cannot hold the transfer should be found out now rather
    // than forty minutes in. Running out of disk half way is this project's own
    // failure mode wearing a different hat.
    // ForSources rather than a filter written here: the setup page measures
    // free space against a selection and this transfers one, so the two have to
    // agree about what a selection means down to the last file.
    var selected = TransferPlan.ForSources(plan.Copies, sources);

    // Every destination this run has already promised to SOME file. The naming
    // helper needs it: it hands out the next name nothing has taken, and until
    // this existed "nothing has taken it" was asked only of the disk - where
    // none of the plan's files have been written yet.
    var plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var promised in selected)
    {
        plannedTargets.Add(Path.Combine(destinationRoot,
            promised.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    // Where finished copies are, according to the ledger. Used to make sure the
    // staging name never lands on one of them.
    var recordedDestinations = new HashSet<string>(
        remembered.Values.Where(r => r.Status == "done").Select(r => r.Destination),
        StringComparer.OrdinalIgnoreCase);

    var work = new List<(PlannedCopy Planned, string Target)>();
    var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    long plannedBytes = 0;
    int alreadyThere = 0;

    foreach (var planned in selected)
    {
        string target = Path.Combine(destinationRoot,
            planned.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        remembered.TryGetValue(planned.Item.DevicePath, out CopyRecord? previous);

        // The file the RECORD is about, which is not always the one the plan
        // would choose today. A version kept alongside an older one lives under
        // a numbered name while the plan, being deterministic, goes on pointing
        // at the bare one. Measuring the plan's choice against a record about a
        // different file answers a question nobody asked: it says "already
        // there" for a photograph that was never copied, and "wrong length" for
        // one that is perfectly fine - and that second answer leads straight to
        // the delete below.
        // ...but only when that record points INSIDE the folder being written
        // to now. The question is "is this file already HERE", and a record
        // about a copy on some other drive cannot answer it. Getting that wrong
        // is not theoretical: a backup to this machine skipped 2,044 files
        // because an earlier run had put them on an external disk - which by
        // then had dropped off the bus and lost them - and reported "already
        // there" for every one.
        string measured = previous is { Status: "done" } && IsUnder(previous.Destination, destinationRoot)
            ? previous.Destination
            : target;
        bool measuredExists = File.Exists(measured);

        var decision = CopyDecision.Decide(previous, planned.Item.Size, planned.Item.ModifiedRaw,
            measuredExists, measuredExists ? new FileInfo(measured).Length : 0);

        if (decision == CopyAction.Skip)
        {
            alreadyThere++;
            continue;
        }

        // Something is already sitting where this file would go. It is safe to
        // replace ONLY if the ledger says it is this same source's own earlier
        // copy; anything else is a file this program cannot account for - the
        // version it deliberately kept last run, or another phone's photograph
        // backed up into the same folder - and the copy below deletes the
        // target before renaming. Copying twice costs seconds. Deleting the
        // only remaining version of a photograph costs the photograph.
        // Deliberately not restricted to 'done' rows. A run that died between
        // the rename and the ledger update leaves the finished file under its
        // real name with the row still saying 'copying' - and that file is
        // still this source's own. Demanding 'done' here would make the next
        // run treat it as a stranger and lay a duplicate down beside it.
        bool targetIsOurOwn = previous is not null
            && string.Equals(previous.Destination, target, StringComparison.OrdinalIgnoreCase);

        // Every file is staged as "<target>.part" and whatever sits under that
        // name is deleted first. TransferPlan promises no two files share a
        // destination; it promises nothing about destination + ".part", which is
        // the namespace actually written into. A phone holding both "photo.jpg"
        // and "photo.jpg.part" - a browser's half-finished download, which the
        // classifier keeps because its first bytes are a real JPEG - would have
        // one finished copy standing on the other's scratch name, and the second
        // file copied would delete the first. The same goes for a finished copy
        // an EARLIER run left there, which the ledger still vouches for.
        string wouldStageOver = target + ".part";
        bool stagingHitsRealFile = plannedTargets.Contains(wouldStageOver)
            || recordedDestinations.Contains(wouldStageOver);

        if (decision == CopyAction.CopyAsNewVersion || claimed.Contains(target)
            || stagingHitsRealFile
            || (File.Exists(target) && !targetIsOurOwn))
        {
            // "Taken" has to include the rest of the PLAN, not just the disk and
            // this pass's own claims. Every later file's destination is already
            // decided and none of them is written yet, so asking only the disk
            // hands this file a name the plan has promised to another one - and
            // the later file then deletes this one to take its place.
            target = TransferPlan.NextFreeName(target,
                candidate => File.Exists(candidate)
                    || claimed.Contains(candidate)
                    || plannedTargets.Contains(candidate));
        }

        claimed.Add(target);
        work.Add((planned, target));
        plannedBytes += Math.Max(0, planned.Item.Size);
    }

    Console.WriteLine($"Scan {scanId} -> {destinationRoot}");
    if (everyKind) Console.WriteLine("Every kind, including audio, archives and app data.");
    if (sources is not ("all" or "")) Console.WriteLine($"Only: {sources}");
    Console.WriteLine($"{work.Count} file(s) to copy, {plannedBytes / 1024 / 1024} MB. " +
        $"{alreadyThere} already there.");
    ReportLeftBehind();

    if (work.Count == 0)
    {
        Console.WriteLine("Nothing to do.");
        return;
    }

    // A margin, not an exact fit: a filesystem needs room for its own
    // bookkeeping, and a transfer that ends by filling the disk leaves the
    // machine worse off than one that refuses to start.
    try
    {
        var drive = new DriveInfo(Path.GetPathRoot(destinationRoot)!);
        long needed = plannedBytes + Math.Max(256L * 1024 * 1024, plannedBytes / 50);
        if (!drive.IsReady)
        {
            Console.WriteLine($"{drive.Name} is not ready.");
            return;
        }
        if (drive.AvailableFreeSpace < needed)
        {
            Console.WriteLine($"Not enough room: {needed / 1024 / 1024} MB needed, " +
                $"{drive.AvailableFreeSpace / 1024 / 1024} MB free on {drive.Name}.");
            return;
        }
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
    {
        Console.WriteLine($"Could not read {destinationRoot}: {ex.GetType().Name}: {ex.Message}");
        return;
    }

    Directory.CreateDirectory(destinationRoot);

    long copiedBytes = 0;
    int copied = 0, failed = 0;
    // Counts failures with no success in between. The scanner has a proper
    // health monitor for the same shape of problem; the copier needs only the
    // blunt version, because the failure that matters here is durable rather
    // than flaky: once the destination is full or the drive is unplugged, every
    // remaining file fails for the same reason. Grinding through thirteen
    // thousand of them costs a device round trip and two committed rows each,
    // prints nothing (failures stop printing after ten and the progress line
    // needs a success to advance), and looks exactly like a freeze.
    int failuresInARow = 0;
    string? breakerReason = null;

    // Counts the one failure that has a specific cure. WPD object handles are
    // the scan's way of asking for a file's bytes, and unplugging the phone ends
    // the session that issued them - measured here, not assumed: a transfer run
    // straight after a replug failed on every file with this code. An earlier
    // measurement in this project reported the handles as surviving "a later
    // session", but what it actually tested was a new PROCESS against a phone
    // that had stayed plugged in. That is a narrower claim than the one drawn
    // from it.
    int staleHandles = 0;
    long lastProgress = DateTime.UtcNow.Ticks;
    int copyAborted = 0;
    // Raised when the copy loop is provably out of the device's hands, so the
    // watchdog can tell "stopped, tidying up" apart from "still wedged".
    int copyLoopLeft = 0;

    // The session must be closed on the way out however this ends. Skipping it
    // leaves the device locked after the process exits, and then every stream
    // the NEXT run opens fails with "the device is unreachable" - measured, not
    // feared: 1606 of 1607 signature checks failed that way once.
    AppDomain.CurrentDomain.ProcessExit += (_, _) => CloseSession();

    // Ctrl+C raises the flag rather than closing anything. The handler runs on a
    // thread-pool thread while this one is very likely inside a COM call, so
    // releasing the objects here would be a use-after-release against the code
    // still using them. Cancelling the termination lets the loop stop at the
    // next file and close the session from a thread that is idle.
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Volatile.Write(ref copyAborted, 1);
        Console.WriteLine("\n[CANCELLED] Stopping after the current file. Nothing is lost; " +
            "run again to carry on.");
        try { device.Cancel(); } catch (Exception) { }
    };

    // The same guard the walk has, for the same reason. A wedged device blocks
    // inside a COM call with no error and no timeout of its own, and a transfer
    // sits inside that risk for the better part of an hour rather than for
    // seconds. Sixty seconds without a single byte is not a slow file: the
    // slowest real read measured was under two.
    var copyWatchdog = new Thread(() =>
    {
        while (Volatile.Read(ref copyAborted) == 0)
        {
            Thread.Sleep(2000);
            if (Volatile.Read(ref copyAborted) == 1) return;

            var idle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Volatile.Read(ref lastProgress));
            if (idle <= TimeSpan.FromSeconds(60)) continue;

            Console.WriteLine($"\n[WARNING] No bytes for {idle.TotalSeconds:F0} seconds. The device has " +
                "stopped answering mid-copy. Stopping, so the ledger reports what was actually taken; " +
                "unplug and replug the phone, then run again to carry on.");
            // Flushed because this project has watched a warning sit invisible
            // in a redirected stdout buffer for 22 minutes while the user stared
            // at a window that had simply stopped.
            Console.Out.Flush();

            Volatile.Write(ref copyAborted, 1);
            try { device.Cancel(); } catch (Exception) { }

            // ESCALATION, and the reason it is not optional: raising a flag only
            // helps if something is still running to read it. Both places that
            // read copyAborted are in the copy loop, and a thread parked inside
            // a COM call reaches neither. Cancel() being ignored is not a fear
            // either - it was measured on this project's own hardware, where the
            // scan watchdog fired, called Cancel(), and the walk carried on for
            // another 4,838 objects. The scan path escalates for exactly this
            // reason; the copy path promised "the ledger reports what was
            // actually taken" and then had no way to keep that promise.
            for (int waited = 0; waited < 20; waited++)
            {
                Thread.Sleep(1000);
                if (Volatile.Read(ref copyLoopLeft) == 1) return;
            }

            Console.WriteLine("[FATAL] The transfer is still stuck 20 seconds after being told to stop, " +
                "which means the device is wedged inside a call that will not return. Ending the process " +
                "so what WAS copied is reported rather than waited on.");
            Console.WriteLine("Every finished file is already on disk and recorded. Unplug and replug the " +
                "phone, then run the same command again to carry on.");
            Console.Out.Flush();
            Environment.FailFast("WPD copy thread unrecoverable: wedged inside a COM call, Cancel() ignored.");
        }
    }) { IsBackground = true, Name = "wpd-copy-watchdog" };
    copyWatchdog.Start();

    var wall = System.Diagnostics.Stopwatch.StartNew();

    foreach (var (planned, target) in work)
    {
        if (Volatile.Read(ref copyAborted) == 1) break;

        var item = planned.Item;
        string part = target + ".part";

        // Written before the stream is opened, so that a pulled cable leaves a
        // 'copying' row behind rather than silence. That row and the .part file
        // are what make an interrupted transfer a recoverable one.
        //
        // Outside the per-file try on purpose, so it needs its own: a copy with
        // no row is a file taken off the phone that nothing recorded, which is
        // the one thing this program exists to prevent. If the ledger cannot be
        // written, the run stops here rather than carrying on unrecorded.
        long copyId;
        try
        {
            copyId = ledger.Begin(identity.DeviceKey, item, target);
        }
        catch (Exception ex)
        {
            breakerReason = $"Defter yazılamadı, aktarım kayıt tutmadan sürdürülemez: {ex.Message}";
            Volatile.Write(ref copyAborted, 1);
            break;
        }

        IStream? wpdStream = null;
        IntPtr readPtr = IntPtr.Zero;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // A .part left by an earlier run is a prefix of a file, and a prefix
            // of a photograph is not a photograph. Appending to it would produce
            // something corrupt that passes a length check.
            if (File.Exists(part)) File.Delete(part);

            uint optimal = 0;
            resources.GetStream(item.ObjectId, ref resourceDefaultKey, 0 /* STGM_READ */, ref optimal, out wpdStream);
            var stream = (System.Runtime.InteropServices.ComTypes.IStream)wpdStream;

            // The driver's own figure, within reason. Reading in the size it
            // asks for is what the measurement ran at, so it is preferred to a
            // round number of our choosing - but it is an unsigned value from a
            // device, and nothing here has ever checked it. Anything at or above
            // 2 GB casts to a negative int and throws on `new byte[]`, and a
            // driver answering 1 would read the phone a byte at a time. Neither
            // has been seen; neither costs anything to rule out.
            int buffer = optimal is >= 4096 and <= 8 * 1024 * 1024 ? (int)optimal : 262144;
            byte[] chunk = new byte[buffer];
            readPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(int));

            long written = 0;
            string sourceHash;
            using (var hasher = System.Security.Cryptography.SHA256.Create())
            using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, buffer))
            {
                while (true)
                {
                    if (Volatile.Read(ref copyAborted) == 1)
                    {
                        throw new OperationCanceledException("Stopped part way through this file.");
                    }

                    // Same discipline as the signature check: zero the slot
                    // first, then clamp what comes back. AllocHGlobal hands back
                    // uninitialised memory and this slot is reused every
                    // iteration, so a driver that reported success without
                    // writing the count would have the file built out of
                    // whatever happened to be there before.
                    System.Runtime.InteropServices.Marshal.WriteInt32(readPtr, 0);
                    stream.Read(chunk, chunk.Length, readPtr);
                    int got = System.Runtime.InteropServices.Marshal.ReadInt32(readPtr);
                    if (got <= 0) break;
                    if (got > chunk.Length) got = chunk.Length;

                    file.Write(chunk, 0, got);
                    hasher.TransformBlock(chunk, 0, got, null, 0);
                    written += got;
                    Volatile.Write(ref lastProgress, DateTime.UtcNow.Ticks);
                }
                hasher.TransformFinalBlock([], 0, 0);
                sourceHash = Convert.ToHexString(hasher.Hash!);
            }

            // What the device promised against what arrived. IStream::Read may
            // legally return fewer bytes than asked for and still report
            // success, which is how a short read once turned a real photo into a
            // file that matched no signature - the same trap one layer down,
            // where the result would be a truncated photograph instead.
            if (item.Size > 0 && written != item.Size)
            {
                throw new IOException($"{item.Size} bytes expected, {written} arrived.");
            }

            // Read back from the disk. Hashing while writing proves what was
            // sent; hashing what landed proves what was stored, and only the
            // second catches a truncated write or a drive that lied about a
            // flush. It costs a local read - minutes against the hour the whole
            // transfer takes - and it is the difference between saying a file
            // was copied and knowing it.
            string onDisk = HashFile(part,
                () => Volatile.Write(ref lastProgress, DateTime.UtcNow.Ticks));
            if (!string.Equals(onDisk, sourceHash, StringComparison.Ordinal))
            {
                throw new IOException("The file read back from disk is not the one that was written.");
            }

            // Only now does it get the real name.
            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);

            ledger.Complete(copyId, written, sourceHash);
            copiedBytes += written;
            copied++;
            failuresInARow = 0;

            if (copied % 50 == 0)
            {
                double mbPerSecond = copiedBytes / 1024.0 / 1024.0 / Math.Max(0.001, wall.Elapsed.TotalSeconds);
                Console.WriteLine($"  {copied}/{work.Count}  {copiedBytes / 1024 / 1024} MB  " +
                    $"{mbPerSecond:F1} MB/s  {failed} failed");
            }
        }
        catch (OperationCanceledException)
        {
            // Not a failure: nothing is wrong with this file, the run was
            // stopped part way through it. The row stays 'copying', which is
            // what the ledger says an interrupted copy looks like, and the next
            // run treats it as unfinished and does it again. Calling Fail here
            // would blame the device for the user's Ctrl+C, permanently.
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { }
        }
        catch (Exception ex)
        {
            // Recorded, not forgotten. A file that could not be taken is still
            // on the phone, and a transfer that quietly drops its failures
            // reports success while leaving things behind.
            failed++;
            failuresInARow++;

            // 0x80042009 is WPD's "invalid object handle": the id does not refer
            // to any object on the device. Three in a row is not three deleted
            // photographs, it is a scan that no longer describes this session -
            // and the answer is a rescan, not a retry. Saying so after three
            // rather than after twenty saves the user nineteen pointless waits
            // and a reason that explains nothing.
            if (ex is System.Runtime.InteropServices.COMException com
                && unchecked((uint)com.HResult) == 0x80042009)
            {
                staleHandles++;
            }
            else
            {
                staleHandles = 0;
            }
            try
            {
                ledger.Fail(copyId, $"{ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ledgerError)
            {
                // The ledger is the whole point; if it cannot be written the run
                // has to stop, and it has to stop SAYING so rather than by
                // unwinding out of the loop and skipping the summary.
                breakerReason = "Defter yazılamadı: " + ledgerError.Message;
                Volatile.Write(ref copyAborted, 1);
            }
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { }
            if (failed <= 10) Console.WriteLine($"  [FAIL] {item.Name}: {ex.Message}");

            if (breakerReason is null && staleHandles >= 3)
            {
                breakerReason = "Bu tarama artık bu bağlantıyı tanımıyor. Telefon çıkarılıp " +
                    "takıldığında dosya kimlikleri geçersiz oluyor; ÖNCE YENİDEN TARAYIN, sonra " +
                    "aktarımı tekrar başlatın. Kopyalanmış dosyalar korunur.";
                Volatile.Write(ref copyAborted, 1);
            }

            if (breakerReason is null && failuresInARow >= 20)
            {
                breakerReason = $"Üst üste {failuresInARow} dosya alınamadı. Son hata: " +
                    $"{ex.GetType().Name}: {ex.Message}";
                Volatile.Write(ref copyAborted, 1);
            }
        }
        finally
        {
            if (readPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(readPtr);
            if (wpdStream is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(wpdStream);
        }
    }

    bool stopped = Volatile.Read(ref copyAborted) == 1;
    // Before raising copyAborted, so the watchdog can tell an orderly stop from
    // a device that is still holding the thread hostage.
    Volatile.Write(ref copyLoopLeft, 1);
    Volatile.Write(ref copyAborted, 1);
    wall.Stop();

    int notReached = work.Count - copied - failed;
    var counts = ledger.Counts(identity.DeviceKey);

    Console.WriteLine();
    Console.WriteLine(stopped ? "STOPPED EARLY." : "DONE.");
    if (breakerReason is not null) Console.WriteLine($"  sebep        : {breakerReason}");
    Console.WriteLine($"  copied        : {copied} file(s), {copiedBytes / 1024 / 1024} MB " +
        $"in {wall.Elapsed.TotalMinutes:F1} min");
    Console.WriteLine($"  already there : {alreadyThere}");
    Console.WriteLine($"  failed        : {failed}");
    if (notReached > 0) Console.WriteLine($"  not reached   : {notReached}");
    Console.WriteLine($"  ledger        : {counts.Done} done, {counts.Failed} failed, " +
        $"{counts.Unfinished} unfinished for this device");

    ReportLeftBehind();

    if (stopped || failed > 0 || notReached > 0)
    {
        Console.WriteLine("\nRun again to carry on. Files that finished are not copied twice.");
    }

    // Printed twice on purpose: before the transfer, where it can still change
    // the user's mind, and after it, next to the total - which is the place a
    // number gets believed.
    void ReportLeftBehind()
    {
        int count = leftBehind.Sum(x => x.Count);
        if (count == 0) return;

        long bytes = leftBehind.Sum(x => x.Bytes);
        Console.WriteLine($"NOT taken: {count} file(s), {bytes / 1024 / 1024} MB - the scan saw them " +
            "and this transfer does not carry them.");
        foreach (var (kind, n, b) in leftBehind)
        {
            string meaning = kind switch
            {
                "Unknown" => "app data, archives, databases - examined and not identified as media",
                "Undetermined" => "COULD NOT BE CHECKED - any of these may be a photograph",
                _ => kind,
            };
            Console.WriteLine($"    {n,6}  {b / 1024 / 1024,6} MB  {meaning}");
        }
        Console.WriteLine("    Add \"all\" as the last argument to take these too.");
    }
}

// Whether a recorded destination belongs to the folder being written to now.
// Compared as full paths with a trailing separator, so "D:/A56 yedek" and
// "D:/A56 yedek 2" cannot be mistaken for one another.
bool IsUnder(string path, string root)
{
    string full = Path.GetFullPath(root);
    if (!full.EndsWith(Path.DirectorySeparatorChar)) full += Path.DirectorySeparatorChar;
    return Path.GetFullPath(path).StartsWith(full, StringComparison.OrdinalIgnoreCase);
}

// Streamed rather than File.ReadAllBytes: some of these are video files - the
// largest on the test phone is 2.1 GB - and the point of the check is not to
// need the whole file in memory to make it.
//
// onProgress is not decoration. This re-read can take minutes on a slow
// destination, and the watchdog measures silence: 60 seconds with no sign of
// life and it declares the phone dead and stops the transfer. A 2.1 GB file
// over USB 2 at ~30 MB/s takes 70 seconds to hash, so without this the copier
// aborts itself on its own largest files and tells the user to replug a phone
// that was never the problem.
string HashFile(string path, Action? onProgress = null)
{
    using var sha = System.Security.Cryptography.SHA256.Create();
    using var file = File.OpenRead(path);

    byte[] buffer = new byte[1 << 20];
    int got;
    while ((got = file.Read(buffer, 0, buffer.Length)) > 0)
    {
        sha.TransformBlock(buffer, 0, got, null, 0);
        onProgress?.Invoke();
    }
    sha.TransformFinalBlock([], 0, 0);
    return Convert.ToHexString(sha.Hash!);
}

// Copies a spread of real files and reports what it cost. Reads them exactly
// the way the copier will, so the numbers carry over.
void MeasureCopy(int wanted, string destination)
{
    Directory.CreateDirectory(destination);
    Console.WriteLine($"Measuring {wanted} file copies into {destination}\n");

    var picked = new List<(string ObjectId, string Name, long Size)>();
    using (var db = new Microsoft.Data.Sqlite.SqliteConnection(
        $"Data Source={SqliteScanSink.DefaultDatabasePath};Mode=ReadOnly;Pooling=False"))
    {
        db.Open();
        using var q = db.CreateCommand();
        // Spread across the whole scan rather than the first N rows, which
        // would all come from one folder and measure one corner of the device.
        // Large files are excluded: this measures per-file cost, and a single
        // 2 GB video would drown thirty-nine others.
        q.CommandText = """
            SELECT object_id, name, size FROM file
            WHERE scan_id = (SELECT MAX(scan_id) FROM scan WHERE status = 'complete')
              AND kind = 'MediaFile' AND size > 0 AND size < 104857600
            ORDER BY file_id;
            """;

        var all = new List<(string, string, long)>();
        using var r = q.ExecuteReader();
        while (r.Read()) all.Add((r.GetString(0), r.GetString(1), r.GetInt64(2)));

        if (all.Count == 0)
        {
            Console.WriteLine("No completed scan to measure against. Run a scan first.");
            return;
        }
        int step = Math.Max(1, all.Count / wanted);
        for (int i = 0; i < all.Count && picked.Count < wanted; i += step) picked.Add(all[i]);
    }

    long totalBytes = 0, openTicks = 0, readTicks = 0;
    int failed = 0, copied = 0;
    var wall = System.Diagnostics.Stopwatch.StartNew();
    var openClock = new System.Diagnostics.Stopwatch();

    foreach (var (objectId, name, size) in picked)
    {
        IStream? wpdStream = null;
        IntPtr readPtr = IntPtr.Zero;
        string target = Path.Combine(destination, TransferPlan.SafeFileName(name));
        try
        {
            uint optimal = 0;
            openClock.Restart();
            resources.GetStream(objectId, ref resourceDefaultKey, 0 /* STGM_READ */, ref optimal, out wpdStream);
            openClock.Stop();
            openTicks += openClock.ElapsedTicks;

            var stream = (System.Runtime.InteropServices.ComTypes.IStream)wpdStream;
            int buffer = optimal > 0 ? (int)optimal : 262144;
            byte[] chunk = new byte[buffer];
            readPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(int));

            var readClock = System.Diagnostics.Stopwatch.StartNew();
            using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, buffer))
            {
                while (true)
                {
                    // Same discipline as the signature check: zero the slot
                    // first and clamp what comes back, because a driver that
                    // reports success without writing the count would leave
                    // whatever happened to be in that memory.
                    System.Runtime.InteropServices.Marshal.WriteInt32(readPtr, 0);
                    stream.Read(chunk, chunk.Length, readPtr);
                    int got = System.Runtime.InteropServices.Marshal.ReadInt32(readPtr);
                    if (got <= 0) break;
                    if (got > chunk.Length) got = chunk.Length;
                    file.Write(chunk, 0, got);
                    totalBytes += got;
                }
            }
            readClock.Stop();
            readTicks += readClock.ElapsedTicks;
            copied++;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            failed++;
            if (failed <= 3) Console.WriteLine($"  [FAIL] 0x{ex.HResult:X8}  {name}");
        }
        finally
        {
            if (readPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(readPtr);
            if (wpdStream is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(wpdStream);
            // Deleted as we go: the measurement must not need the space the
            // real transfer will, and this machine is short of it.
            try { File.Delete(target); } catch (IOException) { }
        }
    }
    wall.Stop();

    double ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;
    double openMs = openTicks / ticksPerMs;
    double readMs = readTicks / ticksPerMs;
    double mb = totalBytes / 1024.0 / 1024.0;
    double perFileOpen = openMs / Math.Max(1, picked.Count);
    double mbPerSecond = mb / Math.Max(0.001, readMs / 1000.0);

    Console.WriteLine($"\nTried {picked.Count} file(s): {copied} copied, {failed} failed.");
    Console.WriteLine($"Stored object ids valid : {(failed == 0 ? "all of them" : $"{copied} of {picked.Count}")}");
    Console.WriteLine($"Opening streams         : {openMs:F0} ms total, {perFileOpen:F1} ms per file");
    Console.WriteLine($"Reading bytes           : {readMs:F0} ms total, {mb:F1} MB, {mbPerSecond:F1} MB/s");
    Console.WriteLine($"Wall clock              : {wall.Elapsed.TotalSeconds:F1} s");

    double projOpenMin = perFileOpen * 13630 / 1000 / 60;
    double projReadMin = (22.14 * 1024 / mbPerSecond) / 60;
    Console.WriteLine($"\nProjected for 13,630 files and 22.14 GB:");
    Console.WriteLine($"  opening  ~{projOpenMin:F1} min");
    Console.WriteLine($"  reading  ~{projReadMin:F1} min");
    Console.WriteLine($"  total    ~{projOpenMin + projReadMin:F1} min");
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
        resources.GetStream(objectId, ref resourceDefaultKey, 0 /* STGM_READ */, ref optimalTransferSize, out wpdStream);

        var stream = (System.Runtime.InteropServices.ComTypes.IStream)wpdStream;

        // IStream::Read may legally return FEWER bytes than asked for and still
        // report success. Passing IntPtr.Zero (discarding the count) made a
        // short read indistinguishable from a full one, and since the untouched
        // tail stays zero it matched no signature - so a real photo over a weak
        // cable became a silent "not media". Now we read until we have all 12
        // bytes or the stream genuinely ends.
        byte[] header = new byte[FileClassifier.SignatureBytes];
        bytesReadPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(int));
        int filled = 0;
        while (filled < header.Length)
        {
            byte[] chunk = new byte[header.Length - filled];
            // AllocHGlobal hands back UNINITIALISED memory and this slot is
            // reused every iteration, so a driver that returns success without
            // writing the count would leave whatever was there before. Zero it
            // first, then clamp: a garbage value larger than the buffer made
            // Buffer.BlockCopy throw, and a garbage small one silently
            // corrupted the header we then classified on.
            System.Runtime.InteropServices.Marshal.WriteInt32(bytesReadPtr, 0);
            stream.Read(chunk, chunk.Length, bytesReadPtr);

            int got = System.Runtime.InteropServices.Marshal.ReadInt32(bytesReadPtr);
            if (got <= 0) break; // end of stream - the file is simply shorter than 12 bytes
            if (got > chunk.Length) got = chunk.Length;
            Buffer.BlockCopy(chunk, 0, header, filled, got);
            filled += got;
        }

        // Too short to identify. Not an error, and deliberately not counted as
        // one: a 4-byte file is a real answer, not a device failure.
        if (filled < header.Length) return FileKind.Unknown;

        return FileClassifier.ClassifyBySignature(header);
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
        return FileKind.Undetermined;
    }
    catch (Exception ex)
    {
        // Narrowing the catch to COMException left everything else - a failed
        // cast, an RCW that has been severed, an allocation failure - with no
        // handler anywhere between here and Task.Run, so one bad object ended
        // the entire walk. Report it and carry on; the file is simply
        // unclassified, which is now a state we can express.
        Console.WriteLine($"[WARNING] Signature check failed unexpectedly on one object: " +
            $"{ex.GetType().Name}: {ex.Message}. Continuing.");
        return FileKind.Undetermined;
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
// the file count, and since "(unreadable)" has no extension it fell all the way
// through to the signature check - opening a content stream on an object whose
// property read had just failed. Those guaranteed failures then fed the health
// monitor, which concluded the session was broken and switched off signature
// checking for the rest of the scan. A self-inflicted wound that silently cost
// real photos: every unrecognized-extension file after that point went
// unclassified. The placeholder was also a real filename a device could return.
// A property the device chose not to supply comes back as a COMException from
// the individual getter, not from GetValues itself. Absence is data, not an
// error, so it is never reported through sink.OnError.
// `expected` says whether a missing value is worth counting. A container has
// no filename, size or modified date to give, so its misses are normal and are
// not counted; only a FILE lacking a property the device otherwise supplies
// 100% of the time is worth surfacing.
string? TryReadString(IPortableDeviceValues values, ref _tagpropertykey key, bool expected)
{
    try
    {
        values.GetStringValue(ref key, out string value);
        return string.IsNullOrEmpty(value) ? null : value;
    }
    catch (System.Runtime.InteropServices.COMException) { if (expected) filePropertyMisses++; return null; }
    catch (InvalidCastException) { if (expected) filePropertyMisses++; return null; } // stored as another VARTYPE
}

ulong? TryReadSize(IPortableDeviceValues values, ref _tagpropertykey key, bool expected)
{
    try
    {
        values.GetUnsignedLargeIntegerValue(ref key, out ulong value);
        return value;
    }
    catch (System.Runtime.InteropServices.COMException) { if (expected) filePropertyMisses++; return null; }
    catch (InvalidCastException) { if (expected) filePropertyMisses++; return null; }
}

DeviceObject? GetObjectInfo(string objectId, string parentPath)
{
    IPortableDeviceValues? values = null;
    try
    {
        properties.GetValues(objectId, wantedProperties, out values);

        // Only these two are required. GetValues reports per-property failures
        // inside the returned collection rather than failing the whole call, so
        // each optional field is read separately and allowed to be absent -
        // which is exactly what we want to measure.
        values.GetStringValue(ref nameKey, out string displayName);
        values.GetGuidValue(ref contentTypeKey, out Guid contentType);
        bool isContainer = contentType == folderType || contentType == functionalObjectType;

        bool expected = !isContainer;
        string? originalFileName = TryReadString(values, ref originalFileNameKey, expected);
        string? persistentId = TryReadString(values, ref persistentIdKey, expected);
        string? modified = TryReadString(values, ref dateModifiedKey, expected);
        ulong? size = TryReadSize(values, ref sizeKey, expected);

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
        ReportError(new ScanError(objectId, parentPath, ScanStage.Properties, ex.HResult, ex.Message));
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
