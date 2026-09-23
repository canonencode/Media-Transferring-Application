namespace MediaTransfer.Core;

/// <summary>
/// What a scanned file turned out to be.
///
/// Lives in its own file rather than beside the walk in Program.cs because
/// FileClassifier - which is pure and has no COM dependency - returns it. A
/// test project can therefore compile the classifier and this enum on their
/// own, without dragging in the WPD interop that Program.cs needs.
/// </summary>
public enum FileKind
{
    /// <summary>
    /// Checked, and it is not media. A real answer, deliberately distinct from
    /// FileClassifier.ClassifyByName returning null, which means "not decided
    /// yet - go read the bytes".
    /// </summary>
    Unknown,

    MediaFile,

    Document,

    /// <summary>
    /// A recording: a voice note, a voice memo, a song, a ringtone.
    ///
    /// Its own kind rather than a flavour of <see cref="MediaFile"/>, because
    /// the two are found differently. Audio is decided by extension ALONE and
    /// never by reading bytes: audio-only MP4 variants share the "ftyp"
    /// container with real video, and telling them apart by major brand was
    /// tested and does not work - different encoders write different brands for
    /// the same audio-only content. Keeping the kinds separate is what lets
    /// audio skip the signature check without also being swept into "not media".
    ///
    /// That sweeping is not hypothetical. It left 107 audio files on a phone
    /// that was then wiped - 70 of them WhatsApp voice notes, which were the one
    /// thing the phone's owner had asked for. The transfer took 13,630 files and
    /// verified every byte of them; it never took these, because nothing in the
    /// record said they were worth taking.
    /// </summary>
    AudioFile,

    /// <summary>
    /// Could NOT be checked - the content read was unavailable, either because
    /// the circuit breaker had already given up on the session or because the
    /// read itself failed.
    ///
    /// This exists because collapsing it into <see cref="Unknown"/> made a file
    /// nobody looked at indistinguishable from one examined and dismissed. Once
    /// the breaker trips mid-scan, every later unrecognised-extension file took
    /// that path: a simulation of a 14,500-object scan with errors starting at
    /// object 3,000 produced 11,480 files reported as "not media" without a
    /// single one of them having been read. Any of those could have been a
    /// photo, and the user had no way to learn which.
    /// </summary>
    Undetermined
}
