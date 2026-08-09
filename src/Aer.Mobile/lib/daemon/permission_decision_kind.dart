/// Dart port of `Aer.Adapters.PermissionDecisionKind`'s string constants (0022's ladder) — the
/// values posted as `decisionKind` to `/api/rooms/permissions/answer`. These MUST equal the C#
/// constants exactly, or the daemon fails closed (`PermissionGateTool.BuildAnswerResult` decides
/// release-vs-refuse on `decisionKind.StartsWith("Allow")`).
///
/// `AllowCommandAnyRoom` ("any this-command in ANY room") is deliberately NOT ported: decision 0052
/// holds that rung until a project-scoped store exists, so no phone affordance may offer or emit it.
library;

class PermissionDecisionKind {
  /// Just this once — nothing persists; only the one held call is released.
  static const allowOnce = 'AllowOnce';

  /// Any this command in this room — persists a room-scoped shell-command-pattern grant.
  static const allowCommandInRoom = 'AllowCommandInRoom';

  /// Any command in this room — persists the room's unscoped shell-run grant.
  static const allowRoom = 'AllowRoom';

  /// Never / always deny this command — the standing refusal.
  static const denyAlways = 'DenyAlways';

  /// Deny once — nothing persists.
  static const deny = 'Deny';
}
