/// Port of `OutcomeClassifier.SplitReasonAndStderr` (`src/Aer.Flow/Outcomes/OutcomeClassifier.cs`).
/// See that file for why `" stderr: "` is the separator and why last-occurrence was rejected.
library;

/// Splits a raw failure reason into its human reason sentence and optional stderr excerpt.
(String sentence, String? stderrExcerpt) splitReasonAndStderr(String? reason) {
  if (reason == null || reason.trim().isEmpty) {
    return ('Step failed.', null);
  }

  const separator = ' stderr: ';
  final index = reason.indexOf(separator);
  if (index < 0) {
    return (reason.trim(), null);
  }

  final sentence = reason.substring(0, index).trim();
  final excerpt = reason.substring(index + separator.length).trim();

  return (sentence, excerpt.isEmpty ? null : excerpt);
}
