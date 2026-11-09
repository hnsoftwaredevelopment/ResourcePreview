# XAML Resource Preview

<img src="https://raw.githubusercontent.com/hnsoftwaredevelopment/ResourcePreview/main/art/icon-128.png" alt="XAML Resource Preview icon" width="96" align="right" />

Hover over `{StaticResource ...}` or `{DynamicResource ...}` in a XAML file and see what the resource looks like, right in the Quick Info tooltip. Icons, brushes and images appear as a picture. Styles appear as a table with a live sample. Templates, fonts, margins, effects and plain values each get a view that fits them.

Click the preview or the file name to jump to the definition.

Works in Visual Studio 2022 (17.14 and later) and Visual Studio 2026.

## Icons, brushes and images

Geometries, drawings, `DrawingImage`, `BitmapImage` and brushes are drawn on a light and a dark tile, so a white icon stays visible in a light theme and a black one in a dark theme. Below the tiles you see the type and size.

![Icon previews](https://raw.githubusercontent.com/hnsoftwaredevelopment/ResourcePreview/main/docs/images/icons.png)

Resources that depend on other resources work too. `Report` is a `DrawingImage` that uses a `DrawingGroup`, which uses a `Geometry` from another file. The preview finds the whole chain.

## Styles

A style is shown as a table of its setters, including the setters it inherits through `BasedOn`. Colors get a swatch, icons a small picture, and the triggers are listed by name.

Above the table you see a real control with the style applied. When the style has triggers on `IsMouseOver`, `IsPressed` or `IsEnabled`, a sample for each of those states appears next to it, so you see the hover and pressed look without moving the mouse. Templates that use the `VisualStateManager` (`MouseOver`, `Pressed`, `Disabled` states) work too.

![Style previews](https://raw.githubusercontent.com/hnsoftwaredevelopment/ResourcePreview/main/docs/images/styles.png)

## Templates

A `ControlTemplate` for a standard control is shown as a real control. A `DataTemplate` needs data to show anything, so it is shown as an element tree with its bindings instead.

![Template previews](https://raw.githubusercontent.com/hnsoftwaredevelopment/ResourcePreview/main/docs/images/templates.png)

## Fonts, margins and values

| Resource | Preview |
|---|---|
| `FontFamily` | Sample text in that font, regular and bold, with a warning when the font is not installed |
| `Thickness` | A sketch of the four edges, with the values |
| `CornerRadius` | A rectangle with those corners, with the values |
| `DropShadowEffect`, `BlurEffect` | A square with the effect applied |
| `sys:Double`, `sys:String`, `sys:Boolean`, enums and structs | The value, as you would write it in XAML |

![Value previews](https://raw.githubusercontent.com/hnsoftwaredevelopment/ResourcePreview/main/docs/images/values.png)

## Finding the definition

The extension reads every `.xaml` file in the solution folder, so it finds resources in other files and in other projects of the solution. Image paths such as `pack://application:,,,/Images/save.png` and `/MyLib;component/Fonts/#Inter` are resolved to the file on disk. Unsaved changes in the open editor count too.

## Which definition wins

When a key is defined more than once, the tooltip shows the definition WPF actually uses, and lists the others as links:

```
Resources\Values.xaml, line 12
WPF uses this definition here.
Also defined in:
Resources\Brushes.xaml, line 9  · overridden by Values.xaml
```

The extension follows the `MergedDictionaries` the way WPF does:

1. The current file comes first. Its own keys win over what it merges.
2. Then its `MergedDictionaries`, the last one first, each with its own merges.
3. Then `App.xaml`, in the same way.

A definition in a dictionary that nothing merges is marked "not in any MergedDictionaries". WPF only loads such a file when code adds it. `Themes\Generic.xaml` is not marked, because WPF loads it by itself.

## Overridden keys in the Error List

If `App.xaml` (or a window) merges two dictionaries that define the same key, WPF only uses the one from the dictionary merged last. Changes to the other one never show up. The extension lists each definition like that as a warning in the Error List:

```
Resource 'InfoBrush' is overridden by the definition in Resources\Values.xaml (line 12)
when loaded through App.xaml. WPF does not use this definition.
```

Double-click the warning to open the definition. The list is updated after you save a `.xaml` file. It is not reported when a window overrides a key from `App.xaml` on purpose, or when the same dictionary is merged twice. You can turn the warnings off in the settings.

## IntelliSense

After `{StaticResource ` or `{DynamicResource ` the IntelliSense list shows a small icon for images, geometries, drawings, brushes and colors. Select an item and its tooltip shows the same preview as on hover, also for styles and templates.

## Go To Resource Definition

Right-click a `{StaticResource ...}` or `{DynamicResource ...}` reference and choose **Go To Resource Definition**, or press **Ctrl+Alt+F12**. The file with the definition opens and the key is selected. The menu item only appears when the caret is on a resource reference.

When the key is defined more than once, the command opens the definition WPF uses (see below) and the status bar shows how many others there are. Hover over the reference to see them all. You can assign another shortcut under **Tools > Options > Environment > Keyboard**: search for `ResourcePreview.GoToResourceDefinition`.

## Settings

**Tools > Options > XAML Resource Preview**, in both the new and the legacy Options dialog.

| Setting | Default | |
|---|---|---|
| Language | Automatic | Follows the Visual Studio language. English, Chinese (Simplified and Traditional), Czech, Dutch, French, German, Italian, Japanese, Korean, Polish, Portuguese (Brazil), Russian, Spanish and Turkish are available. |
| Maximum number of other definitions | 5 | How many other definitions are listed as links. 0 hides the list. |
| Warn about overridden keys | On | Warnings in the Error List for definitions WPF never uses. |
| Show previews in IntelliSense | On | Icons and the preview tooltip in the IntelliSense list. |
| Tile size | 64 | Size of a preview tile in pixels, 16 to 256. |
| Tiles | Light and dark | Both tiles, or only one of them. |
| Tile colors | | Foreground and background of each tile. The foreground is used for geometries, which have no color of their own. |
| Show live sample | On | The real control above a style table or instead of a template tree. |

## Limitations

- **Types from your own project.** Visual Studio runs on .NET Framework and cannot load the assemblies of your project or of NuGet packages. A resource that uses such a type (`local:MyControl`, `{x:Static local:Icons.Save}`) gets no picture. Styles and templates still show their table or tree, with a short note.
- **Resources outside the solution folder**, such as themes that come only as a compiled DLL, are not found.
- **Scopes inside a file.** The whole current file counts as one scope. A key in `Grid.Resources` therefore wins over `App.xaml`, also for elements outside that grid.
- **Resources added in code** (`Application.Current.Resources.MergedDictionaries.Add(...)`) are not followed. Such a file is shown as "not in any MergedDictionaries".
- **A UserControl** sees the resources of the window it is placed in at runtime. The extension does not know that window, and only follows the UserControl itself and `App.xaml`.
- **References that span several lines** are not recognized.

## Building from source

Requirements: Visual Studio 2022 17.14 or later, or Visual Studio 2026, with the **Visual Studio extension development** workload.

1. Open `ResourcePreview.sln` and press **F5**. A second Visual Studio instance (the Experimental Instance) starts with the extension loaded.
2. In that instance, open `samples/SampleApp/SampleApp.sln` and hover over the resource names in `MainWindow.xaml`.

| Folder | Contents |
|---|---|
| `src/ResourcePreview` | The extension |
| `tests/ResourcePreview.Tests` | Unit tests (MSTest), run from Test Explorer |
| `samples/SampleApp` | A WPF app with a resource of every supported kind |
| `tools` | `build-and-test.ps1` builds and runs all tests from the command line. `render-previews.ps1` renders the images in this README. |
| `Builds` | Build output of every project (not in Git). The VSIX is in `Builds/ResourcePreview/Release/`. |
| `art` | Icon sources |
| `docs/images` | The images in this README |

`publishManifest.json` describes the Marketplace listing for `VsixPublisher.exe`. Fill in your publisher ID before the first upload.

## License

[MIT](LICENSE)
