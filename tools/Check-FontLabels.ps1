param([string] $GameDirectory = (Join-Path $( [Environment]::GetFolderPath('ApplicationData') ) 'waxlight\versions\1.22.7'))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
foreach ($dependency in @('Lib\cairo-sharp.dll','Lib\SkiaSharp.dll','Lib\0Harmony.dll','Lib\Newtonsoft.Json.dll','VintagestoryAPI.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $GameDirectory $dependency)) | Out-Null
}
$mod = [Reflection.Assembly]::LoadFrom((Join-Path $projectRoot 'bin\Release\net10.0\SignFonts.dll'))
$type = $mod.GetType('SignFonts.SignFontsModSystem')
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$format = $type.GetMethod('FormatFontLabel', $flags)
$decode = $type.GetMethod('DecodeFontLabel', $flags)
$makeFont = $type.GetMethod('CreateFontLabelFont', $flags)
$baseFont = [Vintagestory.API.Client.CairoFont]::WhiteSmallText()
foreach ($family in @('Georgia','Courier New','Segoe UI','A & B','An "Example" Font','Font <test>')) {
    $label = [string] $format.Invoke($null, @($family))
    $tokens = [Vintagestory.API.Common.VtmlParser]::Tokenize($null,$label)
    if ($tokens.Count -ne 1 -or $tokens[0].Name -ne 'signfontsfont') { throw 'Font label did not produce its display tag.' }
    $decoded = [string] $decode.Invoke($null, @($tokens[0]))
    $font = $makeFont.Invoke($null, @($baseFont,$decoded))
    if ($font.Fontname -cne $family -or $decoded -cne $family) {
        throw "Font name failed to render as its own face with literal wording: $family"
    }
}
if ($format.Invoke($null, @('__game_default__')) -ne 'Default (game font)') { throw 'Default entry must stay readable in the game UI font.' }
'PASS: actual VTML parser safely carries each literal font name to its own display font; Default uses normal UI text.'
