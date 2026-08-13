/// Aer.Daemon's RoomProjection, as pushed over /api/ws and returned by /api/rooms/open.
///
/// REST payloads are camelCase; WS payloads are PascalCase (Aer.Daemon's own
/// `SendStateAsync` builds a bare JsonSerializerOptions with no naming policy — see
/// src/Aer.Daemon/Program.cs). Every fromJson below reads through [caseInsensitive],
/// which normalizes both to the same lowercase keys, rather than guessing casing per field.
library;

Map<String, dynamic> caseInsensitive(Map<String, dynamic> json) =>
    json.map((key, value) => MapEntry(key.toLowerCase(), value));

/// One step's static definition, from RoomProjection.Snapshot.Steps.
class StepDefinition {
  final String stepId;
  final String worker;
  final List<String> supersedeTargets;

  StepDefinition({required this.stepId, required this.worker, required this.supersedeTargets});

  factory StepDefinition.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    final pausePoint = j['pausepoint'] as Map<String, dynamic>?; // vocabulary-ok: payload field key
    final targets = pausePoint == null
        ? <String>[]
        : ((caseInsensitive(pausePoint)['supersedetargets'] as List<dynamic>?) ?? [])
            .map((t) => t.toString())
            .toList();
    return StepDefinition(
      stepId: j['stepid'].toString(),
      worker: j['worker'].toString(),
      supersedeTargets: targets,
    );
  }
}

/// One step's live status, from RoomProjection.State.Steps.
class WorkflowStepState {
  final String stepId;
  final String status;
  final String? latestExecutionId;
  final String? latestFailureReason;
  final String? latestFailureClassification;

  WorkflowStepState({
    required this.stepId,
    required this.status,
    required this.latestExecutionId,
    this.latestFailureReason,
    this.latestFailureClassification,
  });

  bool get isPaused => status == 'Paused';
  bool get isFailed => status == 'Failed';

  factory WorkflowStepState.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    return WorkflowStepState(
      stepId: j['stepid'].toString(),
      status: j['status'].toString(),
      latestExecutionId: j['latestexecutionid']?.toString(),
      latestFailureReason: j['latestfailurereason']?.toString(),
      latestFailureClassification: j['latestfailureclassification']?.toString(),
    );
  }
}

/// One execution's artifact-directory contents, from RoomProjection.Lineage.Executions.
class ExecutionArtifacts {
  final String executionId;
  final String worker;
  final List<String> outputFiles;

  ExecutionArtifacts({required this.executionId, required this.worker, required this.outputFiles});

  factory ExecutionArtifacts.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    return ExecutionArtifacts(
      executionId: j['executionid'].toString(),
      worker: j['worker'].toString(),
      outputFiles: ((j['outputfiles'] as List<dynamic>?) ?? []).map((f) => f.toString()).toList(),
    );
  }
}

/// The runtime conversational permission gate a worker is currently blocked on (0022, #390's mobile
/// phase) — the Dart counterpart of Aer.Flow.Projection.PendingPermission, carried as a top-level
/// sibling of Snapshot/State/Lineage on the daemon's wire-level RoomProjection (Aer.Ui.Core's
/// RoomProjection.cs), not nested under `state`. Null when no gate is open (the common case).
class PendingPermission {
  final String permissionRequestId;
  final String workerId;
  final String vendorTag;
  final String toolName;
  final String toolInputJson;
  final String category;
  final DateTime askedAt;

  PendingPermission({
    required this.permissionRequestId,
    required this.workerId,
    required this.vendorTag,
    required this.toolName,
    required this.toolInputJson,
    required this.category,
    required this.askedAt,
  });

  factory PendingPermission.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    return PendingPermission(
      permissionRequestId: j['permissionrequestid'].toString(),
      workerId: j['workerid']?.toString() ?? '',
      vendorTag: j['vendortag']?.toString() ?? '',
      toolName: j['toolname']?.toString() ?? '',
      toolInputJson: j['toolinputjson']?.toString() ?? '',
      category: j['category']?.toString() ?? '',
      askedAt: DateTime.tryParse(j['askedat']?.toString() ?? '') ?? DateTime.now(),
    );
  }
}

