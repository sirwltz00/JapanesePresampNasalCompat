# Japanese Presamp Nasal Compat (OpenUtau)

This repository builds a separately distributed OpenUtau phonemizer DLL using GitHub Actions, so a Windows PC is not required locally.

## What it aims to do

- Choose `ン` before velar consonants (K/G series).
- Choose `ン2` before bilabial consonants (P/B/M series).
- Treat `ん`, `ン`, and `ン2` as vowel-bearing nasal phonemes for VCV/CVVC transition logic.
- Fall back to aliases available in the voicebank.

Compatibility with all voicebanks is not yet verified. This is a development build until the GitHub Actions compilation succeeds and the DLL is tested in OpenUtau with CV, VCV, and CVVC banks.

## Build from an Android phone

1. Sign in at https://github.com/.
2. Create a **public or private repository** and upload all files from this folder, preserving `.github/workflows/build.yml` and the project/source files.
3. Open the repository's **Actions** tab and enable Actions if GitHub asks.
4. Run **Build Japanese Presamp Nasal Compat** from **Actions → Build Japanese Presamp Nasal Compat → Run workflow**. A push to `main` also starts a build.
5. If it succeeds, open that workflow run, scroll to **Artifacts**, and download `JapanesePresampNasalCompat`.
6. Extract the artifact ZIP and copy `JapanesePresampNasalCompat.dll` into the `Plugins` folder of your OpenUtau installation.
7. Restart OpenUtau and select **Japanese Presamp Nasal Compat** in the track phonemizer menu.

## Important technical note

The source started from OpenUtau's built-in Japanese presamp phonemizer and uses the `Presamp` / `PresampWatcher` implementation in `OpenUtau.Plugin.Builtin`. The standalone project references the upstream Core and Builtin projects. If upstream visibility/API changes prevent a build, inspect the failed workflow log and adjust the project/source to the specific OpenUtau revision. Successful compilation is not the same as verified phonemization behavior.

## License

The source is derived from OpenUtau's codebase. Check and preserve the applicable upstream license and attribution before redistributing builds. Do not claim this is an official OpenUtau release.
