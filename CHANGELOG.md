# Changelog

## 1.0.0

First release.

- C# `///` documentation comments shown as cards: the summary, then labelled sections for parameters, return value, exceptions, remarks and examples, in the editor's fonts and theme colours.
- Switch any comment between card and raw text with its gutter icon, `Ctrl+M, Ctrl+Q`, or `Ctrl+M, Ctrl+W` for the whole file. Comments you switch to raw stay raw the next time you open the file.
- Clickable type and member links (Go To Definition), with their signatures on hover. Web links open in the browser.
- Code coloured with the theme's syntax colours: C#, XML, XAML, HTML and JSON.
- Every C# documentation tag and the common HTML ones: lists and tables, notes, headings, inline formatting, and the less common sections such as permissions and thread safety.
- `<inheritdoc/>` filled in, from source or .NET's documentation files, including `cref`, `path`, chains, and `<inheritdoc/>` inside sections.
- Quick documentation: `Ctrl+Shift+F1` shows the rendered documentation of any name at the cursor in a popup, .NET's included. Optionally on hover, in place of Visual Studio's tooltip.
- Cards in Peek Definition and side-by-side diffs.
- Settings in Visual Studio 2026's Settings window (the classic Options page in Visual Studio 2022).