/// Wire twin of the engine's PermissionAnswer (its doc in src/Aer.Flow/Projection is canonical).
class PermissionAnswer {
  final String permissionRequestId;
  final String toolName;
  final String category;
  final String decisionKind;
  final String? reason;
  final String deciderIdentity;
  final DateTime answeredAt;
  final bool wasRevoked;

  PermissionAnswer({
    required this.permissionRequestId,
    required this.toolName,
    required this.category,
    required this.decisionKind,
    this.reason,
    required this.deciderIdentity,
    required this.answeredAt,
    required this.wasRevoked,
  });

  factory PermissionAnswer.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    return PermissionAnswer(
      permissionRequestId: j['permissionrequestid']?.toString() ?? '',
      toolName: j['toolname']?.toString() ?? '',
      category: j['category']?.toString() ?? '',
      decisionKind: j['decisionkind']?.toString() ?? '',
      reason: j['reason']?.toString(),
      deciderIdentity: j['decideridentity']?.toString() ?? '',
      answeredAt: DateTime.tryParse(j['answeredat']?.toString() ?? '') ?? DateTime.now(),
      wasRevoked: j['wasrevoked'] == true,
    );
  }
}

/// Wire twin of the engine's DormancyTransition (#1178).
class DormancyTransition {
  final bool isEntered;
  final int consecutiveFailures;
  final String? detail;
  final String? clearedBy;
  final DateTime timestamp;

  DormancyTransition({
    required this.isEntered,
    required this.consecutiveFailures,
    this.detail,
    this.clearedBy,
    required this.timestamp,
  });

  factory DormancyTransition.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    return DormancyTransition(
      isEntered: j['isentered'] == true,
      consecutiveFailures: j['consecutivefailures'] is int
          ? j['consecutivefailures'] as int
          : int.tryParse(j['consecutivefailures']?.toString() ?? '0') ?? 0,
      detail: j['detail']?.toString(),
      clearedBy: j['clearedby']?.toString(),
      timestamp: DateTime.tryParse(j['timestamp']?.toString() ?? '') ?? DateTime.now(),
    );
  }
}

/// A projection Aer.Daemon pushes for one room directory. Aer.Daemon still has only one
/// "current" task server-side (RoomClient.CurrentRoomDirectoryPath) and broadcasts every
/// change to every connected WS client regardless of which directory it's for — but this app
/// filters incoming pushes against InboxScreen's own `_openDirectoryPath` before applying one
/// (fixed alongside issue #262's chat work; see `_connect`'s listener), so a different client
/// opening a different task no longer silently changes what this phone shows. directoryPath
/// comes from the DirectoryPath sibling property Aer.Daemon adds to the WS payload (M21 Phase 2,
/// #232) — it is not part of RoomProjection itself, since /api/rooms/decide and /api/rooms/cancel
/// need it and a WS-only client (this app) has no other way to learn it, and it's also this
/// filter's join key. sessionId is the same kind of sibling, added for the mobile chat UI so a
/// push that isn't self-started (seeded from another client, or picked from recent tasks) still
/// tells this phone it's looking at an interactive session and which id to fetch turns for.
class RoomProjection {
  final String? directoryPath;
  final String? sessionId;
  final String workflowTemplateId;
  final String status;
  final List<StepDefinition> stepDefinitions;
  final List<WorkflowStepState> steps;
  final List<ExecutionArtifacts> executions;
  final Map<String, String> workerAdapters;

  /// The runtime conversational permission gate (0022, #390's mobile phase), or null when no worker
  /// is blocked on one — see [PendingPermission]'s doc comment for where this sits on the wire.
  final PendingPermission? pendingPermission;

