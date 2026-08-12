using System.Text;
using Aer.Core;
using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using Aer.Flow.Store;

namespace Aer.Flow.Dispatch;

/// <summary>
/// The concrete binary and arguments to spawn for an <see cref="ExecutionRequest"/>. Resolving a
/// <see cref="ExecutionRequest.Worker"/> role name (e.g. <c>"architect"</c>) to this is a vendor
/// binding concern — <c>CLAUDE.md</c>'s Adapter Isolation rule keeps that resolution out of
/// <c>Aer.Flow</c> entirely, so the caller supplies it explicitly rather than the dispatcher
/// interpreting <see cref="ExecutionRequest.Worker"/> itself.
/// </summary>
/// <param name="WorkingDirectory">
/// The real, already-resolved absolute directory to spawn <see cref="Program"/> in (M23 Phase 3,
/// #272), or <see langword="null"/> to keep the prior default (Core's own process working
/// directory — AER's scratch artifacts folder, never a git-repo requirement). Vendor-agnostic: every
/// <c>IWorkerAdapter</c> forwards <c>WorkerInvocation.WorkingDirectory</c> here unchanged, so a
/// worker can operate on an arbitrary existing project the way it would run raw in a terminal.
/// </param>
/// <param name="PromptText">
/// The exact instructional text this dispatch's adapter built for the worker (issue #292) — e.g.
/// <c>ClaudeWorkerAdapter</c>/<c>AgyWorkerAdapter</c> set this to the identical string they embed
/// as their <c>-p</c> argument. May still contain unexpanded <c>%AER_INPUT_0%</c>/<c>%AER_OUTPUT_DIR%</c>-
/// style placeholders (same convention <see cref="Args"/> already uses) — <see cref="CoreDispatcher"/>
/// expands it the same way before durably writing it to <c>{outputDirectory}/prompt.txt</c>
/// (<see cref="ArtifactManager.PromptFileName"/>), so this record still carries no execution-specific
/// resolved path, matching every other field here. <see langword="null"/> means this adapter has
/// nothing worth capturing this way — <c>DialogueWorkerAdapter</c> leaves this null since its own
/// worker process already durably records each turn's prompt in <c>transcript.jsonl</c>. Archival
/// capture only, for UI/audit display (CLAUDE.md Architecture Rule 1) — never read back by Flow to
/// make a routing decision.
/// </param>
/// <param name="Environment">
/// Extra environment variables to set on the spawned process, beyond whatever
/// <see cref="ExecutionRequest.Environment"/>'s <see cref="EnvironmentVariable.AerComputed"/> entries
/// already contribute (#533). This is the adapter's own seam, not the engine's: a variable like
/// Claude Code's <c>CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH</c> is a vendor quirk, and Architecture Rule
/// 2 keeps vendor quirks inside <c>Aer.Adapters</c> rather than letting <c>Aer.Flow</c> know the
/// variable's name exists. <see langword="null"/> or empty contributes nothing. Since #549 the child
/// does NOT inherit the daemon's whole environment: <c>AerTask.WithClearEnv</c> is called first, so it
/// sees only <see cref="AssembleChildEnvironment"/>'s set — the <c>InheritedEnvironment</c> allowlist,
/// request's AER-computed variables, and these. This param adds to that set and, applied last, wins on
/// a name collision; it does not widen what the allowlist already scopes out (#895).
/// </param>
public sealed record CoreDispatchTarget(
    string Program,
    IReadOnlyList<string> Args,
    string? WorkingDirectory = null,
    Action<string>? OnStdoutLine = null,
    string? PromptText = null,
    IReadOnlyList<(string Name, string Value)>? Environment = null,
    string? StdoutArtifactName = null,
    string? OversizePromptWrapper = null,
    IReadOnlyList<CoreDispatchSeedFile>? SeedFiles = null,
    // #1089: given one complete stdout line, true iff it is this vendor's terminal "finished, status
    // success" marker. Set by the adapter (Adapter Isolation — the dispatcher never parses vendor
    // content, spec Rule 1); null on adapters/paths that do not stream, where the #1089 guard fails
    // safe to "a timeout always fails". Latched into CoreDispatchResult.TerminalSuccessObserved.
    Func<string, bool>? DetectsTerminalSuccess = null);

/// <summary>
/// A launch-configuration file an adapter needs written into place before its worker spawns, where the
/// destination and/or contents reference an AER-computed path (e.g. <c>AER_OUTPUT_DIR</c>) that only
/// resolves inside <see cref="CoreDispatcher.DispatchAsync"/>. Both <paramref name="PathTemplate"/> and
/// <paramref name="Content"/> take the same <c>%NAME%</c>/<c>$NAME</c> placeholder grammar as target
/// arguments and environment values, and are expanded there. Kept vendor-agnostic on purpose: the
/// adapter owns what the file says (Adapter Isolation), the dispatcher only writes it.
/// </summary>
public sealed record CoreDispatchSeedFile(string PathTemplate, string Content);

/// <summary>
/// The raw, unclassified facts of a completed dispatch (spec §8's <c>NaturalExit</c> |
/// <c>TimedOut</c> | <c>CancelRequested</c> vocabulary). M7 Phase 6 explicitly excludes outcome
/// classification — mapping this into <c>ExecutionSucceeded</c>/<c>ExecutionFailed</c>/
/// <c>ExecutionCancelled</c> is the Outcome Classifier's job (Phase 7, spec §8).
/// </summary>
/// <param name="StderrTail">
/// The last <see cref="CoreDispatcher.MaxRetainedStderrLength"/> characters the worker wrote to
/// stderr, or <see langword="null"/> if it wrote nothing (#563). The <i>tail</i> specifically: a
/// vendor CLI's actionable line is the last thing it prints, so head-first truncation would discard
/// exactly the message this field exists to carry.
/// <para>
/// Null also on the crash-recovery path, where <c>MutationInterface</c> rebuilds a result from a
/// stored <c>CoreEvent.ExecutionExited</c> after a restart — stderr was never written to the Event
/// Store, so it genuinely does not survive a crash. Read a null as "not recorded", never as "the
/// worker was silent".
/// </para>
/// </param>
/// <param name="TerminalSuccessObserved">
/// True when the worker emitted a <b>terminal success</b> event on stdout during the run — its vendor
/// CLI's own "I finished, status success" marker (agy's <c>{"event":"result","result":{"status":
/// "SUCCESS"}}</c>, claude's <c>{"type":"result","subtype":"success","is_error":false}</c>), detected by
/// the adapter (Adapter Isolation) via <see cref="CoreDispatchTarget.DetectsTerminalSuccess"/>. It is
/// the ONE fact that distinguishes "the worker finished, then hung at teardown" from "the worker was
/// killed mid-work": the <see cref="Outcomes.OutcomeClassifier"/> uses it to let a <c>TimedOut</c> run
/// whose declared outputs are all present classify as Succeeded instead of a doomed from-scratch retry
/// (#1089). False on the crash-recovery path and whenever the worker did not stream (no marker to see),
/// so the guard fails safe toward today's "a timeout always fails" behaviour.
/// </param>

