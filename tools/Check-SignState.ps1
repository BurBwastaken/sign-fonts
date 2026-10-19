param([string] $GameDirectory = (Join-Path $( [Environment]::GetFolderPath('ApplicationData') ) 'waxlight\versions\1.22.7'))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
foreach ($dependency in @('Lib\cairo-sharp.dll','Lib\SkiaSharp.dll','Lib\0Harmony.dll','Lib\Newtonsoft.Json.dll','VintagestoryAPI.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $GameDirectory $dependency)) | Out-Null
}
$survival = [Reflection.Assembly]::LoadFrom((Join-Path $GameDirectory 'Mods\VSSurvivalMod.dll'))
$mod = [Reflection.Assembly]::LoadFrom((Join-Path $projectRoot 'bin\Release\net10.0\SignFonts.dll'))
$systemType = $mod.GetType('SignFonts.SignFontsModSystem')
$staticFlags = [Reflection.BindingFlags]'Static,NonPublic'
$fields = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$entityType = $survival.GetType('Vintagestory.GameContent.BlockEntitySign')
$rendererType = $survival.GetType('Vintagestory.GameContent.BlockEntitySignRenderer')
$apply = $systemType.GetMethod('ApplyFont', $staticFlags)
$fontFor = $systemType.GetMethod('FontForEntity', $staticFlags)
$write = $systemType.GetMethod('WriteFontAttributes', $staticFlags)
$read = $systemType.GetMethod('ReadSignFont', $staticFlags)
$key = $systemType.GetMethod('BuildPositionKey', $staticFlags)

function New-SignFixture {
    $entity = [System.Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($entityType)
    $renderer = [System.Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($rendererType)
    $font = [Vintagestory.API.Client.CairoFont]::new(24, 'Lora', [double[]] @(0,0,0,1))
    $rendererType.GetField('font', $fields).SetValue($renderer, $font)
    $rendererType.GetField('fontSize', $fields).SetValue($renderer, [single]24)
    $rendererType.GetField('text', $fields).SetValue($renderer, 'Original sign text')
    $entityType.GetField('signRenderer', $fields).SetValue($entity, $renderer)
    $entityType.GetField('text', $fields).SetValue($entity, 'Original sign text')
    return @{ Entity = $entity; Renderer = $renderer }
}

$first = New-SignFixture
$second = New-SignFixture
$systemType.GetField('fontFamily', $staticFlags).SetValue($null, 'Courier New')
foreach ($fixture in @($first,$second)) {
    if ($null -ne $fontFor.Invoke($null, @($fixture.Entity))) { throw 'An untouched sign inherited the last-used font.' }
    $apply.Invoke($null, @($fixture.Entity,$null)) | Out-Null
    if ($rendererType.GetField('font', $fields).GetValue($fixture.Renderer).Fontname -ne 'Lora') { throw 'Untouched sign lost its vanilla font.' }
}

$tree = [Vintagestory.API.Datastructures.TreeAttribute]::new()
$tree.SetString('text', 'Original sign text')
$write.Invoke($null, @($tree,'Georgia')) | Out-Null
$read.Invoke($null, @($first.Entity,$tree.Clone())) | Out-Null
$family = $fontFor.Invoke($null, @($first.Entity))
if ($family -ne 'Georgia') { throw 'Explicit per-sign font did not survive attribute round-trip.' }
$apply.Invoke($null, @($first.Entity,$family)) | Out-Null
if ($null -ne $fontFor.Invoke($null, @($second.Entity))) { throw 'Choosing a font changed another sign.' }
if ($tree.GetString('text') -ne 'Original sign text') { throw 'Saving font attributes changed the wording.' }

# Previewing a different family must not commit it before Save.
$apply.Invoke($null, @($first.Entity,'Arial')) | Out-Null
if ($fontFor.Invoke($null, @($first.Entity)) -ne 'Georgia') { throw 'A preview committed the font selection.' }

$write.Invoke($null, @($tree,$null)) | Out-Null
$read.Invoke($null, @($first.Entity,$tree.Clone())) | Out-Null
$apply.Invoke($null, @($first.Entity,$fontFor.Invoke($null, @($first.Entity)))) | Out-Null
if ($rendererType.GetField('font', $fields).GetValue($first.Renderer).Fontname -ne 'Lora') { throw 'Default selection did not restore the original font.' }
if ($tree.HasAttribute('signfonts:family')) { throw 'Default selection left an explicit font on the sign.' }

$worldA = $key.Invoke($null, @('world-A',0,12,34,56))
$worldB = $key.Invoke($null, @('world-B',0,12,34,56))
$dimensionB = $key.Invoke($null, @('world-A',1,12,34,56))
if ($worldA -eq $worldB -or $worldA -eq $dimensionB) { throw 'Local fallback keys collide across worlds or dimensions.' }
if ($null -ne $key.Invoke($null, @($null,0,12,34,56))) { throw 'Missing world identity must not produce a shared key.' }
'PASS: untouched signs retain vanilla fonts; explicit choices round-trip independently; previews do not commit; Default restores vanilla; world and dimension keys are isolated.'