  /// History of answered or revoked runtime permissions (bounded to newest 50).
  final List<PermissionAnswer> permissionAnswers;

  /// History of turn host dormancy transitions (#1178).
  final List<DormancyTransition> dormancyTransitions;

  RoomProjection({
    required this.directoryPath,
    required this.sessionId,
    required this.workflowTemplateId,
    required this.status,
    required this.stepDefinitions,
    required this.steps,
    required this.executions,
    required this.workerAdapters,
    this.pendingPermission,
    this.permissionAnswers = const [],
    this.dormancyTransitions = const [],
  });

  bool get isDormant => dormancyTransitions.isNotEmpty && dormancyTransitions.last.isEntered;

  List<WorkflowStepState> get pausedSteps => steps.where((s) => s.isPaused).toList();

  StepDefinition? definitionFor(String stepId) =>
      stepDefinitions.where((d) => d.stepId == stepId).cast<StepDefinition?>().firstWhere((_) => true, orElse: () => null);

  ExecutionArtifacts? executionFor(String? executionId) => executionId == null
      ? null
      : executions.where((e) => e.executionId == executionId).cast<ExecutionArtifacts?>().firstWhere((_) => true, orElse: () => null);

  factory RoomProjection.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    final snapshot = caseInsensitive(j['snapshot'] as Map<String, dynamic>);
    final state = caseInsensitive(j['state'] as Map<String, dynamic>);
    final lineage = j['lineage'] == null ? <String, dynamic>{} : caseInsensitive(j['lineage'] as Map<String, dynamic>);

    final workerAdapters = <String, String>{};
    if (j['workeradapters'] is Map<String, dynamic>) {
      (j['workeradapters'] as Map<String, dynamic>).forEach((k, v) {
        if (v != null) workerAdapters[k] = v.toString();
      });
    }

    return RoomProjection(
      directoryPath: j['directorypath']?.toString(),
      sessionId: j['sessionid']?.toString(),
      workflowTemplateId: snapshot['workflowtemplateid'].toString(),
      status: state['status'].toString(),
      stepDefinitions:
          ((snapshot['steps'] as List<dynamic>?) ?? []).map((s) => StepDefinition.fromJson(s as Map<String, dynamic>)).toList(),
      steps: ((state['steps'] as List<dynamic>?) ?? []).map((s) => WorkflowStepState.fromJson(s as Map<String, dynamic>)).toList(),
      executions: ((lineage['executions'] as List<dynamic>?) ?? [])
          .map((e) => ExecutionArtifacts.fromJson(e as Map<String, dynamic>))
          .toList(),
      workerAdapters: workerAdapters,
      pendingPermission: j['pendingpermission'] == null
          ? null
          : PendingPermission.fromJson(j['pendingpermission'] as Map<String, dynamic>),
      permissionAnswers: ((j['permissionanswers'] as List<dynamic>?) ?? [])
          .map((a) => PermissionAnswer.fromJson(a as Map<String, dynamic>))
          .toList(),
      dormancyTransitions: ((j['dormancytransitions'] as List<dynamic>?) ?? [])
          .map((t) => DormancyTransition.fromJson(t as Map<String, dynamic>))
          .toList(),
    );
  }
}

/// One turn of an interactive session, from SessionMetadata.Turns (Aer.Adapters/InteractiveSessions.cs).
class SessionTurn {
  final int turnIndex;
  final String vendor;
  final String humanMessage;
  final String? assistantResponse;
  final DateTime executedAt;
  final String? errorMessage;

  /// #1179: the dormancy-answer marker -- see `SessionTurn.IsDormancyAnswer`'s doc comment in
  /// Aer.Adapters/InteractiveSessions.cs for what it means and why [assistantResponse] stays null.
  final bool isDormancyAnswer;

