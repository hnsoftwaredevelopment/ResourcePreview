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

Above the table you see a real control with the style applied. When the style reacts to `IsEnabled`, a disabled sample appears next to it.

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

When a key is defined more than once, the tooltip lists the other definitions as links.

## Settings

**Tools > Options > XAML Resource Preview**, in both the new and the legacy Options dialog.

| Setting | Default | |
|---|---|---|
| Language | Automatic | Follows the Visual Studio language. English, Chinese (Simplified and Traditional), Czech, Dutch, French, German, Italian, Japanese, Korean, Polish, Portuguese (Brazil), Russian, Spanish and Turkish are available. |
| Maximum number of other definitions | 5 | How many other definitions are listed as links. 0 hides the list. |
| Tile size | 64 | Size of a preview tile in pixels, 16 to 256. |
| Tiles | Light and dark | Both tiles, or only one of them. |
| Tile colors | | Foreground and background of each tile. The foreground is used for geometries, which have no color of their own. |
| Show live sample | On | The real control above a style table or instead of a template tree. |

## Limitations

- **Types from your own project.** Visual Studio runs on .NET Framework and cannot load the assemblies of your project or of NuGet packages. A resource that uses such a type (`local:MyControl`, `{x:Static local:Icons.Save}`) gets no picture. Styles and templates still show their table or tree, with a short note.
- **Resources outside the solution folder**, such as themes that come only as a compiled DLL, are not found.
- **Several definitions of one key.** The definition in the current file wins, then the others in alphabetical order. The extension does not follow the order of `MergedDictionaries`.
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
