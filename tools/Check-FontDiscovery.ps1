param(
    [string] $GameDirectory = (Join-Path $( [Environment]::GetFolderPath('ApplicationData') ) 'waxlight\versions\1.22.7')
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
foreach ($dependency in @('Lib\cairo-sharp.dll', 'Lib\SkiaSharp.dll', 'Lib\0Harmony.dll', 'Lib\Newtonsoft.Json.dll', 'VintagestoryAPI.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $GameDirectory $dependency)) | Out-Null
}
$mod = [Reflection.Assembly]::LoadFrom((Join-Path $projectRoot 'bin\Release\net10.0\SignFonts.dll'))
[Vintagestory.API.Config.RuntimeEnv]::GUIScale = 1
$catalogType = $mod.GetType('SignFonts.WindowsFontCatalog', $true)
$staticFlags = [Reflection.BindingFlags]'Static,NonPublic'
$instanceFlags = [Reflection.BindingFlags]'Instance,NonPublic'
$enumerate = $catalogType.GetMethod('GetInstalledFamilies', $staticFlags)
$before = [string[]] $enumerate.Invoke($null, @())
if ($before.Count -le 5 -or $before -notcontains 'Segoe UI') { throw 'Windows installed fonts were not discovered.' }
if (($before | Sort-Object -Unique).Count -ne $before.Count) { throw 'Font names contain duplicates.' }
if ($before | Where-Object { $_.StartsWith('@') -or [string]::IsNullOrWhiteSpace($_) }) { throw 'Invalid font names were included.' }

$catalog = [Activator]::CreateInstance($catalogType, $true)
try {
    $load = $catalogType.GetMethod('RegisterDirectory', $instanceFlags)
    # Register the game's own font files privately as a real local-folder import.
    # The test changes only this verification process, not the user's Windows installation.
    $failures = [string[]] $load.Invoke($catalog, @([string](Join-Path $GameDirectory 'assets\game\fonts')))
    if ($failures.Count -ne 0) { throw ('Private font imports failed: ' + ($failures -join ', ')) }
    $after = [string[]] $enumerate.Invoke($null, @())
    foreach ($name in @('Almendra', 'Montserrat', 'Lora')) {
        if ($after -notcontains $name) { throw ('Imported font is missing from the dropdown catalog: ' + $name) }
    }
    $registered = $catalogType.GetField('registeredFiles', $instanceFlags).GetValue($catalog)
    if ($registered.Count -lt 3) { throw 'The folder importer did not register the font files.' }
    $register = $catalogType.GetMethod('RegisterFontFile', $instanceFlags)
    if ($register.Invoke($catalog, @([string](Join-Path $GameDirectory 'missing-font-file.ttf')))) { throw 'A nonexistent font was accepted.' }
    $count = $registered.Count
    $register.Invoke($catalog, @([string](Join-Path $GameDirectory 'assets\game\fonts\Lora-Regular.ttf'))) | Out-Null
    if ($registered.Count -ne $count) { throw 'Repeated imports registered the same font twice.' }

    # Confirm the game's text engine can measure the imported family, not just enumerate it.
    $nativeLibraryPath = $env:PATH
    try {
        $env:PATH = (Join-Path $GameDirectory 'Lib') + ';' + $nativeLibraryPath
        $customFont = [Vintagestory.API.Client.CairoFont]::new(24, 'Almendra', [double[]] @(1, 1, 1, 1))
        $standardFont = [Vintagestory.API.Client.CairoFont]::new(24, 'Arial', [double[]] @(1, 1, 1, 1))
        $customWidth = $customFont.GetTextExtents('Custom sign lettering 123').Width
        $standardWidth = $standardFont.GetTextExtents('Custom sign lettering 123').Width
        if ($customWidth -le 0 -or [Math]::Abs($customWidth - $standardWidth) -lt 0.1) { throw 'Cairo did not resolve the imported font as a distinct face.' }
    }
    finally {
        $env:PATH = $nativeLibraryPath
    }

    $merge = $mod.GetType('SignFonts.SignFontsModSystem').GetMethod('MergeFontFamilies', $staticFlags)
    $sources = [System.Collections.Generic.IEnumerable[string][]] @(
        [string[]] @(' Arial ', 'arial', '', 'My Custom Family'), $after, [string[]] @('My Custom Family'))
    $merged = [string[]] $merge.Invoke($null, [object[]] @(,$sources))
    if (($merged | Where-Object { $_ -eq 'Arial' }).Count -ne 1 -or $merged -notcontains 'My Custom Family') { throw 'Configured and detected font merging failed.' }
    "PASS: detected $($before.Count) installed Windows families; private font-folder imports, duplicate handling, and custom menu entries work."
}
finally {
    $catalog.Dispose()
}
$remaining = $catalogType.GetField('registeredFiles', $instanceFlags).GetValue($catalog)
if ($remaining.Count -ne 0) { throw 'Private font registrations were not released.' }
$restored = [string[]] $enumerate.Invoke($null, @())
foreach ($name in @('Almendra', 'Montserrat', 'Lora')) {
    if ($before -notcontains $name -and $restored -contains $name) { throw ('Private font was not unregistered: ' + $name) }
}
'PASS: imported fonts were unregistered on disposal; no system fonts were installed.'
