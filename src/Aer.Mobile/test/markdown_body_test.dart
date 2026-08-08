import 'package:aer_mobile/chat_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_markdown_plus/flutter_markdown_plus.dart';
import 'package:flutter_test/flutter_test.dart';

/// Tests for the mobile markdown transcript renderer (#1080, decision 0051). The security cases
/// (no remote content, §3) and the depth case are the ones that matter — a plain-Text renderer or a
/// renderer with the default network image path must fail them.
void main() {
  Widget buildSubject(String text, {Brightness brightness = Brightness.light}) {
    return MaterialApp(
      theme: ThemeData(brightness: brightness),
      home: Scaffold(
        body: SingleChildScrollView(
          child: MarkdownBodyWidget(text: text, foreground: Colors.black),
        ),
      ),
    );
  }

  testWidgets('Fenced code block renders the code, not the fence markers', (tester) async {
    const markdown = '```dart\nvar x = 1;\n```';
    await tester.pumpWidget(buildSubject(markdown));

    // The code content is present; the literal fence line is not (proves real parsing, not a no-op).
    expect(find.textContaining('var x = 1;', findRichText: true), findsOneWidget);
    expect(find.textContaining('```dart', findRichText: true), findsNothing);
  });

  testWidgets('Inline bold, italic, and code render as parsed rich spans', (tester) async {
    const markdown = 'a **bold** b *italic* c `code` d';
    await tester.pumpWidget(buildSubject(markdown));

    expect(find.textContaining('bold', findRichText: true), findsOneWidget);
    expect(find.textContaining('italic', findRichText: true), findsOneWidget);
    // A no-op renderer would leave the literal markup verbatim; real parsing consumes it.
    expect(find.textContaining('**bold**', findRichText: true), findsNothing);
  });

  testWidgets('Security §3: a markdown image loads no Image widget and shows alt text', (tester) async {
    const markdown = '![alt text](http://evil.com/x.png)';
    await tester.pumpWidget(buildSubject(markdown));

    // The load-bearing assertion: no Image control ever reaches the tree, so nothing is fetched.
    expect(find.byType(Image), findsNothing);
    expect(find.textContaining('alt text', findRichText: true), findsOneWidget);
  });

  testWidgets('Security §3: a reference-style image loads no Image widget', (tester) async {
    // A different syntax path (reference resolution) than the inline image, per 0051 §3.
    const markdown = '![alt text][ref]\n\n[ref]: http://evil.com/x.png';
    await tester.pumpWidget(buildSubject(markdown));

    expect(find.byType(Image), findsNothing);
    expect(find.textContaining('alt text', findRichText: true), findsOneWidget);
  });

  testWidgets('Security §3: an autolink loads no Image widget and is inert', (tester) async {
    const markdown = 'see <http://evil.com/x.png> here';
    await tester.pumpWidget(buildSubject(markdown));

    expect(find.byType(Image), findsNothing);
    expect(find.textContaining('http://evil.com/x.png', findRichText: true), findsOneWidget);
  });

  testWidgets('Security §3: raw <img> / <script> HTML loads no Image widget', (tester) async {
    const markdown = 'before <img src="http://evil.com/x.png"> and <script>alert(1)</script> after';
    await tester.pumpWidget(buildSubject(markdown));

    // Whether the parser degrades raw HTML to literal text or routes <img> through our literal
    // imageBuilder, the invariant is identical: no Image control, no fetch.
    expect(find.byType(Image), findsNothing);
    // Surrounding prose still renders — the message is not swallowed.
    expect(find.textContaining('before', findRichText: true), findsOneWidget);
    expect(find.textContaining('after', findRichText: true), findsOneWidget);
  });

  testWidgets('A link renders its text and a tap neither navigates nor crashes', (tester) async {
    const markdown = '[click here](http://example.com)';
    await tester.pumpWidget(buildSubject(markdown));

    final link = find.textContaining('click here', findRichText: true);
    expect(link, findsOneWidget);
    // onTapLink is unset, so a tap is inert (no navigation, no fetch). Just prove it doesn't throw.
    await tester.tap(link);
    await tester.pump();
  });

  testWidgets('Normal-depth content renders through MarkdownBody (guard does not over-fire)', (tester) async {
    // Control for the guard tests below: ordinary nesting must still render richly, not degrade.
    await tester.pumpWidget(buildSubject('# Title\n\n- a\n  - b\n\n> quote **bold**'));
    expect(find.byType(MarkdownBody), findsOneWidget);
  });

  testWidgets('Pathologically deep emphasis degrades to plain text, never MarkdownBody', (tester) async {
    // The depth guard, not a happy-path render: the recursive build never runs on this input, so the
    // safety no longer depends on the host stack the test happens to run on (finding 1). MarkdownBody
    // is absent because the guard returned SelectableText before constructing it.
    final deepEmphasis = '${'*' * 5000}x${'*' * 5000}';
    await tester.pumpWidget(buildSubject(deepEmphasis));
    expect(tester.takeException(), isNull);
    expect(find.byType(MarkdownBody), findsNothing);
    expect(find.byType(SelectableText), findsOneWidget);
  });

  testWidgets('Deeply nested blockquotes degrade to plain text, never MarkdownBody', (tester) async {
    final deepBlockquote = '${'> ' * 500}deep';
    await tester.pumpWidget(buildSubject(deepBlockquote));
    expect(tester.takeException(), isNull);
    expect(find.byType(MarkdownBody), findsNothing);
  });
}
