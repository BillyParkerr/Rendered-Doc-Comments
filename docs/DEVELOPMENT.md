# Developing Rendered Doc Comments

How to build, run and test the extension, how it works, and where things are in the code.

## Build and run

You need Visual Studio 2026 with the **Visual Studio extension development** workload.

1. Open `RenderedDocComments.slnx` and press **F5**. This builds the extension and starts Visual Studio's experimental instance with it installed.
2. In the experimental instance, use **File > Open > Project/Solution** to open `samples/Samples.csproj`, then open one of its files:
   - `DocCommentSamples.cs`: a realistic API with typical comments.
   - `TagSamples.cs`: one of every supported tag.
   - `InheritDocSamples.cs`: `<inheritdoc/>` cases, including across files.

Open the samples as a project rather than as loose files, so Roslyn can resolve their links.

Keep the repository at a short path: MSBuild on .NET Framework fails on paths longer than 260 characters.

The built extension is `src/RenderedDocComments/bin/<Configuration>/net472/RenderedDocComments.vsix`. Double-click it to install it in your normal Visual Studio.

## Tests

`tests/RenderedDocComments.Tests` covers everything that doesn't need a running Visual Studio. Run it from the repository root:

```bash
dotnet test tests/RenderedDocComments.Tests
```

The tests also appear in Test Explorer. The project compiles the extension's own source files rather than referencing the VSIX, and runs on .NET Framework 4.7.2 like Visual Studio. The few VS types the card builder touches (editor services, options) are replaced by small stand-ins in `Fakes/ExtensionStubs.cs`, so cards get the built-in light colours. WPF tests run on a single shared STA thread (`Fakes/Sta.cs`).

