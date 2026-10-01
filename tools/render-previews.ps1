<#
  Renders Quick Info previews of SampleApp resources to a PNG, outside Visual Studio.
  Used for the images in README.md. Build the solution (Debug) first.

  Example:
    powershell -STA -File tools\render-previews.ps1 -Keys Renew,Report -Output docs\images\icons.png -Theme Dark
#>
param(
    [Parameter(Mandatory = $true)][string[]]$Keys,
    [Parameter(Mandatory = $true)][string]$Output,
    [ValidateSet('Dark', 'Light')][string]$Theme = 'Dark',
    [string]$Culture = 'en',
    [int]$Columns = 2,
    [int]$CardWidth = 440
)

$ErrorActionPreference = 'Stop'
# With -File, a comma-separated list arrives as one string.
$Keys = $Keys | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo($Culture)

$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $root 'Builds\ResourcePreview.Tests\Debug\net48'
[AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($s, $e)
    $file = Join-Path $bin ((New-Object Reflection.AssemblyName $e.Name).Name + '.dll')
    if (Test-Path $file) { [Reflection.Assembly]::LoadFrom($file) }
})

$asm = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'ResourcePreview.dll'))
$shell = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'Microsoft.VisualStudio.Shell.15.0.dll'))
$flags = [Reflection.BindingFlags]'Static,Public,NonPublic'
$instance = $flags -bor [Reflection.BindingFlags]::Instance
function T($name) { $asm.GetType($name, $true) }

(T 'ResourcePreview.Localization.Localizer').GetMethod('SetCulture', $flags).Invoke($null, @([Globalization.CultureInfo]::GetCultureInfo($Culture)))

$main = [string](Join-Path $root 'samples\SampleApp\MainWindow.xaml')
$index = (T 'ResourcePreview.Resources.ResourceIndex').GetMethod('ForFile', $flags).Invoke($null, @($main))
$ct = [Threading.CancellationToken]::None
$styleBuilder = T 'ResourcePreview.Resources.StyleInfoBuilder'
$treeBuilder = T 'ResourcePreview.Resources.TemplateTreeBuilder'
$isStyle = $styleBuilder.GetMethods($flags) | Where-Object { $_.Name -eq 'IsStyle' } | Select-Object -First 1
$isTemplate = $treeBuilder.GetMethods($flags) | Where-Object { $_.Name -eq 'IsTemplate' -and $_.GetParameters()[0].ParameterType.Name -eq 'ResourceEntry' }
$xamlBuilder = (T 'ResourcePreview.Resources.PreviewXamlBuilder').GetMethod('Build', $flags)
$render = (T 'ResourcePreview.Preview.PreviewRenderer').GetMethod('Render', $flags)
$dataType = T 'ResourcePreview.Preview.ResourcePreviewData'

# Colors of the VS tooltip in the Dark and Light themes.
if ($Theme -eq 'Dark') { $back = '#FF252526'; $fore = '#FFF1F1F1'; $border = '#FF3F3F46'; $keyword = '#FF569CD6'; $link = '#FF9CDCFE'; $page = '#FF1E1E1E' }
else { $back = '#FFF6F6F6'; $fore = '#FF1E1E1E'; $border = '#FFCCCEDB'; $keyword = '#FF0000FF'; $link = '#FF0066BF'; $page = '#FFFFFFFF' }
function B($c) { New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString($c)) }

$textKey = $shell.GetType('Microsoft.VisualStudio.PlatformUI.EnvironmentColors').GetProperty('ToolTipTextBrushKey').GetValue($null)

$wrap = New-Object Windows.Controls.WrapPanel -Property @{ Width = ($CardWidth + 16) * $Columns + 16; Background = (B $page) }
$wrap.Resources.Add($textKey, (B $fore).psobject.BaseObject)
$wrap.Margin = '0'

foreach ($key in $Keys) {
    $entry = $index.Find($key, $main, $ct)[0]
    $style = $null; $tree = $null
    if ($isStyle.Invoke($null, @($entry))) { $style = $styleBuilder.GetMethod('Build', $flags).Invoke($null, @($index, $entry, $ct)) }
    if ($isTemplate.Invoke($null, @($entry))) { $tree = $treeBuilder.GetMethod('Build', $flags).Invoke($null, @($entry.Element)) }
    $xaml = $xamlBuilder.Invoke($null, @($index, $entry, $ct))
    $data = [Activator]::CreateInstance($dataType, $instance, $null, @($entry.Key, $entry.ElementTypeName, $xaml.Xaml, $xaml.MissingKeys, $null, $style, $tree, $xaml.UnloadableType), $null)
    $ui = $render.Invoke($null, @($data))

    # Header and location link, drawn the way Quick Info shows them.
    $header = New-Object Windows.Controls.TextBlock -Property @{ FontFamily = 'Consolas'; FontSize = 13 }
    $header.Inlines.Add((New-Object Windows.Documents.Run -Property @{ Text = $entry.ElementTypeName + ' '; Foreground = (B $keyword) }))
    $header.Inlines.Add((New-Object Windows.Documents.Run -Property @{ Text = $entry.Key; Foreground = (B $fore) }))
    $relative = $entry.FilePath.Substring((Join-Path $root 'samples\SampleApp').Length + 1)
    $location = New-Object Windows.Controls.TextBlock -Property @{ Text = "$relative, line $($entry.LineNumber)"; Foreground = (B $link); TextDecorations = [Windows.TextDecorations]::Underline; Margin = '0,6,0,0' }

    $column = New-Object Windows.Controls.StackPanel -Property @{ Margin = '10,8,10,10' }
    [void]$column.Children.Add($header); [void]$column.Children.Add($ui); [void]$column.Children.Add($location)
    $card = New-Object Windows.Controls.Border -Property @{ Child = $column; Background = (B $back); BorderBrush = (B $border); BorderThickness = '1'; Margin = '8'; Width = $CardWidth; VerticalAlignment = 'Top' }
    [void]$wrap.Children.Add($card)
}

$wrap.Measure((New-Object Windows.Size $wrap.Width, ([double]::PositiveInfinity)))
$wrap.Arrange((New-Object Windows.Rect $wrap.DesiredSize))
$wrap.UpdateLayout()
$bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap ([int]($wrap.DesiredSize.Width * 2)), ([int]($wrap.DesiredSize.Height * 2)), 192, 192, ([Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($wrap)
$encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$outputPath = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }
New-Item -ItemType Directory -Force (Split-Path $outputPath) | Out-Null
$stream = [IO.File]::Create($outputPath); $encoder.Save($stream); $stream.Close()
"Saved $outputPath ($([int]$wrap.DesiredSize.Width) x $([int]$wrap.DesiredSize.Height))"
