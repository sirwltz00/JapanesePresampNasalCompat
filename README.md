# Japanese Presamp Extras (OpenUtau)

This repository builds a separately distributed OpenUtau phonemizer DLL using GitHub Actions, so a Windows PC is not required locally.

## What it aims to do

- Choose `ン` before velar consonants (K/G series).
- Choose `ン2` before bilabial consonants (P/B/M series).
- Treat `ん`, `ン`, and `ン2` as vowel-bearing nasal phonemes for VCV/CVVC transition logic.
- Fall back to aliases available in the voicebank.
- Added "wo" lyric support for the Phonemizer
  
Compatibility with all voicebanks is not yet verified. This is a development build until the GitHub Actions compilation succeeds and the DLL is tested in OpenUtau with CV, VCV, and CVVC banks.

## Important technical note

The source started from OpenUtau's built-in Japanese presamp phonemizer and uses the `Presamp` / `PresampWatcher` implementation in `OpenUtau.Plugin.Builtin`. The standalone project references the upstream Core and Builtin projects. If upstream visibility/API changes prevent a build, inspect the failed workflow log and adjust the project/source to the specific OpenUtau revision. Successful compilation is not the same as verified phonemization behavior.

## License

The source is derived from OpenUtau's codebase. Check and preserve the applicable upstream license and attribution before redistributing builds. Do not claim this is an official OpenUtau release.
