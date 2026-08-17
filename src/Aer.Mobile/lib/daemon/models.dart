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

/// Wire twin of the engine's RecordedDecisionMoment (`src/Aer.Flow/Projection/RecordedDecisionMoment.cs`
/// is canonical). Only the fields the transcript row needs are parsed: the verb comes from
/// [decisionType], `Sent back` is the one verb that names a [targetStepId], and [recordedAt] is what
/// merges it into the transcript in order.
class RecordedDecisionMoment {
  final String decisionId;
  final String decisionType;
  final String? targetStepId;
  final DateTime recordedAt;

  RecordedDecisionMoment({
    required this.decisionId,
    required this.decisionType,
    this.targetStepId,
    required this.recordedAt,
  });

  factory RecordedDecisionMoment.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    final target = j['targetstepid'];
    return RecordedDecisionMoment(
      decisionId: j['decisionid']?.toString() ?? '',
      decisionType: j['decisiontype']?.toString() ?? '',
      // StepId serializes as a bare string here, the same shape `steps[].stepId` arrives in.
      targetStepId: (target == null || target.toString().isEmpty) ? null : target.toString(),
      // The engine's RecordedAt is nullable — a moment recorded before timestamps were carried has
      // none. It sorts to the start of the transcript rather than to "now", which would float an old
      // decision to the bottom every time the screen rebuilt.
      recordedAt: DateTime.tryParse(j['recordedat']?.toString() ?? '') ?? DateTime.utc(1),
    );
  }
}

/// A projection Aer.Daemon pushes for one room directory. Aer.Daemon still has only one
/// "current" task server-side (RoomClient.CurrentRoomDirectoryPath) and broadcasts every
/// change to every connected WS client regardless of which directory it's for — but this app
/// filters incoming pushes against the directory the open room screen is bound to before applying one
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

  /// Worker name -> canonical effort word (#1318, decision 0058's scope ruling 4) — the wire twin of
  /// `Aer.Daemon.DaemonBroadcast.BuildWorkerEffortTiers`. Already filtered server-side to entries
  /// whose binding holds one of 0023's four canonical words; a worker with a raw vendor value or no
  /// effort at all is simply absent here, never defaulted or reverse-mapped.
  final Map<String, String> workerEffortTiers;

  /// The runtime conversational permission gate (0022, #390's mobile phase), or null when no worker
  /// is blocked on one — see [PendingPermission]'s doc comment for where this sits on the wire.
  final PendingPermission? pendingPermission;

  /// History of answered or revoked runtime permissions (bounded to newest 50).
  final List<PermissionAnswer> permissionAnswers;

  /// History of turn host dormancy transitions (#1178).
  final List<DormancyTransition> dormancyTransitions;

  /// Decisions already answered in this room (#1240) — the durable history rows the desktop's
  /// transcript has carried since #1199. On the wire since then; nothing on the phone read it.
  final List<RecordedDecisionMoment> recordedDecisionMoments;

  /// The derived room-card status (#1240), a sibling the daemon bolts on exactly like
  /// [directoryPath] — **not** part of RoomProjection, and not the same question as [status] above,
  /// which is the raw engine `WorkflowStatus`. `DaemonBroadcast.DeriveRoomCardStatus` (the daemon) is
  /// where it comes from and why it is sent at all.
  ///
  /// Null means the daemon could not say. Never read absence as a state.
  final String? roomCardStatus;

  /// The prose half of the pair above, the same register `RoomFleetItem.statusText` carries — e.g.
  /// `Out of plan — resumes 2026-08-15 14:32`, wording no client can rebuild without copying the
  /// derivation into its own language.
  final String? roomCardStatusText;

  /// Wire twin of the engine's `RoomState.IsWorkflowOff` (#1216) — see its doc comment for what the
  /// flag means and why absence reads as ON. Parsed here so the twin stays complete; the phone's own
  /// switch is #1196 slice 6.
  final bool isWorkflowOff;

  /// 0054 §7/#1307 ruling 7: the wire twin of `SessionMetadata.Participants` — see
  /// `RoomProjection.Participants`'s doc in Aer.Ui.Core/RoomProjection.cs for why this exists
  /// alongside that field rather than instead of it. Parsing only this slice (ruling 6): nothing on
  /// the phone reads it yet.
  final List<Participant> participants;

  RoomProjection({
    required this.directoryPath,
    required this.sessionId,
    required this.workflowTemplateId,
    required this.status,
    required this.stepDefinitions,
    required this.steps,
    required this.executions,
    required this.workerAdapters,
    this.workerEffortTiers = const {},
    this.pendingPermission,
    this.permissionAnswers = const [],
    this.dormancyTransitions = const [],
    this.recordedDecisionMoments = const [],
    this.roomCardStatus,
    this.roomCardStatusText,
    this.isWorkflowOff = false,
    this.participants = const [],
  });

  bool get isDormant => dormancyTransitions.isNotEmpty && dormancyTransitions.last.isEntered;

  List<WorkflowStepState> get pausedSteps => steps.where((s) => s.isPaused).toList();

  /// Steps that have failed and are not waiting on quota (`ExhaustedUntil`) (#1245).
  List<WorkflowStepState> get failedSteps => steps
      .where((s) => s.isFailed && s.latestFailureClassification != 'ExhaustedUntil')
      .toList();

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

    final workerEffortTiers = <String, String>{};
    if (j['workerefforttiers'] is Map<String, dynamic>) {
      (j['workerefforttiers'] as Map<String, dynamic>).forEach((k, v) {
        if (v != null) workerEffortTiers[k] = v.toString();
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
      workerEffortTiers: workerEffortTiers,
      pendingPermission: j['pendingpermission'] == null
          ? null
          : PendingPermission.fromJson(j['pendingpermission'] as Map<String, dynamic>),
      permissionAnswers: ((j['permissionanswers'] as List<dynamic>?) ?? [])
          .map((a) => PermissionAnswer.fromJson(a as Map<String, dynamic>))
          .toList(),
      dormancyTransitions: ((j['dormancytransitions'] as List<dynamic>?) ?? [])
          .map((t) => DormancyTransition.fromJson(t as Map<String, dynamic>))
          .toList(),
      recordedDecisionMoments: ((j['recordeddecisionmoments'] as List<dynamic>?) ?? [])
          .map((d) => RecordedDecisionMoment.fromJson(d as Map<String, dynamic>))
          .toList(),
      roomCardStatus: (j['roomcardstatus']?.toString().isEmpty ?? true) ? null : j['roomcardstatus'].toString(),
      roomCardStatusText:
          (j['roomcardstatustext']?.toString().isEmpty ?? true) ? null : j['roomcardstatustext'].toString(),
      isWorkflowOff: j['isworkflowoff'] == true,
      participants: ((j['participants'] as List<dynamic>?) ?? [])
          .map((p) => Participant.fromJson(p as Map<String, dynamic>))
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

  /// See `SessionTurn.IsExhausted`'s doc in Aer.Adapters/InteractiveSessions.cs (canonical: what
  /// the flag means, the render-before-errorMessage ordering rule, why [errorMessage] stays
  /// populated). Tolerant parse: absent on old metadata reads false/null, same idiom as
  /// [isDormancyAnswer].
  final bool isExhausted;
  final DateTime? exhaustedUntil;

  /// 0054 §4/#1307: the sender's tag, durable on the turn — see
  /// `SessionTurn.TargetParticipantId`'s doc in Aer.Adapters/InteractiveSessions.cs (canonical: why
  /// null means "posted to the room" and is never the daemon's resolved orchestrator). Parsing only
  /// — no addressing UI on the phone this slice (ruling 6).
  final String? targetParticipantId;

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
    this.targetParticipantId,
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
      targetParticipantId: j['targetparticipantid']?.toString(),
    );
  }
}