/// <param name="StdoutTail">
/// The last <see cref="CoreDispatcher.MaxRetainedStderrLength"/> characters the worker wrote to
/// stdout, or <see langword="null"/> if it wrote nothing. The <i>tail</i> specifically: bounded
/// retention for failure classification (0026/#1115), allowing classifiers to inspect typed worker
/// outputs on stdout without loading full execution streams.
/// <para>
/// Null also on the crash-recovery path, where <c>MutationInterface</c> rebuilds a result from a
/// stored <c>CoreEvent.ExecutionExited</c> after a restart — stdout tail is not written to the Event
/// Store, so it does not survive a crash.
/// </para>
/// </param>
public sealed record CoreDispatchResult(
    int ExitCode,
    CoreExitReason Reason,
    string? StderrTail = null,
    bool TerminalSuccessObserved = false,
    string? StdoutTail = null);


/// <summary>
/// What <c>MutationInterface</c> needs from a dispatcher (spec §12's "Flow never executes a
/// process; it only ever reads the Event Store and emits requests" — this is the seam through
/// which it emits them). Extracted from <see cref="CoreDispatcher"/> so mutation-level tests can
/// substitute a stub with <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/>-controlled
/// completion order (M8 Phase 3) instead of spawning real processes.
/// </summary>
public interface ICoreDispatcher
{
    /// <inheritdoc cref="CoreDispatcher.DispatchAsync"/>
    Task<CoreDispatchResult> DispatchAsync(
        ExecutionRequest request,
        CoreDispatchTarget target,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Accumulates the tail of a worker's output stream as chunks arrive from the native callback
/// (#563): decodes, collapses whitespace, and keeps at most
/// <see cref="CoreDispatcher.MaxRetainedStderrLength"/> characters. Named for the stderr capture
/// it was built for; since #1115 a second instance captures the stdout tail
/// (<see cref="CoreDispatchResult.StdoutTail"/>) with identical, stream-agnostic mechanics.
/// </summary>
/// <remarks>
/// <para>
/// The three pieces of state are one object rather than three parallel locals because they are only
/// correct together: the decoder must be stateful across chunks, and so must
/// <see cref="pendingSpace"/>, or a whitespace run split across a chunk boundary collapses to two
/// spaces instead of one.
/// </para>
/// <para>
/// <b>Whitespace is collapsed here, at capture time, and that placement is the fix for a real
/// defect rather than a tidiness choice.</b> It used to happen in <c>OutcomeClassifier</c>, i.e.
/// <i>between</i> the retention cap below and the display cap there — so the two caps measured
/// different units and the "a silent drop always implies a marked drop" guarantee did not hold. Two
/// concrete failures came out of that ordering: stderr that was mostly indentation could lose
/// thousands of characters to the silent cap and still collapse to under the display cap, showing an
/// operator a truncated tail with no ellipsis; and a worker that printed a diagnostic followed by
/// enough blank lines to fill the buffer had its tail retained as pure whitespace, which collapsed to
/// nothing and restored the exact bare reason this issue exists to replace. Collapsing first makes
/// both caps count the same characters, so the ordering argument is sound and both failures are
/// impossible rather than merely reported.
/// </para>
/// </remarks>
internal sealed class StderrTailBuffer
{
    private readonly System.Text.StringBuilder buffer = new();
    private readonly System.Text.Decoder decoder = System.Text.Encoding.UTF8.GetDecoder();

    /// <summary>
    /// Whether a whitespace run has been seen whose space has not been emitted yet. Deferred rather
    /// than emitted on sight, so runs collapse to one space and neither a leading nor a trailing one
    /// is ever written.
    /// </summary>
    private bool pendingSpace;

    /// <summary>Decodes one chunk of stderr bytes and folds it into the retained tail.</summary>
    public void Append(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // Stateful decode, not one GetString per chunk: a pipe splits at arbitrary byte offsets, so a
        // multi-byte UTF-8 sequence routinely straddles two chunks. Decoding each chunk independently
        // emits a replacement character at every such boundary, corrupting exactly the non-ASCII
        // diagnostics this exists to carry.
        // GetChars runs even when the count is zero, and skipping it was a real bug rather than an
        // optimisation. GetCharCount is a pure calculation — it does NOT hand the bytes to the
        // decoder — so returning early on a zero count discarded them: the decoder never saw the
        // partial sequence it was supposed to be holding, and the next chunk then began with a
        // continuation byte it could only render as U+FFFD. It shows up solely when a chunk decodes
        // to nothing at all, i.e. when the very first bytes of the stream are a split multi-byte
        // character, which is why only the 2-byte split case in the theory catches it.
        var maxChars = decoder.GetCharCount(data, 0, data.Length, flush: false);
        var chars = new char[maxChars];
        var written = decoder.GetChars(data, 0, data.Length, chars, 0, flush: false);
        if (written > 0)
        {
            AppendCollapsed(chars.AsSpan(0, written));
        }
    }

    /// <summary>
    /// Returns the retained tail, or <see langword="null"/> if the worker wrote nothing that survived
    /// collapsing — which must stay distinguishable from "wrote something", since a caller renders an
    /// empty tail as no tail at all rather than as an empty label.
    /// </summary>
    public string? ToTailOrNull()
    {
        // Flushing emits U+FFFD for a trailing sequence the worker cut short (it died mid-write).
        // Better a visible replacement character than silently dropping the final character of the
        // very line being diagnosed.
        var maxChars = decoder.GetCharCount([], 0, 0, flush: true);
        if (maxChars > 0)
        {
            var chars = new char[maxChars];
            var written = decoder.GetChars([], 0, 0, chars, 0, flush: true);
            AppendCollapsed(chars.AsSpan(0, written));
        }

        return buffer.Length > 0 ? buffer.ToString() : null;
    }

    private void AppendCollapsed(ReadOnlySpan<char> chars)
    {
        foreach (var ch in chars)
        {
            if (char.IsWhiteSpace(ch))
            {
                // Suppressed while the buffer is empty, so a leading run never emits anything.
                pendingSpace = buffer.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                buffer.Append(' ');
                pendingSpace = false;
            }

            buffer.Append(ch);
        }

        TrimToTail(buffer);
    }

    /// <summary>
    /// Drops the oldest characters so <paramref name="target"/> holds at most
    /// <see cref="CoreDispatcher.MaxRetainedStderrLength"/> — keeping the <i>end</i>, which is where
    /// a vendor CLI puts the line worth reading.
    /// </summary>
    internal static void TrimToTail(System.Text.StringBuilder target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.Length <= CoreDispatcher.MaxRetainedStderrLength)
        {
            return;
        }

        var excess = target.Length - CoreDispatcher.MaxRetainedStderrLength;

        // Cutting from the front is the mirror of ContractValidator.TrimWithoutSplittingSurrogatePair,
        // which cuts from the back: if the first surviving char is a low surrogate, its high half is
        // among the ones being removed, so drop the orphan too rather than leaving a lone half-pair.
        // The bounds guard is unreachable while MaxRetainedStderrLength is positive, and is here for
        // the same reason its counterpart there is: this runs inside a native callback, where an
        // IndexOutOfRangeException would surface far from the edit that lowered the cap.
        if (excess < target.Length && char.IsLowSurrogate(target[excess]))
        {
            excess++;
        }

        target.Remove(0, excess);
    }
}

/// <summary>
/// Accumulates a worker's stdout and hands back whole lines, decoding STATEFULLY across chunks
/// (#642).
/// </summary>
/// <remarks>
/// Extracted from <c>RunAsync</c>'s event loop so it can be driven at chosen byte offsets. The
/// decode used to sit inline as a stateless <c>Encoding.UTF8.GetString</c> per chunk, which was
/// unreachable from a test: a pipe splits where it likes, so the defect needed a boundary landing
/// mid-character and could not be provoked deterministically through a real process.
/// <para>
/// <see cref="StderrTailBuffer"/> had carried a <c>Decoder</c> since it was written and this path
/// never did. That asymmetry is the wrong way round: stdout is the worker's own output, the text
/// rendered in the Conversation tab, so it had the weaker treatment where it mattered more.
/// </para>
/// <para>
/// NOT thread-safe, deliberately and like its stderr sibling — the caller already holds a lock for
/// the line buffer, and the decoder's cross-chunk state has to be inside that same lock rather than
/// beside it.
/// </para>
/// </remarks>
internal sealed class StdoutLineBuffer
{
    /// <summary>
    /// The ceiling on a newline-free run this buffer will hold before splitting it (#701).
    /// </summary>
    /// <remarks>
    /// Measured before chosen (#701 required exactly that order): the longest single line across
    /// 68,399 lines of the vendor CLIs' own JSONL streams on the measuring machine was 1,346,950
    /// bytes — a <c>claude</c> stream-json line; <c>agy</c>'s longest was 8,529. This ceiling is
    /// roughly six times that worst case, so no legitimately long line observed to date comes near
    /// a split, while a runaway newline-free stream (a <c>\r</c> progress bar, binary on the wrong
    /// descriptor) is bounded inside the daemon's process. Split-with-marker was chosen over the
    /// stderr sibling's keep-the-tail because stdout is what the Conversation tab renders and is
    /// read top-down: every character still arrives, in order, and the fabricated boundary is the
    /// marked thing rather than the dropped thing.
    /// </remarks>
    public const int MaxBufferedLineLength = 8_000_000;

