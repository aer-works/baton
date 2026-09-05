using Baton.Accounting;

namespace Baton.Cli;

/// <summary>How <c>baton ledger</c> renders a rollup. <see cref="Json"/> is the machine contract #1746 and #1848 read.</summary>
public enum LedgerOutputFormat
{
    Text,
    Json,
    Csv,
}

/// <summary>
/// Parsed arguments for the reading form of <c>baton ledger</c> (#1849 phase B) — see
/// <see cref="LedgerViewCommand"/> for what it does and <see cref="LedgerViewOptionsParser"/> for the
/// grammar. <see cref="LedgerCommand"/> owns the <c>--rebuild</c> form, which shares the verb with
/// this one and nothing else — <see cref="LedgerViewOptionsParser.HelpLines"/> is where an operator
/// is told so.
/// </summary>
/// <param name="RoomDirectoryPath">
/// The room this view is scoped to, already resolved to a <c>BatonPaths.RecordKey</c>, or
/// <see langword="null"/> for the fleet view. Also carried on <paramref name="Query"/>: a room view is
/// literally the fleet view with that facet set, and this field exists only so the command can resolve
/// the room's own repository identity rather than the working directory's.
/// </param>
/// <param name="RepositoryIdentityKey">
/// <c>--repo-identity</c> — which repository's ledger file to read, when it is not the one the
/// operator is standing in. <see cref="LedgerViewCommand"/> states how a key resolves to a path.
/// </param>
/// <param name="Drill">
/// <c>--drill</c>: carry the contributing rows as well as the subtotals they roll into. Always AFTER
/// the subtotals in every format that has an order.
/// </param>
/// <param name="Help">
/// <c>--help</c>: print the grammar and the two things a reader's prior gets wrong (which ledger this
/// reads, and which instant the time filter is on), then exit 0 without reading anything.
/// </param>
public sealed record LedgerViewOptions(
    string? RoomDirectoryPath,
    LedgerQuery Query,
    string? RepositoryIdentityKey = null,
    LedgerOutputFormat Format = LedgerOutputFormat.Text,
    bool Drill = false,
    bool Help = false);
