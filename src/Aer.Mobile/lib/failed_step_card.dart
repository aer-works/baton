import 'package:flutter/material.dart';

import 'daemon/failed_step_reason.dart';
import 'daemon/models.dart';
import 'theme/tokens.dart';

/// The card a failed step renders at the end of a workflow room's transcript (#1245) — the phone
/// half of the desktop's `FailedStepBannerViewModel` (`src/Aer.Ui.Core/RoomStepViewModels.cs`).
///
/// Buttonless by design and not an oversight — nothing is waiting on the person, so there is no
/// decision affordance. A card offering an action the phone cannot perform is worse than no card.
class FailedStepCard extends StatelessWidget {
  final WorkflowStepState step;

  /// The worker the shape names for this step, or null when it names none — see the call site for
  /// why an absent name is dropped rather than stood in for.
  final String? worker;

  const FailedStepCard({
    super.key,
    required this.step,
    required this.worker,
  });

  @override
  Widget build(BuildContext context) {
    final (sentence, stderrExcerpt) = splitReasonAndStderr(step.latestFailureReason);
    final who = worker == null ? step.stepId : '${step.stepId} ($worker)';
    final headline = '$who failed — $sentence';

    return Card(
      margin: const EdgeInsets.only(bottom: 12),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(headline, style: Theme.of(context).textTheme.titleMedium),
            if (stderrExcerpt != null) ...[
              const SizedBox(height: 8),
              Container(
                width: double.infinity,
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: Theme.of(context).colorScheme.surfaceContainerHighest,
                  borderRadius: BorderRadius.circular(AerTokens.radiusSm),
                ),
                child: Text(
                  stderrExcerpt,
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        fontFamily: AerTokens.fontMono,
                      ),
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}
