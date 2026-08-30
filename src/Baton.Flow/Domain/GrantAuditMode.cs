using System.Text.Json.Serialization;

namespace Baton.Flow.Domain;

/// <summary>
/// Governs how a worker grant is enforced.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Enforced"/>: the vendor hook vetoes violations in the moment.
/// </para>
/// <para>
/// <see cref="AuditedNotEnforced"/>: the grant exceeds the role's intent because the vendor hook
/// cannot path-scope it (#659), and AER audits after the run instead (#901).
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GrantAuditMode
{
    /// <summary>The vendor hook vetoes violations in the moment.</summary>
    Enforced,

    /// <summary>
    /// The grant exceeds the role's intent because the vendor hook cannot path-scope it (#659), and AER audits after the run instead (#901).
    /// </summary>
    AuditedNotEnforced,
}