    /// <summary>
    /// Appended to every synthetic line the ceiling fabricates, so an operator reading a fragment
    /// can tell it is one — the silent-fragment outcome is the one #701 names as unacceptable.
    /// </summary>
    public static readonly string SplitMarker =
        $" ⟦AER: no newline for {MaxBufferedLineLength:N0} characters — line split by the engine⟧";

    private readonly System.Text.StringBuilder buffer = new();
    private readonly System.Text.Decoder decoder = System.Text.Encoding.UTF8.GetDecoder();

    /// <summary>Decodes one chunk and emits every complete line it completes.</summary>
    public void Append(byte[] data, Action<string> onLine)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(onLine);

        // GetChars runs even when the count is zero. GetCharCount is a pure calculation and does NOT
        // hand the bytes to the decoder, so returning early on a zero count would discard the partial
        // sequence the decoder is meant to be holding — the defect StderrTailBuffer records having
        // shipped, and the reason the 2-byte split arm exists in both theories.
        var maxChars = decoder.GetCharCount(data, 0, data.Length, flush: false);
        var chars = new char[maxChars];
        var written = decoder.GetChars(data, 0, data.Length, chars, 0, flush: false);
        buffer.Append(chars, 0, written);

        var content = buffer.ToString();
        int newlineIndex;
        while ((newlineIndex = content.IndexOf('\n', StringComparison.Ordinal)) >= 0)
        {
            onLine(content[..newlineIndex].TrimEnd('\r'));
            content = content[(newlineIndex + 1)..];
        }

        // The remainder is a line still waiting for its newline, and a worker that never sends one
        // must not grow it forever — see MaxBufferedLineLength for the measurement behind the
        // ceiling and why splitting beats retaining a tail here. Strictly greater than: a run
        // exactly AT the ceiling is still a legitimate line waiting, never split.
        while (content.Length > MaxBufferedLineLength)
        {
            // The cut index counts UTF-16 chars, and a code point above the BMP is a surrogate
            // PAIR — cutting between its halves emits a lone surrogate that any downstream UTF-8
            // re-encode silently replaces with U+FFFD, breaking "every character still arrives".
            // Same guard StderrTailBuffer has always had at its own cut point: back off by one.
            var cut = MaxBufferedLineLength;
            if (char.IsHighSurrogate(content[cut - 1]))
            {
                cut--;
            }

            onLine(content[..cut] + SplitMarker);
            content = content[cut..];
        }

        buffer.Clear();
        buffer.Append(content);
    }

    /// <summary>Emits whatever is left when the stream ends without a trailing newline.</summary>
    public void Flush(Action<string> onLine)
    {
        ArgumentNullException.ThrowIfNull(onLine);

        // Draining the decoder is what makes a stateful decode safe at end-of-stream, and it is the
        // half a chunk-boundary test cannot reach: no mutation of Append turns this red. Without it a
        // stateful decode is STRICTLY WORSE here than the stateless one it replaced — bytes the
        // decoder is holding for a sequence the worker never finished are simply dropped, and when
        // they are all that is left the final line disappears entirely rather than arriving as U+FFFD.
        // See StderrTailBuffer.ToTailOrNull, which has always done this, for why visible beats silent.
        var maxChars = decoder.GetCharCount([], 0, 0, flush: true);
        if (maxChars > 0)
        {
            var chars = new char[maxChars];
            var written = decoder.GetChars([], 0, 0, chars, 0, flush: true);
            buffer.Append(chars, 0, written);
        }

        if (buffer.Length > 0)
        {
            onLine(buffer.ToString());
            buffer.Clear();
        }
    }
}


