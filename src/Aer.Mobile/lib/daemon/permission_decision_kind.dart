/// Dart port of `Aer.Adapters.PermissionDecisionKind`'s string constants (0022's ladder) — the
/// values posted as `decisionKind` to `/api/rooms/permissions/answer`. These MUST equal the C#
/// constants exactly, or the daemon fails closed (`PermissionGateTool.BuildAnswerResult` decides
/// release-vs-refuse on `decisionKind.StartsWith("Allow")`).
///
/// `AllowCommandAnyRoom` ("any this-command in ANY room") is deliberately NOT ported: decision 0052
/// holds that rung until a project-scoped store exists, so no phone affordance may offer or emit it.
library;

class PermissionDecisionKind {
  // Wire strings only. Each rung's semantics are documented once, canonically, on the matching C#
  // constant — see `Aer.Adapters/PermissionDecisionKind.cs`; not restated here (record-once).
  static const allowOnce = 'AllowOnce';
  static const allowCommandInRoom = 'AllowCommandInRoom';
  static const allowRoom = 'AllowRoom';
  static const denyAlways = 'DenyAlways';
  static const deny = 'Deny';
}
