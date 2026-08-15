import 'package:flutter/material.dart';

/// The card a room that has stopped carries at the end of its own transcript (#1240) — the phone
/// half of the desktop's `RoomStoppedCardViewModel` (`src/Aer.Ui.Core/RoomStepViewModels.cs`), whose
/// 2026-08-14 amendment in `docs/design/02-screens.md` is the canonical record for the copy and for
/// why the offer sits on the turn rather than in chrome. Before this a finished room on the phone
/// rendered nothing at all, which under 0018 is not even calm: it makes no claim.
///
/// Which of the desktop's sentences cross to a surface with no run flow — and why dropping the rest
/// is not a second vocabulary — is that amendment's one clause to state. Applied here as three
/// headlines and a single body sentence, on the stopped-mid-run arm.
///
/// Buttonless by design and not an oversight — a run needs worker bindings the phone cannot supply.
/// #1230 (a room carrying its own `bindings.json`) is what would later make an offer possible here.
class RoomStoppedCard extends StatelessWidget {
  /// The daemon's derived `RoomCardStatus` — never the raw `WorkflowStatus`, which cannot tell a
  /// running room from one whose process died (see `RoomProjection.roomCardStatus`).
  final String roomCardStatus;

  const RoomStoppedCard({super.key, required this.roomCardStatus});

  /// Whether [roomCardStatus] is one this card speaks for. Failed is deliberately absent — it belongs
  /// to #617's failed-step banner (the amendment above says why), which the phone does not have yet:
  /// #1245.
  static bool speaksFor(String? roomCardStatus) =>
      roomCardStatus == 'Finished' || roomCardStatus == 'Cancelled' || roomCardStatus == 'Stopped';

  String get _headline => switch (roomCardStatus) {
        'Finished' => 'This room has finished',
        'Cancelled' => 'You stopped this room',
        _ => 'This room stopped mid-run',
      };

  String? get _body =>
      roomCardStatus == 'Stopped' ? 'Nothing is running it and it is not waiting on you.' : null;

  @override
  Widget build(BuildContext context) {
    final body = _body;

    return Card(
      margin: const EdgeInsets.only(bottom: 12),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(_headline, style: Theme.of(context).textTheme.titleMedium),
            if (body != null) ...[
              const SizedBox(height: 4),
              Text(body, style: Theme.of(context).textTheme.bodyMedium),
            ],
          ],
        ),
      ),
    );
  }
}