  /// 0026 §4/#1180: mirrors `SessionTurn.IsExhausted`/`ExhaustedUntil` in
  /// See `SessionTurn.IsExhausted`'s doc in Aer.Adapters/InteractiveSessions.cs (canonical: what
  /// the flag means, the render-before-errorMessage ordering rule, why [errorMessage] stays
  /// populated). Tolerant parse: absent on old metadata reads false/null, same idiom as
  /// [isDormancyAnswer].
  final bool isExhausted;
  final DateTime? exhaustedUntil;

  SessionTurn({
    required this.turnIndex,
    required this.vendor,
    required this.humanMessage,
    required this.assistantResponse,
    required this.executedAt,
    this.errorMessage,
    this.isDormancyAnswer = false,
    this.isExhausted = false,
    this.exhaustedUntil,
  });

  factory SessionTurn.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    return SessionTurn(
      turnIndex: (j['turnindex'] as num?)?.toInt() ?? 0,
      vendor: j['vendor']?.toString() ?? '',
      humanMessage: j['humanmessage']?.toString() ?? '',
      assistantResponse: j['assistantresponse']?.toString(),
      executedAt: DateTime.tryParse(j['executedat']?.toString() ?? '') ?? DateTime.now(),
      errorMessage: j['errormessage']?.toString(),
      isDormancyAnswer: j['isdormancyanswer'] == true,
      isExhausted: j['isexhausted'] == true,
      exhaustedUntil: j['exhausteduntil'] == null ? null : DateTime.tryParse(j['exhausteduntil'].toString()),
    );
  }
}

/// An interactive session's full state, from GET /api/sessions/{sessionId} (Aer.Daemon/Program.cs)
/// — REST-only, camelCase; unlike RoomProjection this is never pushed over /api/ws, so there is no
/// PascalCase/camelCase ambiguity to normalize, but this still reads through [caseInsensitive] for
/// consistency with every other model here.
class SessionMetadata {
  final String sessionId;
  final String roomDirectoryPath;
  final String currentAdapter;
  final int turnCount;
  final List<SessionTurn> turns;

  SessionMetadata({
    required this.sessionId,
    required this.roomDirectoryPath,
    required this.currentAdapter,
    required this.turnCount,
    required this.turns,
  });

  factory SessionMetadata.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    return SessionMetadata(
      sessionId: j['sessionid'].toString(),
      roomDirectoryPath: j['roomdirectorypath'].toString(),
      currentAdapter: j['currentadapter']?.toString() ?? '',
      turnCount: (j['turncount'] as num?)?.toInt() ?? 0,
      turns: ((j['turns'] as List<dynamic>?) ?? []).map((t) => SessionTurn.fromJson(t as Map<String, dynamic>)).toList(),
    );
  }
}

/// One live-streaming event from /api/ws/progress (M24 Phase 1, issue #262) — see RoomClient.cs's
/// ProgressFrame doc comment on desktop for why this is a dedicated socket/frame shape rather than
/// an overload of the /api/ws protocol. Broadcast to every connected progress socket regardless of
/// directory, same as RoomProjection pushes — callers must filter on directoryPath themselves.
class SessionProgressEvent {
  final String? directoryPath;
  final String? stepId;
  final String kind;
  final String text;
  final bool isPartial;

  SessionProgressEvent({
    required this.directoryPath,
    required this.stepId,
    required this.kind,
    required this.text,
    required this.isPartial,
  });

  factory SessionProgressEvent.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    return SessionProgressEvent(
      directoryPath: j['directorypath']?.toString(),
      stepId: j['stepid']?.toString(),
      kind: j['kind']?.toString() ?? '',
      text: j['text']?.toString() ?? '',
      isPartial: j['ispartial'] == true,
    );
  }
}

/// One vendor-discovered skill/command/agent/mode/plugin (M24 Phase 2 follow-up chat capability
/// picker) — the mobile counterpart of Aer.Ui.Core's ChatCapabilityItemViewModel. Only "command"/
/// "skill"/"agent" kinds are invokable; Gemini's "mode"/"plugin" kinds are informational only (see
/// ChatCapabilityItemViewModel's own remarks for why).
class ChatCapabilityItem {
  final String name;
  final String kind;
  final String description;
  final bool isRecentlyUsed;

