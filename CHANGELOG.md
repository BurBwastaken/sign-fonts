# Changelog

## 1.3.2

- Submit font family and rich formatting together, with server validation.
- Confirm accepted formatting saves in chat and warn about rejected or unconfirmed saves.
- Keep a per-sign formatting backup in the world's save data.
- Preserve formatting in server chunk snapshots before block entity API initialisation.
- Log formatting read failures and rejected saves.

The reported restart persistence issue still needs verification on the affected server.

## 1.3.1

- Align selected lines or the cursor's current line.
- Keep separate alignment for paragraphs separated by Enter.
- Update the alignment dropdown as the cursor moves.

## 1.3.0

- Format selected words with different fonts and sizes.
- Add bold, italic, underline, clear formatting, and live sign previews.
- Save and synchronise rich formatting per sign and signpost direction.

## 1.2.0

- Store and synchronise individual font choices on servers.
- Display font names in their own typefaces.
- Preserve unsaved wording during font changes.
- Keep untouched signs on their original font and restore previews on Cancel.

## 1.1.0

- Discover installed Windows fonts and load additional local font files.
