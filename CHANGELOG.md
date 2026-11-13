# Changelog

## Unreleased

- **Keys from a class.** Resources with a key such as `x:Key="{x:Static res:TemplateKeys.ValueEditor}"` now get a preview on hover and show up in the XAML Resources window. Go To Resource Definition, Find Resource References and the Error List work with them too. Keys match on class and member, so another namespace prefix in another file is fine.

## 1.5.0

Hover previews were only the start. This version helps you keep resource dictionaries correct and tidy.

### Find and check

- **Which definition wins.** When a key is defined more than once, the tooltip shows the definition WPF uses, following `MergedDictionaries` and `App.xaml` the way WPF does, and marks the others as overridden or as not in any `MergedDictionaries`.
- **Error List.** Warnings for overridden keys and for references WPF will not find at runtime (missing keys, with a suggestion when only the case differs; keys defined further down; keys in a file that is not loaded). Messages for resources nothing uses, in XAML or in code. Updates while you type, also before you save.
- **Go To Resource Definition** (Ctrl+Alt+F12) and **Find Resource References** (Shift+Alt+F12), with a list of all definitions and uses.
- **IntelliSense.** The tooltip of an item after `{StaticResource ` shows the same preview as on hover.
- **XAML Resources window** (View > Other Windows): all resources of the solution, or only what the active file can use, by file or by type, with search, previews, the Error List notes, and drag and drop into the editor.

### Change

- **Rename Resource Key** in every definition and reference in the solution.
- **Extract to Resource** turns a fixed value, also in a `Setter`, into a resource in a dictionary of your choice.
- **Extract to Style** and **Add to Style** move selected attributes into a new or an existing style.
- **Find Matching Resource** finds existing styles and values that fit the selected attributes, and applies the one you pick.
- All changes go through the editor: open files stay unsaved and can be undone.

### See

- **Color swatches** before color values and color references in the code. Visual Studio has its own (Languages > XAML > Advanced > Show inline color previews); turn off the one you like least.
- **Live samples** of styles show Normal, Mouse over and Pressed when the style has triggers for them.
- New settings for the live sample, the Error List checks, IntelliSense and the color swatches.

### Safety and speed

- The preview never loads types that run code or load content when they are created, such as `ObjectDataProvider`, so a .xaml file from a repository you just cloned cannot run code through this extension.
- File watchers and caches are released when the solution closes; scans of large solutions reuse the list of code files.

## 1.0.0

First release: previews of icons, brushes, images, styles, templates, fonts, margins and values in the Quick Info tooltip, in 15 languages, with settings in both Options windows of Visual Studio.
