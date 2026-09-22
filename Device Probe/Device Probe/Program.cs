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

// PRE-SQLITE: both counters become COUNT queries over a file.classified_by
// column - but that column cannot be filled yet. OnFile reports WHAT a file is,
// never HOW that was decided, so the sink has no way to tell an extension match
// from a signature match. Adding the column means adding that argument first.
int caughtBySignatureOnly = 0;
int signatureChecksActuallyRun = 0;
// PRE-SQLITE: signatureCheckErrors does TWO jobs and only one of them goes.
//
// As a summary statistic it becomes a scan_error row - which needs a Stream
// member added to ScanStage (there is none today) and a sink.OnError call from
// DetectKindBySignature's catch, which currently reports nothing at all.
//
// As the circuit breaker's error signal it STAYS, but the mechanism should
// change: DetectKindBySignature returning a bool instead of the caller diffing
// a global before and after the call.
int signatureCheckErrors = 0;

// The walk no longer prints anything. It reports to a sink, which is what lets
// the SQLite writer consume the same scan without the walk knowing it exists.
var sink = new ConsoleScanSink();
var health = new SessionHealthMonitor();


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
// NOT pre-SQLite, despite appearances. An upsert on UNIQUE(device_id, path)
// would make a duplicate WRITE harmless, but the set also stops the walk from
// re-reading an object it has already handled - and that guard sits in front of
// GetObjectInfo and the signature check, both of which are device round trips.
// A database cannot prevent work that happens before the row is ever produced.
// walkedContainerIds is the same story plus cycle-breaking.
var classifiedObjectIds = new HashSet<string>();  // files already counted
var walkedContainerIds = new HashSet<string>();   // folders already enumerated; also breaks cycles

// Guards CloseSession(), which is reachable from three places at once. An int
// rather than a bool because Interlocked has no bool overload.
int sessionClosed = 0;

// Same guard for the verdict. The watchdog reports just before FailFast, and
// the main thread reports in its finally; if the scan unwedges in the moments
// between the watchdog's Wait timing out and the process dying, both fire.
// Beyond the duplicate output, the two threads would be reading the sink's
// plain List<ScanError> at the same time as the scan thread appends to it.
int outcomeReported = 0;

// PRE-SQLITE: becomes "resolved" on the scan_error row; this set goes.
// Folders that failed to list during the main walk but were successfully
// re-walked by the retry pass. Without this the verdict kept reporting them as
// lost - "N folder(s) could not be listed, losing everything beneath them" -
// even though the retry had listed them and their files are in the results.
// Wrong in the safe direction, but a verdict the user cannot act on or trust.
var recoveredContainerIds = new HashSet<string>();

// Watchdog state. Declared up here because the tree walk writes to it and is
// handed to Task.Run before the watchdog itself is built.
long lastProgressTicks = DateTime.UtcNow.Ticks;
bool stallDetected = false;
bool scanFaulted = false;

// Set by the watchdog, read by the walk. An int because Volatile/Interlocked
// have no bool overloads; 1 means "stop walking, the device is gone".
int scanAborted = 0;

// PRE-SQLITE: the counter goes, but NOT into scan_error. A missing optional
// property is absence, not failure - the same reason TryReadString does not
// report one - and a row in the error table would pollute the "lost one object"
// group with objects that were read perfectly well. It is already derivable
// from the file row: a NULL size or date on a non-container IS the miss.
// Counts a FILE lacking a property the device otherwise supplies for every file.
// Containers are excluded on purpose: the storage root has no filename, size or
// modified date, and counting it produced a constant "3 errors" on every scan
// of every device - which the summary then called "likely real driver errors".
// It was one object, missing the three things a container never has.
int filePropertyMisses = 0;


string? serialNumber = ReadDeviceString(serialKey);
// PRE-SQLITE: these three are the device table's columns, and printing them is
// all that happens to them today - OnScanStarted only receives deviceId and
// friendlyName. A SqliteScanSink keyed on the serial cannot see the serial.
Console.WriteLine($"Serial number: {serialNumber ?? "(not supplied)"}");
Console.WriteLine($"Manufacturer/model: {ReadDeviceString(manufacturerKey) ?? "?"} / {ReadDeviceString(modelKey) ?? "?"}");