/// <summary>
/// Calls the aer-core M5 <c>AerTask</c> binding with an <see cref="ExecutionRequest"/> and records
/// Core's lifecycle events to the combined log (M7 Phase 6). This is the P/Invoke Layer
/// <c>CLAUDE.md</c> requires: the only place in <c>Aer.Flow</c> that touches <c>Aer.Core</c>
/// directly.
/// </summary>
public sealed class CoreDispatcher(ICoreEventLogWriter coreEventLogWriter) : ICoreDispatcher
{
    /// <summary>
    /// How many characters of a worker's stderr are retained for
    /// <see cref="CoreDispatchResult.StderrTail"/> (#563).
    /// </summary>
    /// <remarks>
    /// Deliberately larger than <c>OutcomeClassifier</c>'s own display cap. This bound exists to stop
    /// a chatty worker from growing an unbounded buffer in a native callback; deciding how much of it
    /// an operator actually reads is the classifier's job, and pre-truncating here to the display
    /// size would take that choice away from it.
    /// </remarks>
    public const int MaxRetainedStderrLength = 2000;

    /// <summary>
    /// Expanded-prompt length at which <see cref="DispatchAsync"/> stops passing the prompt inline
    /// and swaps in the adapter's <see cref="CoreDispatchTarget.OversizePromptWrapper"/> pointing at
    /// the already-captured <c>prompt.txt</c> (#748). Deliberately far below every platform
    /// command-line cap this class guards, and fixed rather than derived from them, so the same
    /// workflow delivers its prompt the same way on every OS.
    /// </summary>
    public const int OversizePromptThreshold = 4000;

    /// <summary>
    /// The assembled-command-line ceiling <see cref="DispatchAsync"/> guards against on Windows
    /// (#598), held below <c>CreateProcessW</c>'s documented 32,767-character <c>lpCommandLine</c>
    /// maximum. <see cref="MeasureCommandLineLength"/> is an upper bound, so this margin is not load
    /// bearing the way it was when the measure could under-count; it covers the terminating NUL that
    /// bound omits, and leaves room for the bound to be tightened later without moving the ceiling.
    /// </summary>
    internal const int WindowsCommandLineCeiling = 32_000;

    /// <summary>
    /// The single-integer, UTF-16 command-line ceiling for the running OS, or <see langword="null"/>
    /// where the platform's limit is not that shape. Windows carries a number — its
    /// <c>CreateProcessW</c> <c>lpCommandLine</c> maximum, measured here against #579's
    /// <c>Win32Exception (206)</c>. POSIX returns <see langword="null"/> deliberately: its limit is not
    /// one integer but two byte-based caps — a per-argument <c>MAX_ARG_STRLEN</c> and a total
    /// <c>ARG_MAX</c> across argv+envp — enforced by <see cref="GuardPosixArgumentLength"/> and
    /// <see cref="GuardPosixTotalLength"/> instead (#612), so there is no single number to hand back.
    /// <see langword="null"/> here therefore means "guarded elsewhere", not "unguarded":
    /// <see cref="DispatchAsync"/> branches on it. An over-long POSIX command line is now refused
    /// up-front as a <see cref="CommandLineTooLongException"/> — the same <c>AerFlowException</c> the
    /// Windows path raises, so it no longer escapes <c>Aer.Cli</c>'s top-level handler as a raw stack
    /// trace the way it did before this guard existed.
    /// </summary>
    internal static int? PlatformCommandLineCeiling =>
        OperatingSystem.IsWindows() ? WindowsCommandLineCeiling : null;

    /// <summary>
    /// An upper bound on the command line <c>std::process::Command</c> assembles from
    /// <paramref name="program"/> and <paramref name="args"/> inside aer-core: each argument
    /// contributes its own characters, a separating space, a surrounding quote pair, and the worst
    /// case of std's escaping.
    /// </summary>
    /// <remarks>
    /// A bound rather than an exact reproduction, deliberately: being exact would mean reimplementing
    /// rustc's Windows argument-quoting rules here and holding them in step with a toolchain this repo
    /// does not pin — a claim about someone else's internals no test of ours could keep honest. But a
    /// bound only has to be an over-estimate to be sound, which needs far less than the real rules.
    /// <para>
    /// Escaping never adds more than one character per <c>"</c> plus one per <c>\</c> in an argument:
    /// std emits <c>2n+1</c> backslashes for an interior quote preceded by <c>n</c> of them (<c>n+1</c>
    /// beyond what the raw characters already contribute) and doubles a trailing backslash run
    /// (<c>n</c> beyond). Counting one for each of those characters therefore cannot under-shoot.
    /// </para>
    /// <para>
    /// This started as <c>Length + 3</c> with no escape term, on the reasoning that under-counting only
    /// reproduces today's OS-level failure rather than regressing it. True, but it made the guard miss
    /// an ordinary case: review of #598 pointed out that roughly 768 quote characters in a near-ceiling
    /// argument exhaust the whole margin below 32,767, and a prompt quoting JSON, a schema, or a file's
    /// contents reaches that easily. So the bound is exact enough to not need the margin — the margin
    /// now covers only <see cref="string.Length"/> counting UTF-16 code units, which is what
    /// <c>CreateProcessW</c> counts too, and the terminating NUL that is not counted here.
    /// </para>
    /// </remarks>
    internal static int MeasureCommandLineLength(string program, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(args);

        // The program is quoted but not preceded by a separator; every argument after it is.
        var length = EscapedLength(program) + 2;
        foreach (var arg in args)
        {
            length += EscapedLength(arg) + 3;
        }

        return length;
    }

    /// <summary>
    /// One value's characters plus the most std's Windows escaping can add to them — see
    /// <see cref="MeasureCommandLineLength"/>'s remarks for why one per <c>"</c> and one per <c>\</c>
    /// is an over-estimate rather than a reproduction of the real rules.
    /// </summary>
    private static int EscapedLength(string value)
    {
        var length = value.Length;
        foreach (var character in value)
        {
            if (character is '"' or '\\')
            {
                length++;
            }
        }

        return length;
    }

