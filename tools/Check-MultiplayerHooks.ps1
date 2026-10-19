param([string] $GameDirectory = 'C:\Users\ewant\AppData\Roaming\waxlight\versions\1.22.7')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
foreach ($dependency in @('Lib\cairo-sharp.dll','Lib\SkiaSharp.dll','Lib\0Harmony.dll','Lib\Newtonsoft.Json.dll','VintagestoryAPI.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $GameDirectory $dependency)) | Out-Null
}
$survival = [Reflection.Assembly]::LoadFrom((Join-Path $GameDirectory 'Mods\VSSurvivalMod.dll'))
$mod = [Reflection.Assembly]::LoadFrom((Join-Path $projectRoot 'bin\Release\net10.0\SignFonts.dll'))
$systemType = $mod.GetType('SignFonts.SignFontsModSystem')
$instance = [Activator]::CreateInstance($systemType)
$staticFlags = [Reflection.BindingFlags]'Static,NonPublic'
$instanceFlags = [Reflection.BindingFlags]'Instance,NonPublic'
$harmony = [HarmonyLib.Harmony]::new('signfonts.verification')
try {
    $systemType.GetMethod('InstallServerPatches',$staticFlags).Invoke($null,@($harmony)) | Out-Null
    $systemType.GetMethod('InstallFontMenuPatches',$staticFlags).Invoke($null,@($harmony)) | Out-Null
    foreach ($name in @('Vintagestory.GameContent.BlockEntitySign','Vintagestory.GameContent.BlockEntitySignPost')) {
        $systemType.GetMethod('PatchSignType',$instanceFlags).Invoke($instance,@($harmony,$name)) | Out-Null
        $target = $survival.GetType($name)
        foreach ($method in @('ToTreeAttributes','FromTreeAttributes','OnReceivedClientPacket','Initialize')) {
            $info = [HarmonyLib.Harmony]::GetPatchInfo([HarmonyLib.AccessTools]::Method($target,$method))
            if ($null -eq $info -or $info.Owners -notcontains $harmony.Id) { throw "Missing hook: $name.$method" }
        }
    }
    foreach ($name in @('GuiDialogBlockEntityTextInput','GuiDialogSignPost')) {
        $systemType.GetMethod('PatchSignDialog',$instanceFlags).Invoke($instance,@($harmony,$name)) | Out-Null
    }
    $json = Get-Content (Join-Path $projectRoot 'modinfo.json') -Raw
    $metadata = [Newtonsoft.Json.JsonConvert]::DeserializeObject($json,[Vintagestory.API.Common.ModInfo])
    if ($metadata.Side.ToString() -ne 'Universal' -or -not $metadata.RequiredOnClient) { throw 'Server must advertise the universal mod as required on clients.' }
    if ($metadata.RequiredOnServer) { throw 'Local-only use must remain available on unmodified servers.' }
    'PASS: server persistence/packet hooks, client read/initialise hooks, editor Save hooks, and font-menu hooks install against the real game; metadata requires the mod on players when installed on the server.'
}
finally {
    $harmony.UnpatchAll($harmony.Id)
}
