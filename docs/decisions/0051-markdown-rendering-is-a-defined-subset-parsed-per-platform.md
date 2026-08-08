# 0051 — Markdown rendering is a defined CommonMark subset, parsed per platform, with no remote content

Status: accepted
Date: 2026-08-08

Builds on [0020](0020-one-state-machine.md) (every surface renders one derivation, none invents its own), [0006](0006-visual-direction-quiet.md) (the Quiet visual direction the rendered blocks are styled to), and [0002](0002-one-vocabulary.md) (both surfaces are views of one thing, so they must agree on what a message *is*).

## Context

A worker's reply is Markdown. Vendor CLIs (`claude`, `agy`) emit CommonMark/GFM — headings, lists, emphasis, and above all fenced **code blocks** and diffs, which are most of what a coding tool's transcript carries. Both surfaces (`#267`) render that text through a single flat, wrapping `TextBlock`/`Text` today, so a diff reads as reflowed prose and a code block loses its structure.

Two forces shape how we fix it:

1. **The two surfaces are different stacks.** The desktop is Avalonia/.NET; the phone is Flutter/Dart. There is no shared rendering code and cannot be. A "render markdown" decision therefore has to say what stays the same across two independent implementations.
2. **The content is untrusted.** The text being rendered is model output. Markdown can carry image URLs, link targets, and raw HTML. Rendering any of those as a live resource turns a transcript into a network client acting on an LLM's say-so — a tracking pixel loaded, a host told someone opened the room — which the product's isolation posture (Architecture Rule 4: AER is a keyboard, not a client) does not permit a *view* to undo.

## Decision

**Markdown is rendered as a defined CommonMark subset, parsed on each platform by a mature parser, to native controls — and rendered markdown never fetches a remote resource.**

1. **One subset, both surfaces.** The supported set is: paragraphs; headings; emphasis and strong; inline code; fenced and indented **code blocks** (monospace, on the `sunk` fill, selectable); ordered and unordered lists; blockquotes; and links (styled, opened only on an explicit user action). Anything outside the subset — including raw HTML — degrades to its literal source text; it is never executed or rendered as markup. The subset is the contract the two implementations render the same.

2. **Parsed by a real parser, per platform — never hand-rolled.** Desktop uses **Markdig** (the reference .NET CommonMark parser; UI-agnostic, so a native renderer walks its AST into Avalonia controls and nothing couples to Avalonia's version cadence). Mobile uses the `markdown` Dart package via **`flutter_markdown_plus`** (the maintained continuation of the discontinued `flutter_markdown`). A hand-written subset parser is out: CommonMark's edge cases are exactly where a hand-rolled one drifts, and the two surfaces would drift apart.

3. **No remote content.** Rendered markdown issues no network request of its own: no auto-loaded or prefetched images, no link followed without an explicit user action. This is a property of the *renderer*, enforced there, not a hope about the input.

4. **Rendering is presentation over the one derivation.** The message text is unchanged and still read from the single source both surfaces share ([0020](0020-one-state-machine.md)); this decision governs how that one text is *drawn*, so the surfaces can differ in styling but never in what the message says.

## Rests on

| fact | how we know | if false |
|---|---|---|
| Markdig parses CommonMark and carries no UI-framework dependency, so a renderer over its AST is immune to Avalonia's major-version cadence | it is the reference .NET CommonMark parser (BSD-2), depended on UI-agnostically across the ecosystem; the direct-render `Markdown.Avalonia` is pinned at 11.0.3 with no Avalonia 12 build | we would be coupling markdown rendering to an Avalonia-version-sensitive control instead |
| `flutter_markdown_plus` is a maintained CommonMark renderer for Flutter | pub.dev: the Foresight Mobile continuation of Google's discontinued (May 2025) `flutter_markdown`, over the standard `markdown` Dart package | mobile would have no maintained CommonMark renderer and the subset could not be matched across surfaces |
| Rendering an LLM-supplied remote URL leaks a signal to its host | a fetch is a request: the host learns the resource was loaded, from whom, and when | auto-loading remote images/links in a transcript would be safe, and rule 3 would be unnecessary |

## Consequences

**Easier.** Diffs, code, and structured output in the transcript read as what they are on both surfaces; the subset is one written contract two independent stacks render alike; and a view can never be tricked into acting as a network client for model output.

**Harder.** Two renderers must be kept in agreement against one subset spec rather than sharing code; per-language syntax highlighting inside code blocks is a further step on each; and any future "rich" affordance (rendered images, embeds) has to argue past rule 3 rather than arrive by default.

**Obliges us to** keep the desktop AST-walking renderer and the mobile `flutter_markdown_plus` configuration rendering the same subset, enforce the no-remote-content rule in each renderer (not the parser), and degrade anything outside the subset to literal text.
