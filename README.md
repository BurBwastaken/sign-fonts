# Sign Fonts

A Vintage Story mod by **burb** that adds font and text-formatting controls to the vanilla sign editors, with individual choices for freestanding signs, wall signs, and signpost directions.

## Format individual words (1.3.0)

Type your text, then drag over words (or use Shift with the arrow keys) to select them. Use the font dropdown, size dropdown, **B**, **I**, or **U** to format the selection. Without a selection, formatting applies to the whole active text field. On signposts, first click the direction field you want to change.

**Base size** makes the selection follow the sign's normal size control. **Clear** removes the selected text's font, size, bold, italic, and underline overrides. **Align lines** sets left, centre, or right alignment for the lines touched by your selection. With no selection, it changes only the cursor's current line. Press Enter to separate lines that need independent alignment; automatic wrapping keeps the paragraph's alignment. Select all text to align the entire field. Alignment moves complete lines rather than individual words within a line.

The input box remains a plain-text editing surface; mixed formatting previews on the sign itself. **Save** commits formatting and **Cancel** restores the previous appearance. Plain wording is kept in vanilla data, with formatting stored separately. Text changes retain formatting on unchanged characters; newly inserted text inherits the style at the insertion point. Oversized lettering is clipped to the sign's physical text area.

Install version **1.3.2 on both server and clients** to share this formatting. The archive contains no font files or extra Harmony assembly. Version 1.3.0's formatting controls were confirmed working in-game by the user. The 1.3.1 alignment change compiles against 1.22.7 and still needs in-game checking.

## Server save confirmation (1.3.2)

After Save, look for **Sign Fonts: formatting saved on server.** in chat. A rejection or missing confirmation is now reported visibly. Font family and rich styles are sent together and checked against the text and base size already accepted by vanilla.

The server retains formatting in the sign attributes and a backup in the world save. The normal world-save cycle commits that backup to disk. It is restored only for a matching block and wording at the same dimension and position. Vanilla continues to own the sign's base font-size field. Formatting that an older version already discarded cannot be reconstructed unless it remains in the sign's saved attributes.

Install 1.3.2 on server and clients and fully restart. This update builds against 1.22.7; the restart fix still needs an in-game check on the affected server.

## Choose a font

On Windows, the dropdown automatically lists installed font families, including fonts installed by the current player. Each name is displayed in its own typeface. Open a sign editor and choose a font from **Sign font**. Existing and untouched signs retain their original game font. **Default (game font)** restores that sign's original font.

You can type your wording once, then switch fonts to compare it on the sign. Font changes preserve the current unsaved text, font size, and pigment colour. **Save** commits the font choice for that sign; **Cancel** restores its previous font. The last-used font never becomes a fallback for other signs.

## Multiplayer server installation

Install `SignFonts-1.3.2.zip` on the server, replacing the older archive, and fully restart the server. Each player must also install the same archive in the Mods folder for their active game instance and fully restart the game. In Waxlight, use the instance's Mods folder. Keep the archive zipped and remove older Sign Fonts versions from the active Mods folder.

This version is universal and required on clients when present on the server. The game's normal automatic download looks for a published release in the Vintage Story Mod Database; it does not transfer this private archive from the server. Until an exact `signfonts` version `1.3.2` release is published there, automatic installation can fail with **Release not found**. Manual installation of the matching archive is required for this private build. Version 1.1.0 was client-only and was not advertised this way.

With the mod installed on both sides, the server stores the chosen font name in the individual sign's block-entity data, checks interaction permissions before accepting a font change, and synchronises the choice to players. Original sign wording is preserved. Font files are not uploaded or distributed: players need the chosen font locally to render the same typeface.

The mod can also run locally on a server without it. In that case choices remain private to that player and are keyed by the save's unique identifier, dimension, and sign position. Old seed-based choices from versions before 1.2.0 are retained in the config for reference but are no longer applied, because they can collide between worlds. Re-select any old custom choice you want to keep.

## Add your own fonts

Each player can use either of these methods:

1. Install a font in Windows and fully relaunch the game. It will appear automatically.
2. Put `.ttf`, `.otf`, or `.ttc` files directly in the instance's `ModConfig/SignFonts/Fonts` folder, then fully relaunch the game. The mod loads them privately for the game without installing them in Windows.

For Waxlight, open your instance's data folder and use:

```text
ModConfig/SignFonts/Fonts
```

Font names are sorted alphabetically and duplicates are removed. Invalid font files are skipped with a warning in the client log. Use the installed family name shown in the dropdown, which may differ from the file name.

This version synchronises font choices through servers running the mod, but does not upload or download font files between players.

## Optional configuration

`ModConfig/signfonts.json` retains its manually configured `fontFamilies` entries alongside automatically discovered fonts. You can still add a family name manually if needed, including on non-Windows systems:

```json
{
  "fontFamily": "Georgia",
  "fontFamilies": [ "Georgia", "Arial", "Times New Roman", "Courier New", "Verdana", "My Custom Font" ]
}
```

The selected family must be available to Cairo on the computer running the game. Automatic discovery and private font-file loading currently support Windows. Fully relaunch after editing the configuration or adding fonts.

## Build

Set `VINTAGE_STORY_DIR` to the folder containing `VintagestoryAPI.dll`, `Vintagestory.dll`, and `VintagestoryLib.dll`, set `HARMONY_DLL` to the game's `Lib/0Harmony.dll`, then run `dotnet build -c Release`. The mod uses the Harmony assembly provided by the game and does not bundle a duplicate.

Requires the .NET 10 SDK and a local installation of Vintage Story 1.22.7. The repository does not include the game's assemblies or fonts.

Example in PowerShell:

```powershell
$env:VINTAGE_STORY_DIR = 'C:\path\to\VintageStory'
$env:HARMONY_DLL = Join-Path $env:VINTAGE_STORY_DIR 'Lib\0Harmony.dll'
dotnet build -c Release
New-Item -ItemType Directory -Path dist -Force | Out-Null
Compress-Archive -LiteralPath @('modinfo.json', 'bin\Release\net10.0\SignFonts.dll') -DestinationPath 'dist\SignFonts-1.3.2.zip' -Force
```

Release archives contain only `modinfo.json` and `SignFonts.dll` at the root.