| Area | What's checked |
|---|---|
| Comment scanning | Which lines are doc comments, indentation, spans; the fast line lookups agree with a plain search on every line; which edits count as touching a comment |
| Incremental scanning | After 6,000 random edits (single and multi-caret changes, big cuts and pastes, several edits between scans) the result always matches reading the whole file; a keystroke reads only a few lines; comments move with lines inserted above them |
| Comment keys | Remembered choices survive edits, attribute lines are skipped, look-alike declarations get distinct keys |
| XML parsing and sections | Half-typed and malformed comments never throw (every prefix of a comment, random input); entities, CDATA, attributes; the less common sections, tags left out, formatting outside any section; inherited-doc merging |
| Cref display | Documentation IDs and source-style crefs, generics, constructors; crefs known to name a type |
| Code colouring | C# keywords, types, methods, strings and comments; XML/HTML tags, attributes, values, comments and CDATA; which colouring each `language` gets; the coloured runs always add up to the original text |
| Cards | Every supported tag, built into a real card: the document's blocks, formatting, colours and text; section order; columns filling the card's width; inherited docs; every comment in `samples/` builds |
| `<inheritdoc/>` | Overrides, interfaces, base classes, constructors, chains, crefs to generic methods, parameters the member doesn't have, unresolvable crefs, the files each result came from (with Roslyn, against sample source); `path` selection and `<inheritdoc/>` inside sections |
| Quick documentation | Finding the symbol at a position (against sample source and a compiled library with its own documentation file, standing in for .NET's): calls, constructors, `var`, keyword types, extension methods, parameters, locals, crefs, undocumented overrides, every position in the file. The popup's card: header, signature links, Back link, inherited docs, sizing to its content without wrapping |
| Shortcut option | Typed keys tidied into Visual Studio's form |
| Settings registration | `Options/registration.json` is well formed, every stored setting maps to the classic store's value and default, the code reads exactly the registered settings, and the category replaces the classic Options page |

The editor itself (folding, the gutter, the cursor, the popup's placement and closing, hover, and how cards look on screen) is checked by hand in the experimental instance.

## How it works

A card replaces a comment without changing the text:

- Lines 2 to N of the comment are folded away with an outlining region. Line 1 keeps its normal height, plus extra space below it (through a line transform) for the rest of the card.
- The card is drawn on its own adornment layer above the text, over an editor-coloured strip that hides line 1's raw text, the fold's box and Visual Studio's collapsed-line shading.
- Each card is one read-only `RichTextBox` holding one `FlowDocument`: paragraphs, a two-column table for the labelled sections, lists and code. That lets a selection run across the whole card. Code blocks' rounded backgrounds are drawn behind the text box, where layout puts each code paragraph.
- In side-by-side diffs, which manage their own folding, the card is drawn over the comment's raw lines instead.

Cards are built from the comment's text with a forgiving XML parser, so half-typed comments still render. Roslyn is used only where it's needed and only off the UI thread: resolving links when they're hovered or clicked, `<inheritdoc/>`, and quick documentation.

## Code layout

```
src/RenderedDocComments/
  Parsing/      Finds /// blocks (incrementally), the forgiving XML parser, the section
                model, cref display names, and which part of inherited docs an
                <inheritdoc/> takes
  Rendering/    Theme from the editor's settings, the WPF card builder, the C# and XML
                tokenizers, the code-language choice and the highlighter
  Editor/       Per-file raw/rendered state, the fold over lines 2..N, the card layer, the
                gutter icon, the controller (cursor, Find, outlining, toggles), copying card
                text, and Ctrl+M, L (toggle all outlining without counting the cards' folds)
  Editor/QuickDoc*
                The quick documentation popup, its shortcut and Escape handling, and the
                hover source that swaps Visual Studio's tooltip for it
  Navigation/   Roslyn: resolving crefs, Go To Definition, link signatures, <inheritdoc/>
                (finding the source, following chains, merging), and the symbol and docs at
                a position for quick documentation
  Options/      Settings: registration.json for Visual Studio 2026's Settings window,
                reading them (with change notifications) and the classic store and
                Tools > Options page they stay in step with, and the shortcut option
  Commands/     Menu commands
  Diagnostics/  Guard: catches and logs errors at every entry point from Visual Studio
tests/RenderedDocComments.Tests/
                Unit tests (xUnit)
samples/        A small C# project to open in the experimental instance
```

## Errors

Every place Visual Studio calls into the extension (layout, tagging, the gutter, commands, events, background work) catches its own exceptions through `Guard`, so a bug shows up as a plain comment rather than an error dialog or a broken editor.

- A comment whose card can't be built shows a plain card with its text.
- The first error in a session is mentioned once in the status bar. Each distinct error is written once to Visual Studio's activity log, under the source `RenderedDocComments`. Start Visual Studio with `devenv /log` to be sure it's recorded; the file is `ActivityLog.xml` under `%AppData%\Microsoft\VisualStudio\`.

## Performance

The extension runs on every keystroke and scroll, so it's built to do as little as possible on the UI thread.

- Finding comments is incremental: each new snapshot's comments are worked out from the previous one's, reading only the lines around the edit. The whole file is read only when it opens, or after an edit bigger than half the file.
- An edit that doesn't touch a comment, or a line next to one, does no further comment work. Ordinary typing inside a comment doesn't ask Visual Studio to re-check folds at all, since Visual Studio moves fold edges with the text itself.
- Looking up the comment on a line is a binary search.
- Cards stay on screen between keystrokes and are only moved; cards that scroll out of view are hidden rather than removed (up to 40), so scrolling back doesn't lay out their text again.
- While Visual Studio is idle, cards about a screen above and below the view are built one at a time, so scrolling finds them ready. Each editor keeps at most 120 built cards.
- Roslyn work (`<inheritdoc/>`, link tooltips, Go To Definition, quick documentation) runs on a background thread. `<inheritdoc/>` cards look up their inherited docs again only after typing pauses (0.6 s).

## Contributing

- Run the tests before sending a pull request, and add tests for parsing, rendering or Roslyn changes where you can.
- Changes to the editor behaviour need checking by hand in the experimental instance with the sample files.
- If you change `RenderedDocCommentsPackage.vsct`, raise the version in `[ProvideMenuResource("Menus.ctmenu", …)]` in `RenderedDocCommentsPackage.cs`: Visual Studio only reloads commands and key bindings when it changes.
- To add a setting, add it to `Options/registration.json` (Visual Studio 2026's Settings window; the schema is `Common7\IDE\UnifiedSettings\registration.schema.json` in a Visual Studio install), `UnifiedSettingsStore`, `ExtensionOptions` (with its classic store name, which the registration's `migration` keeps in step) and the classic `OptionsPage`. The registration tests check they agree.