bool cameraMode = IsCameraMode();
sink.OnScanStarted(deviceId, friendlyName, cameraMode);

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
            sink.OnScanFinished(new ScanOutcome(
                Completed: false,
                Stalled: true,
                Faulted: false,
                CameraMode: cameraMode,
                UndeterminedFiles: sink.UndeterminedFiles,
                SubtreeLosses: sink.Errors.Count(e => e.Stage is ScanStage.Enumerate or ScanStage.EnumerateNext
                                       && !recoveredContainerIds.Contains(e.ObjectId))));
            Console.WriteLine($"Found before the device stopped answering: {sink.MediaFiles} media file(s) " +
                $"and {sink.Documents} document(s) out of {sink.TotalFilesSeen} file(s) seen.");
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
    // PRE-SQLITE: the outcome is assembled by reading ConsoleScanSink's own
    // properties, which is why `sink` is declared as the concrete type rather
    // than IScanSink. Nothing here compiles against the interface, so a second
    // implementation cannot be dropped in. The walk needs to keep these two
    // figures itself before that is possible.
    if (Interlocked.Exchange(ref outcomeReported, 1) == 0)
    sink.OnScanFinished(new ScanOutcome(
        // NOT just IsCompletedSuccessfully. A walk stopped by the abort flag
        // returns normally, so the task "succeeds" - and ScanOutcome documents
        // Completed as "reached the end under its own power". A sink trusting
        // that alone (the SQLite one will) would record a cut-short scan as a
        // full census and later conclude everything it missed was deleted.
        Completed: scanTask.IsCompletedSuccessfully && Volatile.Read(ref scanAborted) == 0,
        Stalled: stallDetected,
        Faulted: scanFaulted,
        CameraMode: cameraMode,
        UndeterminedFiles: sink.UndeterminedFiles,
        SubtreeLosses: sink.Errors.Count(e => e.Stage is ScanStage.Enumerate or ScanStage.EnumerateNext
                                       && !recoveredContainerIds.Contains(e.ObjectId))));
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
        Console.WriteLine($"\nSkipping the retry pass: the device stopped responding, so re-asking it for " +
            $"{failedObjects.Count} object(s) would only wait for answers that are not coming.");
        return;
    }

    var toRetry = failedObjects.ToList();
    int originalFailedCount = toRetry.Count;
    Console.WriteLine($"\nRetrying {originalFailedCount} previously-failed object(s)...");

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
            PrintTree(objectId, recoveredPath);
            recoveredFolders++;

            // Only Enumerate/EnumerateNext failures inflate SubtreeLosses, so
            // only those are worth un-counting here.
            if (stage is ScanStage.Enumerate or ScanStage.EnumerateNext)
            {
                recoveredContainerIds.Add(objectId);
            }
        }
        else
        {
            ClassifyAndReportFile(retried, parentPath, recovered: true);
            recoveredFiles++;
        }
    }

    // Counted only after the work actually happened. The old version
    // incremented before the Android check and before the re-walk, so blocked
    // folders and folders that failed again both still reported as "recovered".
    // PRE-SQLITE: console-only, and it is trust-relevant - how much of a scan
    // was recovered versus lost belongs on the scan row, not just on screen.
    // Needs a retry-level signal on IScanSink; there is none.
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