    /// <summary>
    /// Throws <see cref="CommandLineTooLongException"/> when <paramref name="program"/> and
    /// <paramref name="args"/> would assemble past <paramref name="ceiling"/> (#598). Takes the
    /// ceiling as an argument rather than reading <see cref="PlatformCommandLineCeiling"/> itself, so
    /// that the boundary is exercisable on every OS the test suite runs on and not only the one whose
    /// limit is being enforced.
    /// </summary>
    internal static void GuardCommandLineLength(string program, IReadOnlyList<string> args, int ceiling)
    {
        var length = MeasureCommandLineLength(program, args);
        if (length <= ceiling)
        {
            return;
        }

        // Report the longest single argument alongside the total rather than naming a cause. Both
        // adapters embed the whole prompt as one argument, so that figure is the prompt nearly every
        // time — but not always: a long PermissionScope or several --add-dir paths contribute too, and
        // an operator whose longest argument turns out to be small needs to see that rather than be
        // sent to shorten content that was never the problem. The guidance points at the fix decision
        // 0048 settled on — file-passing — not "make the prompt shorter", because the overflow is
        // almost always inlined content, which belongs in a file the worker reads.
        var longest = args.Count == 0 ? 0 : args.Max(arg => arg.Length);
        throw new CommandLineTooLongException(
            $"Cannot dispatch '{program}': its command line assembles to about {length} characters, "
            + $"past the {ceiling} this platform is guarded at. Its longest single argument is "
            + $"{longest} characters — a worker's prompt is passed inline as one argument. Hand large "
            + "content to the worker as a file it reads under its read-files grant (as the review workflow "
            + "does), rather than inlining it in the prompt.");
    }

    /// <summary>
    /// The byte image a single argument occupies in a POSIX <c>exec</c> — its UTF-8 encoding plus the
    /// terminating NUL. POSIX counts bytes, not the UTF-16 code units <see cref="MeasureCommandLineLength"/>
    /// counts for <c>CreateProcessW</c>, so a non-ASCII prompt weighs more here than on Windows.
    /// </summary>
    internal static int PosixArgBytes(string value) => Encoding.UTF8.GetByteCount(value) + 1;

    /// <summary>
    /// Throws <see cref="CommandLineTooLongException"/> when any single argument's byte image reaches
    /// <paramref name="maxArgStrlen"/> — Linux's per-argument <c>MAX_ARG_STRLEN</c>, which both adapters'
    /// single-inline-prompt shape is exactly what exceeds first (#612). Takes the cap as an argument
    /// rather than reading <see cref="PosixProcessLimits.LinuxMaxArgStrlen"/> itself, so the boundary is
    /// exercisable on every OS the suite runs on and not only Linux — the same reason
    /// <see cref="GuardCommandLineLength"/> takes its ceiling in. The kernel refuses an argument whose
    /// bytes-including-NUL exceed the cap, so <see cref="PosixArgBytes"/> is compared with <c>&gt;</c>.
    /// </summary>
    internal static void GuardPosixArgumentLength(string program, IReadOnlyList<string> args, int maxArgStrlen)
    {
        foreach (var arg in args)
        {
            var bytes = PosixArgBytes(arg);
            if (bytes <= maxArgStrlen)
            {
                continue;
            }

            throw new CommandLineTooLongException(
                $"Cannot dispatch '{program}': one of its arguments is about {bytes} bytes, past the "
                + $"{maxArgStrlen}-byte per-argument limit this platform enforces (MAX_ARG_STRLEN). A "
                + "worker's prompt is passed inline as one argument. Hand large content to the worker as "
                + "a file it reads under its read-files grant (as the review workflow does), rather than "
                + "inlining it in the prompt.");
        }
    }

    /// <summary>
    /// A conservative upper bound on the bytes the program, its arguments, and the child's environment
    /// occupy in one POSIX <c>exec</c> image — the total <c>ARG_MAX</c> is charged against (a limit
    /// across argv <em>and</em> envp, #612). Each string is charged its UTF-8 bytes, a NUL, and a 64-bit
    /// pointer slot, and a duplicated environment name is charged twice, so the figure can only over-shoot
    /// the kernel's real accounting, never under-shoot it — the same over-estimate discipline
    /// <see cref="MeasureCommandLineLength"/> uses for Windows.
    /// </summary>
    internal static long MeasurePosixTotalBytes(
        string program,
        IReadOnlyList<string> args,
        IReadOnlyList<(string Name, string Value)> environment)
    {
        // Every platform AER ships on is 64-bit, so each argv/envp entry costs an 8-byte pointer on top
        // of its string. Counting it makes the bound an over-estimate of the kernel's real accounting.
        const int pointerBytes = 8;

        long total = PosixArgBytes(program) + pointerBytes;
        foreach (var arg in args)
        {
            total += PosixArgBytes(arg) + pointerBytes;
        }

        foreach (var (name, value) in environment)
        {
            // "NAME=VALUE\0" plus its envp pointer.
            total += Encoding.UTF8.GetByteCount(name) + 1 + Encoding.UTF8.GetByteCount(value) + 1 + pointerBytes;
        }

        return total;
    }

    /// <summary>
    /// Throws <see cref="CommandLineTooLongException"/> when <see cref="MeasurePosixTotalBytes"/> exceeds
    /// <paramref name="argMax"/> — the kernel's <c>ARG_MAX</c>, a total across argv <em>and</em> envp,
    /// which is why <paramref name="environment"/> is passed and measured here and not just the arguments
    /// (#612). Takes the cap as an argument for the same cross-OS testability reason as the guards above.
    /// </summary>
    internal static void GuardPosixTotalLength(
        string program,
        IReadOnlyList<string> args,
        IReadOnlyList<(string Name, string Value)> environment,
        long argMax)
    {
        var total = MeasurePosixTotalBytes(program, args, environment);
        if (total <= argMax)
        {
            return;
        }

        throw new CommandLineTooLongException(
            $"Cannot dispatch '{program}': its command line and environment assemble to about {total} "
            + $"bytes, past this platform's {argMax}-byte ARG_MAX (a combined limit on arguments and "
            + "environment). A worker's prompt is passed inline as one argument. Hand large content to "
            + "the worker as a file it reads under its read-files grant (as the review workflow does), "
            + "rather than inlining it in the prompt.");
    }