/// An interactive session's full state, from GET /api/sessions/{sessionId} (Aer.Daemon/Program.cs)
/// — REST-only, camelCase; unlike RoomProjection this is never pushed over /api/ws, so there is no
/// PascalCase/camelCase ambiguity to normalize, but this still reads through [caseInsensitive] for
/// consistency with every other model here.
/// Mirrors `Aer.Flow.Domain.Participant` (#1305) — see that record's doc comment for what a
/// participant is and how it relates to its vendor/model/effort.
class Participant {
  final String id;
  final String name;
  final String vendor;
  final String? model;
  final String? effort;
  final bool isOrchestrator;

  Participant({
    required this.id,
    required this.name,
    required this.vendor,
    this.model,
    this.effort,
    required this.isOrchestrator,
  });

  factory Participant.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    return Participant(
      id: j['id']?.toString() ?? '',
      name: j['name']?.toString() ?? '',
      vendor: j['vendor']?.toString() ?? '',
      model: j['model']?.toString(),
      effort: j['effort']?.toString(),
      isOrchestrator: j['isorchestrator'] == true,
    );
  }
}

class SessionMetadata {
  final String sessionId;
  final String roomDirectoryPath;
  final String currentAdapter;
  final int turnCount;
  final List<SessionTurn> turns;
  // Null on a room whose room.json predates #1305 -- callers fall back to currentAdapter, the same
  // way ChatViewModel.LoadFromMetadata does on desktop.
  final List<Participant>? participants;

  SessionMetadata({
    required this.sessionId,
    required this.roomDirectoryPath,
    required this.currentAdapter,
    required this.turnCount,
    required this.turns,
    this.participants,
  });

  factory SessionMetadata.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    return SessionMetadata(
      sessionId: j['sessionid'].toString(),
      roomDirectoryPath: j['roomdirectorypath'].toString(),
      currentAdapter: j['currentadapter']?.toString() ?? '',
      turnCount: (j['turncount'] as num?)?.toInt() ?? 0,
      turns: ((j['turns'] as List<dynamic>?) ?? []).map((t) => SessionTurn.fromJson(t as Map<String, dynamic>)).toList(),
      participants: (j['participants'] as List<dynamic>?)
          ?.map((p) => Participant.fromJson(p as Map<String, dynamic>))
          .toList(),
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
