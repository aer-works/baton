using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Accounting;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// <c>baton ledger [&lt;room-dir&gt;] [filters] [--format text|json|csv] [--drill]</c> (#1849 phase B,
/// operator ruling 2026-09-05): the room and fleet readings of the repository-keyed cost ledger.
/// </summary>
/// <remarks>
/// <para>
/// <b>This command formats; it does not sum.</b> Every number below comes from
/// <see cref="LedgerRollup"/> — the one accounting projection, whose own remarks state what that buys
/// and how a room reading relates to a fleet one — so the two readings and all three formats are
/// arithmetically incapable of disagreeing.
/// </para>
/// <para>
/// <b>Why the order is fixed</b> (spec/baton.md §7 states what it is): a reader who stops after the
/// first screen has read the per-vendor answer, which is the one #1849 says is comparable across
/// vendors. The all-vendor figure comes last because it is the one that must be read together with
/// its label.
/// </para>
/// <para>
/// Not a <see cref="CommandResult"/>/<see cref="FlowStateReporter"/> command, for the same reason
/// <see cref="LedgerCommand"/> is not: there is no workflow pump here to report on.
/// </para>
/// </remarks>
public static class LedgerViewCommand
{
    /// <summary>
    /// <c>WhenWritingNull</c>, matching the ledger file's own serialization: an absent field is absent
    /// in the view too, never <c>null</c> and never <c>0</c>. The record's per-property attributes
    /// already say this for <see cref="CostLedgerEntry"/>; this repeats it for the rollup types so a
    /// filter nobody set is simply not in the echoed <c>query</c>.
    /// </summary>
    private static readonly JsonSerializerOptions ViewSerializerOptions =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false };

    public static async Task<int> ExecuteAsync(
        LedgerViewOptions options,
        TextWriter output,
        string? ledgerFilePathOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        if (options.Help)
        {
            Write(output, LedgerViewOptionsParser.Usage);
            foreach (var line in LedgerViewOptionsParser.HelpLines)
            {
                Write(output, line);
            }

            return 0;
        }

        var ledgerFilePath = ledgerFilePathOverride
            ?? await ResolveLedgerFilePathAsync(options, cancellationToken).ConfigureAwait(false);

        var entries = await CostLedgerStore.ReadAllAsync(ledgerFilePath, cancellationToken).ConfigureAwait(false);

        // CSV is rows OR NOTHING -- it has no subtotal section for --drill to be an alternative to, so
        // gating its rows on that flag would make the obvious `--format csv > out.csv` write an empty
        // export and exit 0. --drill selects the row section of the two formats that have one.
        var rollup = LedgerRollup.Build(
            entries, options.Query, options.Drill || options.Format == LedgerOutputFormat.Csv);

        switch (options.Format)
        {
            case LedgerOutputFormat.Json:
                Write(output, JsonSerializer.Serialize(rollup, ViewSerializerOptions));
                break;
            case LedgerOutputFormat.Csv:
                LedgerCsv.Write(output, rollup.Rows ?? []);
                break;
            default:
                WriteText(output, rollup, ledgerFilePath);
                break;
        }

        return 0;
    }

    /// <summary>
    /// Which repository's ledger file this reading is over.
    /// <list type="bullet">
    /// <item><c>--repo-identity</c> names it explicitly, in either spelling an operator has to hand: the
    /// file's own stem (what <c>ls ~/.baton/ledger</c> shows) when a file by that name exists, else the
    /// canonical identity a row records (<c>github.com/owner/repo</c>), slugged the way the writer
    /// slugged it. Case-folded first, because <c>RepositoryIdentity.From</c> case-folds before hashing
    /// and an unfolded key would digest to a different — and empty — file.</item>
    /// <item>With a <c>&lt;room-dir&gt;</c> and no explicit key, the ROOM's own repository, off its
    /// registry entry — not the working directory's. A room is read from wherever the operator happens
    /// to be standing, including outside any repository at all.</item>
    /// <item>Otherwise the repository the operator is standing in.</item>
    /// </list>
    /// A directory with no repository identity is a <see cref="CliArgumentException"/> naming
    /// <c>--repo-identity</c>, rather than an empty rollup: "no rows" and "you asked the wrong
    /// question" must not print the same thing.
    /// </summary>
    private static async Task<string> ResolveLedgerFilePathAsync(
        LedgerViewOptions options, CancellationToken cancellationToken)
    {
        if (options.RepositoryIdentityKey is { Length: > 0 } key)
        {
            var trimmed = key.Trim();
            var byFileStem = Path.Combine(BatonPaths.Root, BatonPaths.CostLedgerDirectoryName, $"{trimmed}.jsonl");
            return File.Exists(byFileStem)
                ? byFileStem
                : BatonPaths.CostLedgerFile(RepositoryIdentity.FileSlugFor(trimmed.ToLowerInvariant()));
        }

        var repository = options.RoomDirectoryPath is { Length: > 0 } roomDirectoryPath
            ? await RepositoryIdentityResolver.TryResolveForRoomAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false)
            : await RepositoryIdentityResolver.TryResolveAsync(Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);

        if (repository is null)
        {
            throw new CliArgumentException(
                "No repository identity here: git reported neither an 'origin' remote nor a repository for "
                + (options.RoomDirectoryPath is { Length: > 0 } room
                    ? $"room '{room}' (its recorded project root, or this working directory when it has no registry entry). "
                    : $"'{Environment.CurrentDirectory}'. ")
                + "The cost ledger is keyed by repository, so there is no file to read. Name one explicitly.",
                "baton ledger --repo-identity github.com/owner/repo");
        }

        return BatonPaths.CostLedgerFile(repository.FileSlug);
    }

    private static void WriteText(TextWriter output, LedgerRollup rollup, string ledgerFilePath)
    {
        var query = rollup.Query;

        Write(output, $"Cost ledger: {ledgerFilePath}");
        if (!File.Exists(ledgerFilePath))
        {
            // Said out loud: an empty rollup from a file that was never written looks exactly like a
            // repository that spent nothing, and only this line tells the two apart.
            Write(output, "  (no ledger file yet for this repository -- nothing has settled here)");
        }

        if (query.Room is { Length: > 0 } room)
        {
            Write(output, $"Room: {room}");
        }

        Write(output, $"Window: {DescribeWindow(query)}");

        var facets = DescribeFacets(query);
        if (facets is not null)
        {
            Write(output, $"Filters: {facets}");
        }

        Write(
            output,
            $"Rows: {Number(rollup.Total.Attempts)} matched"
                + (query.UndatedExcluded > 0
                    ? $", {Number(query.UndatedExcluded)} excluded by the window for having no endedAt"
                    : string.Empty));
        // Same disclosure as the missing-file line above, one level down: a room key no row carries
        // reads exactly like a room that spent nothing. A mistyped path gets here, and so does a room
        // that was never registered -- the identity probe then falls back to the working directory's
        // repository, which is a real ledger with none of that room's rows in it.
        if (query.Room is { Length: > 0 } filteredRoom && rollup.Total.Attempts == 0)
        {
            Write(
                output,
                $"  (no row in this ledger carries room '{filteredRoom}' -- either nothing has settled "
                    + "there, or that room's work belongs to a different repository's ledger)");
        }

        Write(output, string.Empty);

        // Per-vendor FIRST -- the comparable answer. The all-vendor line follows, never precedes.
        foreach (var vendor in rollup.Vendors)
        {
            WriteSubtotal(output, vendor.Vendor ?? LedgerRollup.UnknownVendor, vendor);
            Write(output, string.Empty);
        }

        WriteSubtotal(output, "all vendors", rollup.Total);
        Write(
            output,
            "  Both figures are ESTIMATES -- API list-price equivalent and modelled plan-meter cost. "
                + "Neither is an invoice, subscription spend, or a quota reading.");

        if (rollup.Rows is not { } rows)
        {
            return;
        }

        Write(output, string.Empty);
        Write(output, $"Rows contributing to the subtotals above ({Number(rows.Count)}):");
        foreach (var row in rows)
        {
            Write(output, $"  {DescribeRow(row)}");
        }
    }

    private static void WriteSubtotal(TextWriter output, string label, LedgerSubtotal subtotal)
    {
        var completeness = new List<string>();
        if (subtotal.Partial > 0)
        {
            completeness.Add($"{Number(subtotal.Partial)} partial");
        }

        if (subtotal.Unread > 0)
        {
            completeness.Add($"{Number(subtotal.Unread)} with no usage read");
        }

        Write(
            output,
            $"{label} -- {Number(subtotal.Attempts)} attempt(s)"
                + (completeness.Count > 0 ? $" ({string.Join(", ", completeness)})" : string.Empty));
        Write(
            output,
            "  tokens: "
                + $"in {Tokens(subtotal.TokensIn)}, out {Tokens(subtotal.TokensOut)}, "
                + $"cache-read {Tokens(subtotal.CacheReadTokens)}, cache-creation {Tokens(subtotal.CacheCreationTokens)}, "
                + $"thinking {Tokens(subtotal.ThinkingTokens)}");
        Write(
            output,
            $"  API-equivalent estimate: {Money(subtotal.ApiEquivalentUsd)} "
                + $"(priced: {Number(subtotal.ApiEquivalentPriced)}, unpriced: {Number(subtotal.ApiEquivalentUnpriced)})");
        Write(
            output,
            $"  plan-meter estimate: {Money(subtotal.PlanMeterEstimateUsd)} "
                + $"(priced: {Number(subtotal.PlanMeterPriced)}, unpriced: {Number(subtotal.PlanMeterUnpriced)})");
    }

    private static string DescribeRow(CostLedgerEntry row)
    {
        var builder = new StringBuilder();
        builder.Append(row.EndedAt is { } endedAt ? Instant(endedAt) : "(no endedAt)".PadRight(20));
        builder.Append("  ").Append(row.Adapter ?? LedgerRollup.UnknownVendor);
        builder.Append("  ").Append(row.Model ?? "-");
        builder.Append("  ").Append(row.Role ?? "-");
        builder.Append("  ").Append(row.Outcome ?? "-");
        builder.Append("  in ").Append(Tokens(row.TokensIn));
        builder.Append(" out ").Append(Tokens(row.TokensOut));
        builder.Append(" cache-read ").Append(Tokens(row.CacheReadTokens));
        builder.Append(" cache-creation ").Append(Tokens(row.CacheCreationTokens));
        builder.Append(" thinking ").Append(Tokens(row.ThinkingTokens));
        builder.Append("  api ").Append(Money(row.ApiEquivalentUsd));
        builder.Append(" plan ").Append(Money(row.PlanMeterEstimateUsd));
        builder.Append("  ").Append(row.Execution ?? "(no execution id)");
        return builder.ToString();
    }

    private static string DescribeWindow(LedgerQuery query) => (query.Since, query.Until) switch
    {
        (null, null) => "everything in the file (no --since/--until)",
        ({ } since, null) => $"endedAt >= {Instant(since)} (inclusive)",
        (null, { } until) => $"endedAt < {Instant(until)} (exclusive)",
        ({ } since, { } until) => $"endedAt >= {Instant(since)} (inclusive) and < {Instant(until)} (exclusive)",
    };

    private static string? DescribeFacets(LedgerQuery query)
    {
        var facets = new List<string>();
        void Add(string name, string? value)
        {
            if (value is { Length: > 0 })
            {
                facets.Add($"{name}={value}");
            }
        }

        Add("vendor", query.Vendor);
        Add("model", query.Model);
        Add("role", query.Role);
        Add("project", query.Project);
        Add("outcome", query.Outcome);
        Add("workflow", query.Workflow);
        Add("pr", query.PullRequest);
        Add("issue", query.Issue);
        if (query.SourceKind is { } kind)
        {
            Add("source-kind", JsonSerializer.Serialize(kind).Trim('"'));
        }

        return facets.Count == 0 ? null : string.Join(", ", facets);
    }

    /// <summary>A dimension no row reported prints as <c>-</c>, never <c>0</c> — the subtotal's own absence doctrine, kept in the rendering.</summary>
    private static string Tokens(long? value) =>
        value is { } present ? present.ToString(CultureInfo.InvariantCulture) : "-";

    private static string Money(decimal? value) =>
        value is { } present ? "$" + present.ToString("0.######", CultureInfo.InvariantCulture) : "-";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Rendered in UTC, the frame the ledger records in — an instant that arrived as local time is converted, not relabelled.</summary>
    private static string Instant(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// LF explicitly, not <see cref="TextWriter.WriteLine()"/>: this repo runs on Windows, and the
    /// same query over the same file has to produce the same BYTES wherever it is compared (#1849's
    /// determinism criterion), which a host-dependent line ending would break.
    /// </summary>
    private static void Write(TextWriter output, string line)
    {
        output.Write(line);
        output.Write('\n');
    }
}