  ChatCapabilityItem({required this.name, required this.kind, required this.description, required this.isRecentlyUsed});

  bool get isInvokable => kind == 'command' || kind == 'skill' || kind == 'agent';
}

/// One task/session directory's lightweight fleet-list entry (M24 Phase 5, #278) — the mobile
/// counterpart of Aer.Ui.Core's RoomFleetItem, as returned by GET /api/rooms.
class RoomFleetItem {
  final String roomDirectoryPath;
  final String friendlyName;
  final String typeLabel;
  final String statusText;
  final int pausedStepCount;
  final bool isArchived;
  final DateTime? lastActivityAt;

  /// The interactive session's id, present only for a session room (null for a workflow). Carries
  /// the identity a fleet row taps into to open its ChatScreen (front-door row-as-place, #1044);
  /// its presence is also how a row tells a session from a workflow without parsing typeLabel.
  final String? sessionId;

  /// The status vocabulary is the wire form of `RoomCardStatus` (`src/Aer.Ui.Core/HomeViewModel.cs`),
  /// or null for a never-run room. Drives the 0018 attention-band sort (#1133); `statusText` already carries
  /// the human line ("Waiting for your reply" vs "review", "Out of plan — resumes …"). Unrecognized
  /// values are tolerated by design — every consumer is an equality check, never an exhaustive switch.
  final String? status;

  RoomFleetItem({
    required this.roomDirectoryPath,
    required this.friendlyName,
    required this.typeLabel,
    required this.statusText,
    required this.pausedStepCount,
    required this.isArchived,
    this.lastActivityAt,
    this.sessionId,
    this.status,
  });

  factory RoomFleetItem.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    return RoomFleetItem(
      roomDirectoryPath: j['roomdirectorypath']?.toString() ?? '',
      friendlyName: j['friendlyname']?.toString() ?? '',
      typeLabel: j['typelabel']?.toString() ?? '',
      statusText: j['statustext']?.toString() ?? '',
      pausedStepCount: (j['pausedstepcount'] as num?)?.toInt() ?? 0,
      isArchived: j['isarchived'] == true,
      lastActivityAt: j['lastactivityat'] != null ? DateTime.tryParse(j['lastactivityat'].toString()) : null,
      sessionId: (j['sessionid']?.toString().isEmpty ?? true) ? null : j['sessionid'].toString(),
      status: (j['status']?.toString().isEmpty ?? true) ? null : j['status'].toString(),
    );
  }
}

/// GET /api/sessions/{id}/commands's shape: WorkerCapabilities's own fields plus the additive
/// RecentlyUsed sibling (same idiom as RoomProjection's DirectoryPath/WorkerAdapters siblings).
class SessionCommandsResult {
  final String vendor;
  final List<ChatCapabilityItem> items;
  final List<String> models;

  SessionCommandsResult({required this.vendor, required this.items, required this.models});

  factory SessionCommandsResult.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    final recentlyUsed = ((j['recentlyused'] as List<dynamic>?) ?? []).map((n) => n.toString()).toSet();
    final rawItems = (j['items'] as List<dynamic>?) ?? [];
    return SessionCommandsResult(
      vendor: j['vendor']?.toString() ?? '',
      items: rawItems.map((raw) {
        final item = caseInsensitive(raw as Map<String, dynamic>);
        final name = item['name']?.toString() ?? '';
        return ChatCapabilityItem(
          name: name,
          kind: item['kind']?.toString() ?? '',
          description: item['description']?.toString() ?? '',
          isRecentlyUsed: recentlyUsed.contains(name),
        );
      }).toList(),
      models: ((j['models'] as List<dynamic>?) ?? []).map((m) => m.toString()).toList(),
    );
  }
}