    /// <summary>
    /// The exact environment the spawned child receives, in application order: the inherited allowlist
    /// (<see cref="InheritedEnvironment"/>), then <paramref name="request"/>'s AER-computed variables,
    /// then <paramref name="target"/>'s own adapter variables — later entries overriding earlier ones by
    /// name when applied, the ordering <c>ClaudeWorkerAdapter.SimpleModeVariable</c> depends on. One
    /// assembly point so <see cref="GuardPosixTotalLength"/> measures precisely what
    /// <see cref="DispatchAsync"/> applies: two enumerations of these three sources would drift the
    /// moment a fourth is added, and the drift would silently mis-size the ARG_MAX guard.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Value)> AssembleChildEnvironment(
        ExecutionRequest request, CoreDispatchTarget target)
    {
        var environment = new List<(string Name, string Value)>();
        environment.AddRange(InheritedEnvironment.Resolve());

        var pathVariables = request.Environment
            .OfType<EnvironmentVariable.AerComputed>()
            .ToDictionary(v => v.Name, v => v.Value);

        foreach (var environmentVariable in request.Environment)
        {
            // PassThrough variable *values* are resolved by whatever wires a concrete worker adapter
            // (spec §3) — out of scope here. Only AER-computed variables (paths the Artifact Manager
            // already resolved) are set.
            if (environmentVariable is EnvironmentVariable.AerComputed aerComputed)
            {
                environment.Add((aerComputed.Name, aerComputed.Value));
            }
        }

        // Target environment VALUES take the same placeholder grammar as target arguments (#442: the
        // agy per-execution home references AER_OUTPUT_DIR, which only exists here). Expansion is
        // keyed on the computed-variable names, so a value carrying no such token is untouched.
        if (target.Environment is { } targetEnvironment)
        {
            foreach (var (name, value) in targetEnvironment)
            {
                environment.Add((name, ExpandVariables(value, pathVariables)));
            }
        }

        return environment;
    }

    /// <summary>
    /// Spawns <paramref name="target"/> with <paramref name="request"/>'s AER-computed environment
    /// variables and timeout, and returns once the process has exited, timed out, or been
    /// cancelled. Never throws for any of those three outcomes — each is a normal result §8 must
    /// later classify, not an error condition — but does not suppress genuine dispatch failures
    /// (e.g. the binary could not be spawned at all), which propagate as <see cref="AerException"/>.
    /// </summary>
    public async Task<CoreDispatchResult> DispatchAsync(
        ExecutionRequest request,
        CoreDispatchTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(target);

        // Resolve variable values from request.Environment
        var pathVariables = request.Environment
            .OfType<EnvironmentVariable.AerComputed>()
            .ToDictionary(v => v.Name, v => v.Value);

        // Perform expansion on target arguments
        var expandedArgs = target.Args.Select(arg => ExpandVariables(arg, pathVariables)).ToList();

        // The child's environment, assembled once so the ARG_MAX guard below measures exactly what
        // WithEnv applies further down — POSIX ARG_MAX is a total across argv AND envp, so the guard
        // cannot be honest without the environment in hand at guard time.
        var childEnvironment = AssembleChildEnvironment(request, target);

        // Issue #292: durably capture the resolved prompt an ordinary (non-dialogue) step's worker
        // was actually invoked with — the same UI/audit transparency a dialogue step's transcript.jsonl
        // already gives its per-turn prompts (CLAUDE.md Architecture Rule 1: archival capture for UI
        // display, never read back to make a routing decision). Written before AerTask ever spawns
        // (below), so it is present even if the execution later fails or times out. Null PromptText
        // (DialogueWorkerAdapter; a future adapter with nothing to capture) is a deliberate no-op, not
        // a missing-data condition.
        if (target.PromptText is { } promptText && pathVariables.TryGetValue("AER_OUTPUT_DIR", out var outputDirectory))
        {
            var promptFilePath = Path.Combine(outputDirectory, ArtifactManager.PromptFileName);
            var expandedPromptText = ExpandVariables(promptText, pathVariables);
            await File.WriteAllTextAsync(promptFilePath, expandedPromptText, CancellationToken.None)
                .ConfigureAwait(false);

            // #748: when the adapter provides an OversizePromptWrapper and the expanded prompt length
            // reaches or exceeds OversizePromptThreshold, swap the inline prompt argument for the
            // expanded wrapper and pass AER_PROMPT_FILE in the child environment so command-line
            // guards measure the shortened argument list.
            if (target.OversizePromptWrapper is { } wrapper && expandedPromptText.Length >= OversizePromptThreshold)
            {
                var promptArgIndex = target.Args.ToList().IndexOf(promptText);
                if (promptArgIndex >= 0)
                {
                    pathVariables["AER_PROMPT_FILE"] = promptFilePath;
                    expandedArgs[promptArgIndex] = ExpandVariables(wrapper, pathVariables);

                    var updatedChildEnvironment = childEnvironment.ToList();
                    updatedChildEnvironment.Add(("AER_PROMPT_FILE", promptFilePath));
                    childEnvironment = updatedChildEnvironment;
                }
            }
        }

        // Seed vendor-declared launch files (Adapter Isolation: the adapter owns the contents) whose
        // path and/or body reference an AER-computed variable that only resolves here — the same reason
        // the prompt capture and the agy per-execution home live at this point. agy's own settings.json
        // carrying a permissions.allow for the granted write is the first user (#1084): a write-granted
        // agy role with no shell/network runs under --mode accept-edits, where agy headless-denies the
        // write unless an allow-rule is present; the hook still bounds where the write may land.
        if (target.SeedFiles is { Count: > 0 } seedFiles)
        {
            foreach (var seed in seedFiles)
            {
                var seedPath = ExpandVariables(seed.PathTemplate, pathVariables);
                var seedDirectory = Path.GetDirectoryName(seedPath);
                if (!string.IsNullOrEmpty(seedDirectory))
                {
                    Directory.CreateDirectory(seedDirectory);
                }

                await File.WriteAllTextAsync(seedPath, RenderSeedContent(seed.Content, pathVariables), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        // #598: measured here, on the expanded arguments, because this is the only place the real
        // command line exists — an adapter builds `%AER_OUTPUT_DIR%`, not the absolute path that
        // placeholder becomes above, so a guard living in an adapter would measure the wrong string.
        // Deliberately after the prompt capture: a command line long enough to trip this is a prompt
        // problem, and prompt.txt is the artifact an operator needs in order to see how it got that
        // big — throwing before writing it would withhold the evidence for the very failure reported.
        if (PlatformCommandLineCeiling is { } ceiling)
        {
            GuardCommandLineLength(target.Program, expandedArgs, ceiling);
        }
        else
        {
            // POSIX (#612): two byte-based caps rather than one UTF-16 ceiling. The per-argument
            // MAX_ARG_STRLEN is Linux-only — macOS has no per-argument cap and bounds the prompt through
            // ARG_MAX alone — and ARG_MAX is queried at runtime, skipped when it cannot be determined
            // (see PosixProcessLimits.ArgMaxBytes). Both throw CommandLineTooLongException, which
            // MutationInterface records as Permanent — the same up-front, non-retried refusal the Windows
            // path already produces.
            if (OperatingSystem.IsLinux())
            {
                GuardPosixArgumentLength(target.Program, expandedArgs, PosixProcessLimits.LinuxMaxArgStrlen);
            }

            if (PosixProcessLimits.ArgMaxBytes() is { } argMax)
            {
                GuardPosixTotalLength(target.Program, expandedArgs, childEnvironment, argMax);
            }
        }

        // Only ever invoked for a WorkerBinding.Process dispatch (MutationInterface never calls a
        // dispatcher for a NonProcess execution, §17.3) — Timeout is therefore always set.
        using var task = new AerTask(target.Program, [.. expandedArgs]).WithTimeout(request.Timeout!.Value);

        if (target.WorkingDirectory is { } workingDirectory)
        {
            task.WithCwd(workingDirectory);
        }

        // Unconditional since #563. This used to be gated on `target.OnStdoutLine is not null`, i.e.
        // the dialogue/chat path only, which meant an ordinary `aer run` never captured — and
        // aer-core's no-sink drain runs `io::copy(&mut reader, &mut io::sink())` (os/mod.rs:121), so
        // every byte the worker wrote explaining its own failure was read and thrown away.
        //
        // Nothing visible regresses by turning this on: both platforms already spawn the child with
        // `.stderr(Stdio::piped())` unconditionally and explicitly never `Stdio::inherit`
        // (os/unix.rs:26, os/windows.rs:78), so this output has never reached the operator's terminal
        // and there is no inherited stream to take away.
        //
        // aer-core has no stderr-only capture mode — one bool covers both streams — so this also
        // starts delivering StdoutChunk for non-chat dispatches. That case is a no-op below, and the
        // guard there is *decode-free*, not allocation-free: by the time it runs, the binding has
        // already copied the chunk into a managed array (CallbackBridge.cs:36-37, unconditional for
        // any chunk event with DataLen > 0) and allocated an AerEventArgs. Those allocations are a
        // layer below anything this file can suppress. Chunks are 8 KiB, and a `-p` style adapter
        // produces tens of KB, so it is a handful of short-lived arrays per dispatch — gen0 churn,
        // not a leak. Stated precisely because the earlier wording here claimed the non-chat path
        // cost nothing, which would have been read as "we checked".
        task.WithCaptureOutput(true);

        // #549: the child inherited the operator's ENTIRE environment until WithClearEnv existed, so a
        // CLAUDE_CODE_SIMPLE=1 exported anywhere in the shell that started the daemon disabled the
        // mandatory gate on every worker, silently. WithClearEnv means the child sees only
        // childEnvironment, whose source order and override semantics AssembleChildEnvironment's own doc
        // states. See InheritedEnvironment for what survives.
        task.WithClearEnv();
        foreach (var (name, value) in childEnvironment)
        {
            task.WithEnv(name, value);
        }

        var exitCode = 0;
        var reason = CoreExitReason.Natural;
        var pendingLogWrites = new List<Task>();
        var stdoutLines = new StdoutLineBuffer();
        var stdoutLock = new object();

        // #563.
        var stderrTail = new StderrTailBuffer();
        var stderrLock = new object();

        // 0026 / #1115.
        var stdoutTail = new StderrTailBuffer();

        // #1089: the terminal-success signal. The adapter (Adapter Isolation) owns what its vendor's
        // "I finished, status success" line looks like; here we only invoke that predicate on each
        // complete stdout line and latch the flag. Combined with OnStdoutLine into one sink so a line is
        // decoded once, and non-null whenever EITHER a progress callback OR a detector is present -- so
        // detection works on the dispatch path even when nothing consumes progress. Mutated on aer-core's
        // single callback thread under stdoutLock (below); read after the post-run Flush, which takes the
        // same lock, so the latch is visible.
        var terminalSuccessObserved = false;
        var detectsTerminalSuccess = target.DetectsTerminalSuccess;
        Action<string>? stdoutLineSink = target.OnStdoutLine;
        if (detectsTerminalSuccess is not null)
        {
            var innerProgress = target.OnStdoutLine;
            stdoutLineSink = line =>
            {
                innerProgress?.Invoke(line);
                if (!terminalSuccessObserved && detectsTerminalSuccess(line))
                {
                    terminalSuccessObserved = true;
                }
            };
        }

        ExecutionStreamLogger? streamLogger = pathVariables.TryGetValue("AER_OUTPUT_DIR", out var outputDir)
            ? new ExecutionStreamLogger(outputDir)
            : null;

        // #887 stage 2: a deterministic command step's stdout IS its declared artifact. Resolved
        // once here, not per chunk; per-chunk open-append-flush matches what
        // ExecutionStreamLogger already does for the stream logs. The lock is insurance against
        // a future second writer, NOT against concurrent chunks -- aer-core's event pump invokes
        // the callback synchronously on one thread (its own remark below on the decode says the
        // same), so chunk appends are already serialized and ordered.
        //
        // Created EAGERLY, before dispatch: a well-behaved command whose success case is empty
        // stdout (an empty `git diff`, a no-match grep) produces zero chunks, and a lazily
        // created file would then never exist -- ContractValidator would fail a correct run
        // (#887 review, medium). Same create-regardless-of-content guarantee git's own
        // `--output` gives CaptureWorkerAdapter.
        var stdoutArtifactPath = target.StdoutArtifactName is not null && outputDir is not null
            ? Path.Combine(outputDir, target.StdoutArtifactName)
            : null;
        var stdoutArtifactLock = new object();
        if (stdoutArtifactPath is not null)
        {
            Directory.CreateDirectory(outputDir!);
            using var created = new FileStream(stdoutArtifactPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        }

        task.EventRaised += (_, e) =>
        {
            switch (e.Kind)
            {
                case AerTaskEventKind.Started:
                    // CancellationToken.None, not cancellationToken: a cancellation firing is
                    // exactly what makes this record worth having (§7, §9's crash clause depends on
                    // Started actually landing before a cancel/timeout/host-stop can be attributed
                    // to it), so recording it must not itself be cancellable by that same signal —
                    // the same reasoning DispatchAndRecordOutcomeAsync's outcome append already
                    // applies to its own append.
                    pendingLogWrites.Add(coreEventLogWriter.AppendAsync(
                        new CoreEvent.ExecutionStarted(request.ExecutionId, e.Pid), CancellationToken.None));
                    break;

                case AerTaskEventKind.StdoutChunk:
                    if (e.Data is { Length: > 0 })
                    {
                        streamLogger?.AppendStdout(e.Data);
                        if (stdoutArtifactPath is not null)
                        {
                            lock (stdoutArtifactLock)
                            {
                                Directory.CreateDirectory(outputDir!);
                                using var fs = new FileStream(stdoutArtifactPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                                fs.Write(e.Data, 0, e.Data.Length);
                                fs.Flush();
                            }
                        }
                        lock (stdoutLock)
                        {
                            stdoutTail.Append(e.Data);
                            if (stdoutLineSink is not null)
                            {
                                // The decode is inside the lock, unlike the stateless GetString it replaces:
                                // the buffer now carries decoder state between chunks, so two callbacks
                                // decoding concurrently would interleave into one another's partial
                                // sequences. The lock was already here for the line buffer; the decode joins
                                // it rather than sitting beside it.
                                stdoutLines.Append(e.Data, stdoutLineSink);
                            }
                        }
                    }
                    break;

                case AerTaskEventKind.StderrChunk:
                    if (e.Data is { Length: > 0 })
                    {
                        streamLogger?.AppendStderr(e.Data);
                        lock (stderrLock)
                        {
                            stderrTail.Append(e.Data);
                        }
                    }
                    break;

                case AerTaskEventKind.Exited:
                    streamLogger?.MarkTerminal();
                    exitCode = e.ExitCode;
                    reason = ToCoreExitReason(e.ExitReason);
                    string? capturedStderrTail;
                    lock (stderrLock)
                    {
                        capturedStderrTail = stderrTail.ToTailOrNull();
                    }
                    pendingLogWrites.Add(coreEventLogWriter.AppendAsync(
                        new CoreEvent.ExecutionExited(request.ExecutionId, e.ExitCode, reason, capturedStderrTail), CancellationToken.None));
                    break;
            }
        };

        try
        {
            // Dispatch(Exited) above has already run by the time RunAsync's Task completes (native
            // callbacks fire synchronously inside aer_task_run, which returns before RunAsync's
            // wrapping Task.Run does), so exitCode/reason are already set here on the natural path.
            await task.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AerTimeoutException)
        {
            reason = CoreExitReason.TimedOut;
        }
        catch (AerCancelException)
        {
            reason = CoreExitReason.CancelRequested;
        }
        finally
        {
            streamLogger?.MarkTerminal();
        }

        await Task.WhenAll(pendingLogWrites).ConfigureAwait(false);

        bool terminalSuccessLatched;
        string? capturedStdoutTail;
        lock (stdoutLock)
        {
            if (stdoutLineSink is not null)
            {
                stdoutLines.Flush(stdoutLineSink);
            }

            // Read under the same lock the sink mutates, and AFTER Flush drains the last buffered line --
            // a terminal `result` arriving in the final chunk is only latched once Flush runs it.
            terminalSuccessLatched = terminalSuccessObserved;
            capturedStdoutTail = stdoutTail.ToTailOrNull();
        }

        string? capturedStderr;
        lock (stderrLock)
        {
            capturedStderr = stderrTail.ToTailOrNull();
        }

        return new CoreDispatchResult(exitCode, reason, capturedStderr, terminalSuccessLatched, capturedStdoutTail);

    }


    private static CoreExitReason ToCoreExitReason(AerExitReason reason) => reason switch
    {
        AerExitReason.Natural => CoreExitReason.Natural,
        AerExitReason.TimedOut => CoreExitReason.TimedOut,
        AerExitReason.CancelRequested => CoreExitReason.CancelRequested,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown AerExitReason."),
    };

    /// <summary>
    /// The one home of the placeholder token grammar (#713): <c>%NAME%</c>, <c>${NAME}</c>, or
    /// <c>$NAME</c> where the name ends at the first non-identifier character. A name that is not
    /// an AER-computed variable stays literal — this expands AER's own placeholders, it is not a
    /// shell. <c>Aer.Adapters.WorkerEnvironmentReference</c> is where a reference is
    /// <em>written</em>; this is where every reference is <em>expanded</em>, and no other layer
    /// expands one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>$NAME</c> form previously had no boundary — a bare <c>Replace</c> — so any longer
    /// word beginning with a variable's name got the value spliced in mid-word
    /// (<c>$AER_OUTPUT_DIRECTORY</c> became the path plus <c>ECTORY</c>), and <c>${NAME}</c>, the
    /// ordinary way to disambiguate, was not recognised at all. One pass over the string rather
    /// than one pass per variable also means a substituted <em>value</em> is never itself
    /// re-scanned, and the boundary makes longest-name-first ordering unnecessary: a name that is
    /// a prefix of a longer identifier simply does not match it.
    /// </para>
    /// <para>
    /// Three edges the grammar sentence alone does not decide, found by this change's reviewer and
    /// stated here so they are decided once. There is <b>no escape</b>: a known name always
    /// expands, in every form, and only unknown names stay literal. An unknown <c>%…%</c> pair
    /// consumes its closing <c>%</c>, so in the pathological <c>%A%AER_OUTPUT_DIR%</c> the unknown
    /// <c>%A%</c> also keeps the known name from expanding — write <c>%%</c> pairs or reorder;
    /// AER's own emissions never produce that shape. And <c>\w</c> is Unicode-wide where AER's
    /// computed names are ASCII, so a non-ASCII letter after a known name reads as more identifier
    /// and the token stays literal — an under-expansion, never a mis-expansion.
    /// </para>
    /// </remarks>
    private static readonly System.Text.RegularExpressions.Regex VariableToken = new(
        @"%(?<name>\w+)%|\$\{(?<name>\w+)\}|\$(?<name>\w+)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Expands every <see cref="VariableToken"/> — the grammar and its edges live there.</summary>
    private static string ExpandVariables(string arg, Dictionary<string, string> vars) =>
        VariableToken.Replace(
            arg,
            match => vars.TryGetValue(match.Groups["name"].Value, out var value) ? value : match.Value);

    /// <summary>
    /// Expands a seed file's CONTENT against forward-slashed variable values — distinct from the seed
    /// PATH, which expands natively. AER-computed variables are absolute paths, and on Windows their
    /// raw value carries backslashes (<c>C:\Users\...</c>). A seed body is frequently JSON (agy's
    /// <c>settings.json</c> is the first user, #1084), where a substituted <c>C:\U…</c> is an invalid
    /// string escape that voids the whole file — so an allow-rule inside it would silently never load
    /// and the write it was meant to permit would still be denied. Forward slashes are valid JSON, a
    /// path Windows still accepts, and the exact form agy normalises both rule and target to before
    /// comparing, so the rule still matches. The path stays native because
    /// <see cref="Directory.CreateDirectory(string)"/> and <see cref="File.WriteAllTextAsync(string,string?,CancellationToken)"/>
    /// want the platform separator.
    /// </summary>
    internal static string RenderSeedContent(string content, Dictionary<string, string> pathVariables) =>
        ExpandVariables(content, pathVariables.ToDictionary(kv => kv.Key, kv => kv.Value.Replace('\\', '/')));
}
