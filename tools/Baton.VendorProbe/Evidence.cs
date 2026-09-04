namespace Baton.VendorProbe;

/// <summary>
/// Where a claim about a vendor CLI came from. The distinction exists because six claims in one
/// sitting were wrong in the same way — a single surface was checked, nothing was found, and the
/// capability was recorded as absent.
/// </summary>
public enum Evidence
{
    /// <summary>A run demonstrated it. The only class that may be stated as a plain fact.</summary>
    Observed,

    /// <summary>Found in help text, the shipped binary, or vendor docs — never executed.</summary>
    Inspected,

    /// <summary>Looked for and not found. Invalid without the list of surfaces consulted.</summary>
    NotFound,
}

/// <summary>
/// A place a capability can hide. A CLI is not one surface, and treating it as one is what produced
/// every wrong row this suite exists to prevent — most memorably <c>/usage</c>, which is absent from
/// <c>--help</c> and from the subcommand list, and works perfectly as a slash command.
/// </summary>
public static class Surfaces
{
    public const string Help = "--help";
    public const string Subcommands = "subcommand list";
    public const string SlashCommand = "in-session slash command";
    public const string StructuredOutput = "structured output stream";
    public const string AppServer = "app-server JSON-RPC";
    public const string ExecHelp = "exec --help";
    public const string ConfigFile = "config file";
    public const string LocalState = "local state directory";
    public const string StdErr = "stderr";

    /// <summary>
    /// The flag was passed and its exit code compared against one for a flag that certainly does not
    /// exist. Without that control an exit code is not evidence, because "accepted" and "silently
    /// ignored" look identical.
    /// </summary>
    public const string ControlFlag = "flag acceptance vs. a control flag";
    public const string Binary = "shipped binary (strings)";

    /// <summary>
    /// A server the CLI starts on localhost for the duration of a run. Easy to miss entirely, since
    /// nothing on stdout mentions it — <c>agy</c> announces its gRPC and HTTP ports only in the log.
    /// </summary>
    public const string LocalServer = "local RPC server";
    public const string VendorDocs = "vendor documentation";
}

/// <summary>
/// One capability, for one vendor, with the evidence behind it.
/// </summary>
/// <param name="Capability">What was being established, e.g. "plan usage &amp; reset".</param>
/// <param name="Vendor">The CLI, e.g. <c>claude</c> or <c>agy</c>.</param>
/// <param name="Evidence">How the claim was established.</param>
/// <param name="Value">The finding — a flag, a value list, a quoted line. Null for a negative.</param>
/// <param name="SurfacesConsulted">Every surface actually looked at. Mandatory for a negative.</param>
/// <param name="Detail">What the run did, or why the result is what it is.</param>
/// <param name="VendorVersion">The CLI version this ran against; results expire when it moves.</param>
public sealed record Finding(
    string Capability,
    string Vendor,
    Evidence Evidence,
    string? Value,
    IReadOnlyList<string> SurfacesConsulted,
    string Detail,
    string? VendorVersion)
{
    /// <summary>
    /// The rule this whole suite exists to enforce, made structural rather than remembered:
    /// <b>a negative claim is invalid without the list of surfaces it was established on.</b>
    /// </summary>
    /// <remarks>
    /// Every wrong claim this suite was built after would have failed here. "agy has no structured
    /// output" was established from <c>--help</c> alone; "neither vendor reports quota" was
    /// established from <c>--help</c> and the subcommand list, while the answer sat in the slash
    /// commands. Requiring the list does not make a probe thorough — it makes an incomplete one
    /// <em>visibly</em> incomplete, which is the part that failed.
    /// </remarks>
    public static Finding Absent(
        string capability, string vendor, IReadOnlyList<string> surfacesConsulted, string detail, string? version)
    {
        if (surfacesConsulted.Count == 0)
        {
            throw new ArgumentException(
                $"A NotFound finding for '{capability}' on '{vendor}' names no surfaces. "
                + "A negative claim without its surface list is exactly the mistake this type exists "
                + "to prevent — say where you looked, or do not record the finding.",
                nameof(surfacesConsulted));
        }

        return new Finding(capability, vendor, Evidence.NotFound, Value: null, surfacesConsulted, detail, version);
    }

    public static Finding Seen(
        string capability, string vendor, string value, IReadOnlyList<string> surfaces, string detail, string? version)
        => new(capability, vendor, Evidence.Observed, value, surfaces, detail, version);

    public static Finding Read(
        string capability, string vendor, string value, IReadOnlyList<string> surfaces, string detail, string? version)
        => new(capability, vendor, Evidence.Inspected, value, surfaces, detail, version);

    /// <summary>How this reads in the generated matrix — never a bare "absent".</summary>
    public string Rendered() => Evidence switch
    {
        Evidence.Observed => $"**{Value}**",
        Evidence.Inspected => $"{Value} *(inspected, not run)*",
        _ => $"not found on: {string.Join(", ", SurfacesConsulted)}",
    };
}
