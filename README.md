# Kanata Warutsu Phonemizer (OpenUtau)

**JA VCV & CVVC+** is a custom Japanese phonemizer for OpenUtau, created
for WaltzさんP's UTAU voicebank, **Kanata Warutsu (奏多ワルツ)**.

This repository builds a separately distributed OpenUtau phonemizer DLL
with GitHub Actions, so a Windows PC is not required locally.

## Features

### Japanese CV / VCV / CVVC phonemization

-   Uses the existing Japanese presamp-based phonemizer logic to
    generate CV, VCV, and CVVC aliases and transitions.
-   Uses the singer's available OTO aliases and existing fallback
    behavior when resolving phonemes.
-   Supports phonetic hints and `presamp.ini` replacement rules.
-   Includes glottal-stop (`・`) alias handling and related fallback
    candidates.

### Automatic round-robin (RR) aliases

The phonemizer can automatically select among numbered RR recordings
discovered in the loaded voicebank's OTO inventory.

For example, a logical alias such as `a ka` can resolve to any available
alias from:

``` text
a ka RR0
a ka RR1
a ka RR2
```

RR variants do **not** require an ordinary unsuffixed alias such as
`a ka` to exist. The phonemizer discovers variants per complete base
alias, so gaps in numbering are fine: if only `ka RR0`, `ka RR2`, and
`ka RR5` exist, only those aliases are eligible.

RR naming rules:

-   Put a space before `RR`.
-   Put the suffix at the very end of the complete alias:
    `[complete alias] RR[number]`.
-   Use a numbered suffix such as `ka RR0`, `a ka RR1`, or `a ka息 RR2`.
-   Expression text and other alias content should come before the RR
    suffix.
-   Do not put the RR suffix in the middle of an alias or omit the
    separating space.

When multiple variants exist, selection is randomized and avoids
repeating the previous RR index for the same logical base alias whenever
possible. Selection history is tracked independently for each base
alias, including its VCV/CVVC context. The same note position reuses its
cached selection during recalculation to help keep editor refreshes
stable. If only one variant exists, that variant is used.

Explicitly entered numbered RR aliases, such as `ka RR1`, are treated as
requests for that exact alias when it exists; the phonemizer should not
append another RR suffix or randomly replace the requested index.

RR detection is based on the actual loaded OTO aliases, not a hardcoded
maximum RR number. The RR index is cached and rebuilt when the singer's
OTO inventory is detected to have changed.

### Custom nasal handling

-   Prefer `ン` before velar consonants, including K/G-series
    consonants, where the voicebank's alias setup supports it.
-   Prefer `ン2` before bilabial consonants, including P/B/M-series
    consonants, where supported.
-   Treat `ん`, `ン`, and `ン2` as vowel-bearing nasal phonemes for
    VCV/CVVC transition logic.
-   Preserve the phonemizer's custom nasal selection rather than
    replacing it with generic `n + consonant` aliases.

The actual alias still needs to exist in the voicebank for it to be
selected.

### Romaji, kana, and multi-mora lyrics

-   Supports multi-mora lyric splitting for romaji and kana input.
-   Uses the existing `+` continuation-note mechanism to distribute
    morae across the lyric note and its continuation notes as evenly as
    possible.
-   Divides note durations among the morae assigned to each note and
    then processes them through the existing phonemization logic,
    preserving transitions between the generated morae.
-   Converts `wo`, `WO`, and `Wo` to Japanese `を` rather than `うぉ`
    when the lyric is exactly `wo` (case-insensitive).

## RR alias examples

For a voicebank that uses RR-only aliases, the OTO can contain:

``` text
ka RR0
ka RR1
ka RR2
a ka RR0
a ka RR1
a ka息 RR0
a ka息 RR1
```

No plain `ka`, `a ka`, or `a ka息` alias is required for those logical
aliases to use their RR recordings.

A mixed voicebank is also supported in principle: an alias with
discovered RR variants can use RR selection while another alias can
continue through ordinary alias lookup and fallback.

## Build and compatibility notes

This repository builds the phonemizer DLL through GitHub Actions. A
successful build confirms compilation against the configured OpenUtau
source revision, but does not by itself verify every phonemization
behavior in OpenUtau.

Compatibility with all voicebanks is not yet verified. Test the
resulting DLL in OpenUtau with the intended CV, VCV, and CVVC voicebank
configurations, especially for custom OTO naming, RR-only aliases,
phonetic hints, and expression-bearing aliases.

The source started from OpenUtau's built-in Japanese presamp phonemizer
and uses the `Presamp` / `PresampWatcher` implementation in
`OpenUtau.Plugin.Builtin`. The standalone project references the
upstream Core and Builtin projects. If upstream visibility or API
changes prevent a build, inspect the failed workflow log and adjust the
project/source to the specific OpenUtau revision.

## License and attribution

The source is derived from OpenUtau's codebase. Check and preserve the
applicable upstream license and attribution before redistributing
builds. This is a community phonemizer and is not an official OpenUtau
release.
