# XAML Resource Preview

<img src="https://raw.githubusercontent.com/hnsoftwaredevelopment/ResourcePreview/main/art/icon-128.png" alt="XAML Resource Preview icon" width="96" align="right" />

Hover over `{StaticResource ...}` or `{DynamicResource ...}` in a XAML file and see what the resource looks like, right in the Quick Info tooltip. Icons, brushes and images appear as a picture. Styles appear as a table with a live sample. Templates, fonts, margins, effects and plain values each get a view that fits them.

Click the preview or the file name to jump to the definition.

Works in Visual Studio 2022 (17.14 and later) and Visual Studio 2026.

Version 1.5 adds checks in the Error List, refactorings, color swatches and a window with all resources. See the [changelog](https://github.com/hnsoftwaredevelopment/ResourcePreview/blob/main/CHANGELOG.md).

## Icons, brushes and images

Geometries, drawings, `DrawingImage`, `BitmapImage` and brushes are drawn on a light and a dark tile, so a white icon stays visible in a light theme and a black one in a dark theme. Below the tiles you see the type and size.

![Icon previews](https://raw.githubusercontent.com/hnsoftwaredevelopment/ResourcePreview/main/docs/images/icons.png)

Resources that depend on other resources work too. `Report` is a `DrawingImage` that uses a `DrawingGroup`, which uses a `Geometry` from another file. The preview finds the whole chain.

## Styles

A style is shown as a table of its setters, including the setters it inherits through `BasedOn`. Colors get a swatch, icons a small picture, and the triggers are listed by name.

Above the table you see a real control with the style applied. When the style has triggers on `IsMouseOver`, `IsPressed` or `IsEnabled`, a sample for each of those states appears next to it, so you see the hover and pressed look without moving the mouse. Templates that use the `VisualStateManager` (`MouseOver`, `Pressed`, `Disabled` states) work too.

![Style previews](https://raw.githubusercontent.com/hnsoftwaredevelopment/ResourcePreview/main/docs/images/styles.png)

## Templates

A `ControlTemplate` for a standard control is shown as a real control.

A `DataTemplate` is rendered with sample data, with its element tree and bindings below it, so you can check the controls, spacing and alignment without running the app. Each binding gets a value that fits the property it sets: `{Binding Title}` on a `TextBlock` shows "Title", a list (`ItemsSource`) gets three items, and other properties keep their default. Paths such as `Author.Name` work too. For your own sample values, use the design-time properties of the XAML designer, which WPF ignores at runtime:

```xml
<DataTemplate x:Key="PropertyRow">
    <TextBox Text="{Binding Value}" d:Text="120" IsReadOnly="{Binding IsReadOnly}" />
</DataTemplate>
```

The preview never loads types from your own project (see [Limitations](#limitations)). A control of your own (`local:Rating`) becomes a dashed box with its name, in the same place and size. A converter of your own is left out, so that binding shows the plain value. Converters from WPF itself, such as `BooleanToVisibilityConverter`, keep working.

Like an icon, the sample appears on the light and the dark tile from the settings, because a template often sets its own colors (`Foreground="White"` for a dark app) and only reads well on one of them. Set **Tiles** to Light or Dark if you only want one.

An `ItemsPanelTemplate` is shown with a few sample items in the panel, so you see how a `WrapPanel` or a horizontal `StackPanel` arranges them.

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

![Tooltip of an overridden key](https://raw.githubusercontent.com/hnsoftwaredevelopment/ResourcePreview/main/docs/images/overridden.png)

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

## Keys from a class (x:Static)

Keys can also be static members of a class, so code and XAML share them:

```xml
<DataTemplate x:Key="{x:Static res:TemplateKeys.ValueEditor}">
    <TextBox Text="{Binding Value}" />
</DataTemplate>

<ContentControl ContentTemplate="{StaticResource {x:Static res:TemplateKeys.ValueEditor}}" />
```

The preview, the XAML Resources window, Go To Resource Definition, Find Resource References and the Error List work with these keys as well. Two keys are the same when they name the same class and member, also when files use another prefix for the namespace. The extension does not load your assembly, so it cannot read the value of the member. A lookup from code, such as `FindResource(TemplateKeys.ValueEditor)`, is therefore not linked to the definition, and these keys are never reported as unused. A missing key is only reported when other keys of that class are defined in XAML, so WPF keys such as `{x:Static SystemColors.ControlBrushKey}` are left alone. Rename Resource Key is not available for them: the key is the name of a member in your code.

## References WPF will not find

A `{StaticResource}` that WPF cannot resolve compiles fine and only fails when the app loads the XAML. The extension reports these in the Error List, and the tooltip on the reference says what is wrong:

| Problem | Example |
|---|---|
| The key does not exist | `{StaticResource AccentBrsh}`. If only the case differs, the message suggests the right key. |
| The key is only defined further down in the same file | A style that uses `LateBrush` above `<SolidColorBrush x:Key="LateBrush">`. A StaticResource does not look ahead. |
| The key is in a file that is not loaded there | The key is only in a dictionary that nothing merges, or in the resources of another window. |

A `{DynamicResource}` is looked up again at runtime and code can still add the key, so only a missing key is reported, as a message instead of a warning.

![Error List with overridden keys, references WPF will not find and unused resources](https://raw.githubusercontent.com/hnsoftwaredevelopment/ResourcePreview/main/docs/images/errorlist.png)

To avoid false alarms, a missing key is not reported when `App.xaml` merges a dictionary from outside the solution (such as a NuGet theme) or when there is no `App.xaml` (a class library). "Not loaded" is not reported for a `UserControl`, because at runtime it also sees the resources of the window it is placed in.

## Unused resources

Resources that nothing uses show up as messages in the Error List (turn on **Messages** in its toolbar). A resource counts as used when a `{StaticResource}` or `{DynamicResource}` in any `.xaml` file refers to it, or when its key appears as a string in a `.cs` or `.vb` file, such as `FindResource("AccentBrush")`.

Only files the app loads are checked: `App.xaml`, windows and controls, and dictionaries that something merges. Dictionaries nothing merges, theme files such as `Themes\Generic.xaml`, and solutions without `App.xaml` are skipped, because the resources there may be used outside the solution.

## IntelliSense

After `{StaticResource ` or `{DynamicResource `, select an item in the IntelliSense list and its tooltip shows the same preview as on hover, also for styles and templates.

![IntelliSense with the preview of a brush](https://raw.githubusercontent.com/hnsoftwaredevelopment/ResourcePreview/main/docs/images/intellisense.png)

## Go To Resource Definition

Right-click a `{StaticResource ...}` or `{DynamicResource ...}` reference and choose **Go To Resource Definition**, or press **Ctrl+Alt+F12**. The file with the definition opens and the key is selected. The menu item only appears when the caret is on a resource reference.

When the key is defined more than once, the command opens the definition WPF uses (see below) and the status bar shows how many others there are. Hover over the reference to see them all. You can assign another shortcut under **Tools > Options > Environment > Keyboard**: search for `ResourcePreview.GoToResourceDefinition`.

## Find Resource References

Right-click a reference or an `x:Key` and choose **Find Resource References**, or press **Shift+Alt+F12**. The **Resource References** window lists the definitions of the key and every `{StaticResource}` and `{DynamicResource}` that uses it, grouped by file. Double-click a line (or press Enter) to open it.

![The Resource References window](https://raw.githubusercontent.com/hnsoftwaredevelopment/ResourcePreview/main/docs/images/references.png)

When a key has several definitions, the list shows where a use gets another definition than the one you started from ("uses the definition in Values.xaml"), or where WPF does not find the key at all.

## Rename Resource Key

Right-click a reference or an `x:Key` and choose **Rename Resource Key...**. Enter the new name and the key changes in every definition and every `{StaticResource}`, `{DynamicResource}` and `ResourceKey=` in the solution. The dialog says how many places change, and refuses a name that is already taken or contains characters that break the XAML.

Files that are open in an editor are changed but not saved, so you can check them and undo with Ctrl+Z. Files that are not open are saved right away. Uses in code, such as `FindResource("AccentBrush")`, are not renamed. There is no default shortcut; assign one under **Tools > Options > Environment > Keyboard** (`ResourcePreview.RenameResourceKey`).

## Extract to resource

Put the caret in a fixed value, such as `Background="#FF2D7D9A"` or `Margin="4,2"`, and press **Ctrl+.** (the light bulb), or right-click and choose **Extract to Resource...**. Enter a key and pick the dictionary. The value becomes a resource in that dictionary, and the attribute becomes `{StaticResource Key}`.

![The light bulb on a fixed color](https://raw.githubusercontent.com/hnsoftwaredevelopment/ResourcePreview/main/docs/images/extract.png)

| Property | Resource |
|---|---|
| `Background`, `Foreground`, `BorderBrush`, `Fill`, `Stroke` with a color | `SolidColorBrush` |
| `Color` | `Color` |
| `Margin`, `Padding`, `BorderThickness` | `Thickness` |
| `CornerRadius` | `CornerRadius` |
| `FontSize`, `Width`, `Height`, `Opacity` ... | `sys:Double` (adds `xmlns:sys` when needed) |
| `FontFamily`, `FontWeight` | `FontFamily`, `FontWeight` |
| `Text`, `Content`, `Header`, `ToolTip`, `Title` | `sys:String` |

The list of dictionaries contains the ones this file can see: its own merged dictionaries and those of `App.xaml`, and `App.xaml` itself. When the value is in a dictionary and you add the resource to that same dictionary, it goes above the place where it is used, because a StaticResource does not look ahead.

### Several attributes into one Style

Select some attributes in a start tag, for example `Fill="White" Stretch="Uniform" Width="16" Height="16"`, and press **Ctrl+.** or right-click:

- **Extract to Style...** creates a new `Style` with a `Setter` for each selected attribute and `TargetType` set to the element. The attributes are replaced by `Style="{StaticResource Key}"`. If the element already had a style, the new one is `BasedOn` it.
- **Add to style 'X'** appears when the element already has `Style="{StaticResource X}"`. The selected attributes become setters of that existing style, wherever it is defined. A setter for a property the style already sets is replaced, because a style may set each property only once.

Event handlers such as `Click="OnClick"`, `x:Name` and `x:Key` stay on the element. Elements with a prefix (`local:MyControl`) are not supported, because the target file may not know that prefix.

## Color swatches in the editor

A small square before each color value in a .xaml file shows the color, without hovering:

- literal colors in brush and color properties: `Background="White"`, `Fill="#2D7D9A"`, `Color="#80FF0000"`, and `Value="White"` in a `Setter` for such a property;
- references: `Foreground="{StaticResource AccentBrush}"` shows the color of `AccentBrush` when it is a `SolidColorBrush` or a `Color`, also when the brush refers to a `Color` resource;
- `<Color x:Key="Accent">#FF2D7D9A</Color>`.

![Color swatches in a dictionary](https://raw.githubusercontent.com/hnsoftwaredevelopment/ResourcePreview/main/docs/images/swatches.png)

Semi-transparent colors show on a checkerboard. Gradients and brushes with an `Opacity` get no swatch. Hover over a swatch to see the color code. Turn the swatches off in the settings.

Visual Studio has color swatches of its own: **Tools > Options > Languages > XAML > Advanced > Show inline color previews**. With both on, you see two swatches per color: the one from Visual Studio before the quote, this one after it. Turn off the one you like least. The swatches of this extension also show the resource key and color code when you hover over them.

## Find an existing resource

You type `Fill="White"`, but a `WhiteBrush` already exists. Select some attributes (or put the caret in one) and choose **Find Matching Resource...** from the context menu or the light bulb. A list shows the existing resources that fit; nothing changes until you pick one and click **Apply**.

![Find Matching Resource on a button](https://raw.githubusercontent.com/hnsoftwaredevelopment/ResourcePreview/main/docs/images/findmatching.png)

- **Styles** fit when every setter they have, including those from `BasedOn`, is one of the selected attributes with the same value. Selected attributes the style does not set stay on the element. A style that sets more than you selected is not shown, because it would change the look. Styles with triggers are shown with the number of triggers.
- **Single values** fit one attribute: a `SolidColorBrush`, `Color`, `Thickness`, `CornerRadius`, `sys:Double`, `FontFamily`, `FontWeight` or `sys:String` with the same value.
- Values are compared, not text: `White` equals `#FFFFFFFF`, `8` equals `8,8,8,8` for a margin, `16` equals `16.0`. A setter with `{StaticResource AccentBrush}` matches the color of `AccentBrush`.
- The style's `TargetType` must fit the element: a style for `Shape` fits a `Path`.
- Only resources the element can use are searched: the lookup order of the file, and in the file itself only resources above the element.
- An element that already has a `Style` only gets single values.

## XAML Resources window

**View > Other Windows > XAML Resources** lists the resources of the solution.

![The XAML Resources window, by file and by type](https://raw.githubusercontent.com/hnsoftwaredevelopment/ResourcePreview/main/docs/images/xamlresourceswindow.png)

- **By file** shows the dictionaries in the order WPF searches them from `App.xaml`, then the other files. **By type** groups them as brushes, colors, images and geometries, styles, templates and values.
- **Only what the active file can use** follows the active .xaml file: its own resources, its merged dictionaries and those of `App.xaml`, one definition per key, the one WPF picks.
- Each resource shows a small preview, its type, and the same notes as the Error List: overridden by another file, not in any MergedDictionaries, unused.
- Type in the search box to filter by key. Hover for the full preview. Double-click or press Enter to open the definition.
- Right-click for Go To Resource Definition, Find Resource References, Rename Resource Key and Copy `{StaticResource Key}`.
- Drag a resource into the editor to insert `{StaticResource Key}` where you drop it.

The window refreshes when a .xaml file is saved, or with the refresh button.

## Settings

**Tools > Options > XAML Resource Preview**, in both the new and the legacy Options dialog.

| Setting | Default | |
|---|---|---|
| Language | Automatic | Follows the Visual Studio language. English, Chinese (Simplified and Traditional), Czech, Dutch, French, German, Italian, Japanese, Korean, Polish, Portuguese (Brazil), Russian, Spanish and Turkish are available. |
| Maximum number of other definitions | 5 | How many other definitions are listed as links. 0 hides the list. |
| Warn about overridden keys | On | Warnings in the Error List for definitions WPF never uses. |
| Check resource references | On | Error List entries for references WPF will not resolve. |
| Report unused resources | On | Error List messages for resources nothing uses. |
| Show previews in IntelliSense | On | The preview tooltip in the IntelliSense list. |
| Tile size | 64 | Size of a preview tile in pixels, 16 to 256. |
| Tiles | Light and dark | Both tiles, or only one of them. |
| Tile colors | | Foreground and background of each tile. The foreground is used for geometries, which have no color of their own. |
| Show live sample | On | The real control above a style table or instead of a template tree, and the rendered `DataTemplate` and `ItemsPanelTemplate`. |
| Show color swatches in the editor | On | Color squares before color values and color references in .xaml files. |

## Limitations

- **Types from your own project.** Visual Studio runs on .NET Framework and cannot load the assemblies of your project or of NuGet packages. A resource that uses such a type (`local:MyControl`, `{x:Static local:Icons.Save}`) gets no picture. Styles and templates still show their table or tree, with a short note.
- **Resources outside the solution folder**, such as themes that come only as a compiled DLL, are not found.
- **Scopes inside a file.** The whole current file counts as one scope. A key in `Grid.Resources` therefore wins over `App.xaml`, also for elements outside that grid.
- **Resources added in code** (`Application.Current.Resources.MergedDictionaries.Add(...)`) are not followed. Such a file is shown as "not in any MergedDictionaries".
- **A UserControl** sees the resources of the window it is placed in at runtime. The extension does not know that window, and only follows the UserControl itself and `App.xaml`.
- **References that span several lines** are not recognized.
- **Safe preview.** The preview never creates types that run code or load content when they are created (`ObjectDataProvider`, `XmlDataProvider`, `Frame`, `WebBrowser`), and only loads types from WPF itself and the `System` namespace (`sys:Double`, `sys:String`). A .xaml file from a repository you just cloned cannot run code in Visual Studio through this extension.
- **Keys built in code** (`$"{name}Brush"`) are not seen, so such a resource is reported as unused.
- **Other markup as a key**, such as `x:Key="{x:Type Button}"` or `{ComponentResourceKey ...}`, is not supported. `{x:Static ...}` keys are; see above.

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
