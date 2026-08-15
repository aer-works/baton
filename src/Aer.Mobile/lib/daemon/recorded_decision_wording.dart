/// The transcript row for a decision already answered in a room (#1240).
/// `PlainLanguage.ForRecordedDecision`/`ForDecision` (`src/Aer.Ui.Core/RoomStepViewModels.cs`) is the
/// one home for these words and for the grammar rule below; this is the mobile copy of that exact
/// mapping, kept character-identical so the two surfaces cannot drift.
library;

import 'models.dart';

/// Only `Supersede` names a target — `FlowEvent.ExternalDecisionRecorded` says so of its own
/// `TargetStepId`, and only its verb ("Sent back") takes one grammatically. Appending the target to
/// whatever verb happened to arrive would read "Approved to review" the day another decision type
/// carries one.
///
/// An unmapped decision type renders its raw wire value, which is the one deliberate divergence from
/// the C#: that method throws so a new enum member reddens the golden map, and this app is a *client*
/// of a daemon that may be newer than it — throwing here would blank a whole transcript over one
/// unknown row. Same reading `roomStatus`'s catch-all takes in `rooms_screen.dart`.
String formatRecordedDecisionWording(RecordedDecisionMoment moment) {
  final verb = switch (moment.decisionType) {
    'Resume' => 'Approved',
    'Reject' => 'Rejected',
    'RetryWithRevision' => 'Retry requested',
    // vocabulary-ok: decision type label
    'Supersede' => 'Sent back',
    _ => moment.decisionType,
  };

  // vocabulary-ok: decision type label
  return (moment.decisionType == 'Supersede' && moment.targetStepId != null)
      ? '$verb to ${moment.targetStepId}'
      : verb;
}
