param(
    [string] $GameDirectory = (Join-Path $( [Environment]::GetFolderPath('ApplicationData') ) 'waxlight\versions\1.22.7')
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
foreach ($dependency in @('Lib\cairo-sharp.dll', 'Lib\SkiaSharp.dll', 'Lib\0Harmony.dll', 'Lib\Newtonsoft.Json.dll', 'VintagestoryAPI.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $GameDirectory $dependency)) | Out-Null
}
$survival = [Reflection.Assembly]::LoadFrom((Join-Path $GameDirectory 'Mods\VSSurvivalMod.dll'))
$mod = [Reflection.Assembly]::LoadFrom((Join-Path $projectRoot 'bin\Release\net10.0\SignFonts.dll'))
[Vintagestory.API.Config.RuntimeEnv]::GUIScale = 1
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$rendererType = $survival.GetType('Vintagestory.GameContent.BlockEntitySignRenderer', $true)
$entityType = $survival.GetType('Vintagestory.GameContent.BlockEntitySign', $true)

# Use the real vanilla renderer, but skip its graphics registration constructor.
# SetNewText updates its preview state without needing a graphics context.
$renderer = [System.Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($rendererType)
$entity = [System.Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($entityType)
$font = [Vintagestory.API.Client.CairoFont]::new(36, 'Georgia', [double[]] @(1, 0.2, 0.4, 1))
$rendererType.GetField('font', $flags).SetValue($renderer, $font)
$rendererType.GetField('fontSize', $flags).SetValue($renderer, [single]36)
$entityType.GetField('signRenderer', $flags).SetValue($entity, $renderer)
$entityType.GetField('color', $flags).SetValue($entity, [int]0)
$apply = $mod.GetType('SignFonts.SignFontsModSystem').GetMethod('ApplyFont', [Reflection.BindingFlags]'Static,NonPublic')

foreach ($scenario in @(
    @{ Saved = ''; Draft = "Try this text`nSecond line" },
    @{ Saved = 'Previously saved wording'; Draft = 'Changed text not yet saved' },
    @{ Saved = 'Previously saved wording'; Draft = '' }
)) {
    $entityType.GetField('text', $flags).SetValue($entity, $scenario.Saved)
    $rendererType.GetField('text', $flags).SetValue($renderer, $scenario.Draft)
    foreach ($family in @('Courier New', 'Arial', 'Georgia')) {
        $apply.Invoke($null, @($entity, $family)) | Out-Null
        if ($rendererType.GetField('text', $flags).GetValue($renderer) -cne $scenario.Draft) {
            throw 'Changing font replaced the live draft with saved text.'
        }
        if ($entityType.GetField('text', $flags).GetValue($entity) -cne $scenario.Saved) {
            throw 'Changing font changed the saved wording.'
        }
        $actualFont = $rendererType.GetField('font', $flags).GetValue($renderer)
        if ($actualFont.Fontname -ne $family) { throw 'The selected font was not applied.' }
        if ($rendererType.GetField('fontSize', $flags).GetValue($renderer) -ne 36) { throw 'The preview font size changed.' }
        $expectedColor = @(1, 0.2, 0.4, 1)
        for ($i = 0; $i -lt 4; $i++) {
            if ([Math]::Abs($actualFont.Color[$i] - $expectedColor[$i]) -gt (1.0 / 255)) { throw 'The preview colour changed.' }
        }
    }
}
'PASS: real vanilla renderer retains new, edited, multiline, and intentionally empty drafts across repeated font changes, preserving size and colour.'
