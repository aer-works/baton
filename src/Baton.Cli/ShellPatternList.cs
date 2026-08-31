namespace Baton.Cli;

/// <summary>
/// What a <c>PreToolUse</c> hook makes of <c>BATON_HOOK_SHELL_PATTERNS</c> (#659).
/// </summary>
public sealed record ShellPatternList(ShellPatternListStatus Status, IReadOnlyList<string> Patterns)
{
    /// <summary>
    /// Splits a vendor-tagged value, judged from the point of view of <paramref name="ownVendorTag"/>.
    /// </summary>
    public static ShellPatternList Parse(string? raw, string ownVendorTag)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownVendorTag);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ShellPatternList(ShellPatternListStatus.Absent, Array.Empty<string>());
        }

        var separator = raw.IndexOf(':');
        if (separator < 0 || !raw[..separator].Trim().Equals(ownVendorTag, StringComparison.Ordinal))
        {
            return new ShellPatternList(ShellPatternListStatus.WrongVendor, Array.Empty<string>());
        }

        var patterns = raw[(separator + 1)..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new ShellPatternList(ShellPatternListStatus.Present, patterns);
    }
}

/// <summary>Which case a parsed <c>BATON_HOOK_SHELL_PATTERNS</c> falls into.</summary>
public enum ShellPatternListStatus
{
    /// <summary>AER said what shell patterns apply. An empty <see cref="ShellPatternList.Patterns"/> means unscoped.</summary>
    Present,

    /// <summary>
    /// Nothing arrived, so this gate cannot know what patterns apply. The enum only reports the
    /// fact; the CONSUMER decides what it means -- <c>AgyHookCheckCommand</c> reads this as a deny,
    /// but <c>HookCheckCommand.Decide</c> (claude) deliberately reads it as an unscoped-shell no-op
    /// instead (#1459) -- see that method's own remarks for why.
    /// </summary>
    Absent,

    /// <summary>
    /// Another vendor's list, whose patterns this gate cannot judge. Same caveat as
    /// <see cref="Absent"/>: the enum only reports; claude's <c>HookCheckCommand</c> reads this as a
    /// no-op too, not a deny.
    /// </summary>
    WrongVendor,
}
