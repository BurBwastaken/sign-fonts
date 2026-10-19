param(
    [string] $GameDirectory = 'C:\Users\ewant\AppData\Roaming\waxlight\versions\1.22.7'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
foreach ($dependency in @('Lib\cairo-sharp.dll', 'Lib\SkiaSharp.dll', 'Lib\0Harmony.dll', 'Lib\Newtonsoft.Json.dll', 'VintagestoryAPI.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $GameDirectory $dependency)) | Out-Null
}
$modAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $projectRoot 'bin\Release\net10.0\SignFonts.dll'))
[Vintagestory.API.Config.RuntimeEnv]::GUIScale = 1

# Exercise the actual Vintage Story composer without creating a graphics context.
$root = [Vintagestory.API.Client.ElementBounds]::Fixed(0, 0, 300, 300).WithEmptyParent()
$content = [Vintagestory.API.Client.ElementBounds]::Fill.WithFixedPadding(10)
$content.BothSizing = [Vintagestory.API.Client.ElementSizing]::FitToChildren
$root.WithChild($content) | Out-Null
$composerConstructor = [Vintagestory.API.Client.GuiComposer].GetConstructors(
    [Reflection.BindingFlags]'Instance,Public,NonPublic') | Where-Object { $_.GetParameters().Count -eq 3 } | Select-Object -First 1
$composer = $composerConstructor.Invoke(@($null, $root, 'signfonts-regression'))
$composer.BeginChildElements($content) | Out-Null
$anchor = [Vintagestory.API.Client.ElementBounds]::Fixed(140, 120, 100, 20)
$composer.AddStaticElement([Vintagestory.API.Client.GuiElementStaticText]::new(
    $null, 'Save', [Vintagestory.API.Client.EnumTextOrientation]::Left,
    $anchor, [Vintagestory.API.Client.CairoFont]::WhiteDetailText()), 'save') | Out-Null
$composer.EndChildElements() | Out-Null
$composer.Composed = $true
$catalog = $modAssembly.GetType('SignFonts.WindowsFontCatalog')
$choices = [string[]] $catalog.GetMethod('GetInstalledFamilies', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, @())
if ($choices.Count -lt 2) { $choices = [string[]] @('Georgia', 'Courier New') }
$callback = [Vintagestory.API.Client.SelectionChangedDelegate] { param($family, $active) }

# Reproduce the bug: the game's helper ignores additions to an already composed GUI.
[Vintagestory.API.Client.GuiComposerHelpers]::AddDropDown(
    $composer, $choices, $choices, 0, $callback,
    [Vintagestory.API.Client.ElementBounds]::Fixed(0, 170, 250, 28), 'old-picker') | Out-Null
if ($null -ne $composer.GetElement('old-picker')) { throw 'The regression fixture did not reproduce the original failure.' }

# Run the helper from the built mod and verify real controls and their layout parent.
$method = $modAssembly.GetType('SignFonts.SignFontsModSystem').GetMethod(
    'AddFontPickerControls', [Reflection.BindingFlags]'Static,NonPublic')
$method.Invoke($null, @($composer, $choices, 1, $callback)) | Out-Null
$picker = $composer.GetElement('signfonts-picker')
if ($picker -isnot [Vintagestory.API.Client.GuiElementDropDown]) { throw 'Font dropdown was not created.' }
if ($null -eq $composer.GetElement('signfonts-label')) { throw 'Font label was not created.' }
if (-not [object]::ReferenceEquals($picker.Bounds.ParentBounds, $content)) { throw 'Dropdown has the wrong layout parent.' }
if ($composer.Composed) { throw 'Composer must remain open for recomposition.' }
$root.CalcWorldBounds()
if ($content.OuterHeight -lt ($picker.Bounds.fixedY + $picker.Bounds.fixedHeight)) { throw 'Dialog content does not contain the dropdown.' }
'PASS: reproduced the original failure; the built mod creates the dropdown and label inside the sign editor content bounds.'