// PRE-SQLITE: this whole summary becomes a query, and for the console probe it
// moves into ConsoleScanSink.OnScanFinished. Note what that move needs: the
// sink holds the file totals, but the signature counters and the health state
// below are locals here and reach no sink at all. They have to travel - as
// arguments, or on ScanOutcome - before the block can move anywhere.
Console.WriteLine($"\nDone. {sink.MediaFiles} media file(s) and {sink.Documents} document(s) found out of {sink.TotalFilesSeen} file(s) seen.");
Console.WriteLine($"({caughtBySignatureOnly} of those were caught only by file signature - their extension wasn't recognized.)");
Console.WriteLine($"Expensive signature check actually ran on {signatureChecksActuallyRun} file(s) (out of {sink.TotalFilesSeen} total).");
Console.WriteLine($"Signature check itself errored (not just 'no match') on {signatureCheckErrors} file(s).");
if (health.SkippedBecauseDisabled > 0)
{
    Console.WriteLine($"[IMPORTANT] {health.SkippedBecauseDisabled} file(s) with an unrecognized extension went " +
        "UNCHECKED because the circuit breaker had already disabled signature checking. Any of them could be a " +
        "real photo or video that this scan did not count.");
}
// PRE-SQLITE: the breaker's verdict belongs on the scan row - a scan that
// stopped checking signatures partway is not fully trustworthy - but it reaches
// no sink today, so ScanOutcome cannot carry it yet.
Console.WriteLine(health.CheckingDisabled
    ? "Session health: signature checking was DISABLED partway through this scan (see warning above)."
    : "Session health: OK, signature checking ran normally for the whole scan.");

if (filePropertyMisses > 0)
{
    Console.WriteLine($"\n[NOTE] {filePropertyMisses} file(s) were missing a property (name, size or date) that " +
        "this device otherwise supplies for every file. Containers are not counted here, so each of these is " +
        "a genuine gap worth a look.");
}

// PRE-SQLITE: becomes the scan_skipped_folder table. The console rendering
// moves into ConsoleScanSink; this block leaves Program.cs.
if (sink.SkippedFolders.Count > 0)
{
    Console.WriteLine($"\n{sink.SkippedFolders.Count} folder(s) deliberately not walked:");
    foreach (var skipped in sink.SkippedFolders)
    {
        Console.WriteLine($"  {skipped}");
    }
}

// PRE-SQLITE: becomes the scan_error table plus a GROUP BY. The console
// rendering moves into ConsoleScanSink; this block leaves Program.cs.
// Errors grouped by which call failed and why, instead of a flat count plus
// the first ten messages. The breakdown is the point: an Enumerate/Next
// failure loses a whole subtree while a Properties failure loses one object,
// and one HResult repeated 1,400 times is a very different problem from five
// different HResults - neither of which the old summary could express.
if (sink.Errors.Count > 0)
{
    Console.WriteLine($"\n{sink.Errors.Count} device error(s) during this scan, by call site and cause:");
    foreach (var group in sink.Errors
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

    int subtreeLosses = sink.Errors.Count(e => e.Stage is ScanStage.Enumerate or ScanStage.EnumerateNext);
    if (subtreeLosses > 0)
    {
        Console.WriteLine($"\n[IMPORTANT] {subtreeLosses} of those errors happened while LISTING a folder, so an " +
            "unknown number of files below those points were never seen at all. Affected folders:");
        foreach (var path in sink.Errors
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
        sink.OnError(new ScanError(objectId, parentPath, ScanStage.Enumerate, ex.HResult, ex.Message));
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
                sink.OnError(new ScanError(objectId, parentPath, ScanStage.EnumerateNext, ex.HResult, ex.Message));
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
    FileKind kind = DetectKindBySignature(objectId);

    if (kind is FileKind.MediaFile or FileKind.Document) caughtBySignatureOnly++;

    double? trippedAt = health.RecordResult(errored: signatureCheckErrors > errorsBefore);
    if (trippedAt is double rate)
    {
        // PRE-SQLITE: the moment the breaker trips is a scan-level event worth
        // recording, not just printing.
        Console.WriteLine($"\n[WARNING] Signature checks are failing at {rate:P0} - the device session's " +
            "stream-reading capability looks broken, not just 'these files aren't media'. Disabling the " +
            "signature fallback for the rest of THIS scan (extension-based detection is unaffected and " +
            "continues normally). For full accuracy including unrecognized-extension files, unplug/replug " +
            "the phone (or restart the 'Portable Device Enumerator Service' / WPDBusEnum) and run again.\n");
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
        sink.OnError(new ScanError(objectId, parentPath, ScanStage.Properties, ex.HResult, ex.Message));
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
