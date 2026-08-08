import 'package:aer_mobile/chat_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  Widget buildSubject(String text, {Brightness brightness = Brightness.light}) {
    return MaterialApp(
      theme: ThemeData(brightness: brightness),
      home: Scaffold(
        body: SingleChildScrollView(
          child: MarkdownBodyWidget(
            text: text,
            foreground: Colors.black,
          ),
        ),
      ),
    );
  }

  testWidgets('1. Fenced code block renders code text inside formatted code block', (tester) async {
    const markdown = '`dart\nvar x=1;\n`';
    await tester.pumpWidget(buildSubject(markdown));

    expect(find.textContaining('var x=1;', findRichText: true), findsOneWidget);
    expect(find.text('`dart\nvar x=1;\n`'), findsNothing);
    expect(find.byType(SelectableText), findsWidgets);
  });

  testWidgets('2. Inline bold, italic, and code render as rich parsed spans', (tester) async {
    const markdown = '**bold** *italic* code';
    await tester.pumpWidget(buildSubject(markdown));

    expect(find.byType(SelectableText), findsWidgets);
    expect(find.textContaining('bold', findRichText: true), findsOneWidget);
    expect(find.textContaining('italic', findRichText: true), findsOneWidget);
    expect(find.textContaining('code', findRichText: true), findsOneWidget);
    expect(find.text('**bold** *italic* code'), findsNothing);
  });

  testWidgets('3. Security - image renders alt text literally and no Image widget', (tester) async {
    const markdown = '![alt text](http://evil.com/x.png)';
    await tester.pumpWidget(buildSubject(markdown));

    expect(find.byType(Image), findsNothing);
    expect(find.text('alt text', findRichText: true), findsOneWidget);
  });

  testWidgets('4. Security - raw HTML renders literal source text and no Image widget', (tester) async {
    const markdown = '<img src= http://evil.com/x.png> and <script>alert(1)</script>';
    await tester.pumpWidget(buildSubject(markdown));

    expect(find.byType(Image), findsNothing);
    expect(find.textContaining('<img src=', findRichText: true), findsOneWidget);
    expect(find.textContaining('<script>alert(1)</script>', findRichText: true), findsOneWidget);
  });

  testWidgets('5. Link renders styled text and tap does not crash or navigate', (tester) async {
    const markdown = '[click](http://example.com)';
    await tester.pumpWidget(buildSubject(markdown));

    final linkFinder = find.textContaining('click', findRichText: true);
    expect(linkFinder, findsOneWidget);

    await tester.tap(linkFinder);
    await tester.pump();
  });

  testWidgets('6. Deep nesting pumps without throwing and renders content', (tester) async {
    final deepEmphasis = 'x';
    await tester.pumpWidget(buildSubject(deepEmphasis));
    expect(find.textContaining('x', findRichText: true), findsOneWidget);

    final deepBlockquote = ' text';
    await tester.pumpWidget(buildSubject(deepBlockquote));
    expect(find.textContaining('text', findRichText: true), findsOneWidget);
  });
}
