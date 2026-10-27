using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Classic;
using OpenUtau.Api;
using OpenUtau.Core.Ustx;
using OpenUtau.Core;
//using Serilog;

namespace OpenUtau.Plugin.KanataWarutsuPhonemizer {
    [Phonemizer("Kanata Warutsu Phonemizer", "JA VCV & CVVC+", "WaltzさんP", language: "JA")]
    public class KanataWarutsuPhonemizer : Phonemizer {

        // CV, VCV, CVVCを含むすべての日本語VBをサポートする予定です。Will support all Japanese VBs including CV, VCV, CVVC
        // 基本的な仕様はpresampに準拠します。Basic behavior conforms to presamp
        // 歌詞に書かれた"強"などの表情は非対応。VoiceColorでやってもらいます。Append suffixes such as "強" written in the lyrics are not supported
        // 喉切り"・"はpresamp.iniに書いてなくても動くようなんとかします。I'll try to make it work even if "・" are not written in presamp.ini
        // Supporting: [VOWEL][CONSONANT][PRIORITY][REPLACE][ALIAS(VCPAD,VCVPAD)]
        // Partial supporting: [NUM][APPEND][PITCH] -> Using to exclude useless characters in lyrics

        private USinger singer;
        private Presamp presamp;
        private static int globalPresampGeneration = 0;
        private int localPresampGeneration = 0;
        private static PresampWatcher presampWatcher;
        private static string currentlyWatchedPresampDir;

        // RR aliases use a trailing " RR<number>" suffix, e.g. "ka RR0" or "a ka RR2".
        // The index is built from singer.Otos; no maximum RR number is assumed.
        private sealed class RrVariant {
            public long Index;
            public string Suffix;
        }

        private sealed class RrSelection {
            public long Index;
        }

        private static readonly Regex rrSuffixRegex = new Regex(
            @"^(?<base>.+)(?<suffix> RR(?<index>[0-9]+))$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private const int MaxRrSelectionsPerAlias = 512;
        // Look at a short run of earlier selections for this alias. Recent-use weighting
        // reduces obvious clusters while preserving a genuine random choice each time.
        private const int RecentRrWindow = 6;
        private readonly object rrLock = new object();
        private readonly Random rrRandom = CreateRrRandom();
        private Dictionary<string, List<RrVariant>> rrVariantsByBase = new Dictionary<string, List<RrVariant>>(StringComparer.Ordinal);
        private readonly Dictionary<string, SortedDictionary<int, RrSelection>> rrSelectionsByBase =
            new Dictionary<string, SortedDictionary<int, RrSelection>>(StringComparer.Ordinal);
        private USinger rrIndexedSinger;
        private System.Collections.Generic.IList<UOto> rrIndexedOtoList;
        private int rrIndexedOtoCount = -1;
        private bool rrIndexedOtoDirty;


        // in case voicebank is missing certain symbols
        static readonly string[] substitution = new string[] {
            "ty,ch,ts=t", "j,dy=d", "gy=g", "ky=k", "py=p", "ny=n", "ry=r", "my=m", "hy,f=h", "by,v=b", "dz=z", "l=r", "ly=l"
        };

        static readonly Dictionary<string, string> substituteLookup;

        static KanataWarutsuPhonemizer() {
            substituteLookup = substitution.ToList()
                .SelectMany(line => {
                    var parts = line.Split('=');
                    return parts[0].Split(',').Select(orig => (orig, parts[1]));
                })
                .ToDictionary(t => t.Item1, t => t.Item2);
        }

        public override void SetSinger(USinger singer) {
            bool sameSingerInstance = ReferenceEquals(this.singer, singer);
            bool generationChanged = this.localPresampGeneration != globalPresampGeneration;
            if (sameSingerInstance && !generationChanged) {
                // EnsureRrIndex also watches the singer's OTO-list reference, count, and
                // dirty state, so a reused singer can refresh its index after a reload.
                return;
            }

            this.singer = singer;
            ClearRoundRobinState(clearSelections: true);
            if (this.singer == null) {
                return;
            }
            this.localPresampGeneration = globalPresampGeneration;
            if (this.presamp == null || generationChanged || !sameSingerInstance) {
                this.presamp = new Presamp();
                this.presamp.ReadPresampIni(singer.Location, singer.TextFileEncoding);
            }
            SetupPresampWatcher(singer.Location);
        }

        private void SetupPresampWatcher(string directory) {
            if (string.IsNullOrEmpty(directory) || currentlyWatchedPresampDir == directory) {
                return;
            }
            if (presampWatcher != null) {
                presampWatcher.Dispose();
                presampWatcher = null;
            }
            currentlyWatchedPresampDir = directory;
            if (Directory.Exists(directory)) {
                presampWatcher = new PresampWatcher(directory, () => {
                    System.Threading.Thread.Sleep(200);
                    globalPresampGeneration++;
                    if (this.singer != null) {
                        OpenUtau.Core.SingerManager.Inst.ScheduleReload(this.singer);
                    }
                });
            }
        }

        public override Result Process(Note[] notes, Note? prev, Note? next, Note? prevNeighbour, Note? nextNeighbour, Note[] prevNeighbours) {
            // OpenUtau passes the lyric note together with its '+' extender notes.
            // If the lyric contains multiple Japanese morae, split it across that group
            // and run the existing VCV/CVVC logic for each generated mora.
            if (notes != null && notes.Length > 0
                && string.IsNullOrEmpty(notes[0].phoneticHint)
                && TrySplitMultiMoraLyric(notes[0].lyric, out var morae)
                && morae.Count > 1) {
                return ProcessMultiMora(notes, morae, prev, next, prevNeighbour, nextNeighbour, prevNeighbours);
            }
            return ProcessSingleMora(notes, prev, next, prevNeighbour, nextNeighbour, prevNeighbours);
        }

        private Result ProcessMultiMora(Note[] notes, List<string> morae, Note? prev, Note? next,
            Note? prevNeighbour, Note? nextNeighbour, Note[] prevNeighbours) {
            int groupStart = notes[0].position;
            int baseCount = morae.Count / notes.Length;
            int extraCount = morae.Count % notes.Length;
            int moraIndex = 0;
            var virtualNotes = new List<Note>();

            // Distribute morae as evenly as possible across the lyric note and its extenders.
            // Each real note's duration is then divided evenly among the morae assigned to it.
            for (int noteIndex = 0; noteIndex < notes.Length; noteIndex++) {
                var source = notes[noteIndex];
                int count = baseCount + (noteIndex < extraCount ? 1 : 0);
                if (count == 0) {
                    continue;
                }
                for (int j = 0; j < count; j++) {
                    int start = source.position + (int)((long)source.duration * j / count);
                    int end = source.position + (int)((long)source.duration * (j + 1) / count);
                    virtualNotes.Add(new Note {
                        lyric = morae[moraIndex++],
                        phoneticHint = null,
                        tone = source.tone,
                        position = start,
                        duration = Math.Max(1, end - start),
                        phonemeAttributes = source.phonemeAttributes,
                    });
                }
            }

            var combined = new List<Phoneme>();
            for (int i = 0; i < virtualNotes.Count; i++) {
                var current = virtualNotes[i];
                Note? previousVirtual = i > 0 ? virtualNotes[i - 1] : (Note?)null;
                Note? followingVirtual = i + 1 < virtualNotes.Count ? virtualNotes[i + 1] : (Note?)null;
                Note? localPrev = previousVirtual ?? prev;
                Note? localNext = followingVirtual ?? next;
                Note? localPrevNeighbour = previousVirtual ?? prevNeighbour;
                Note? localNextNeighbour = followingVirtual ?? nextNeighbour;
                Note[] localPrevNeighbours = previousVirtual.HasValue
                    ? new Note[] { previousVirtual.Value }
                    : prevNeighbours;

                var single = ProcessSingleMora(new Note[] { current }, localPrev, localNext,
                    localPrevNeighbour, localNextNeighbour, localPrevNeighbours);
                int offset = current.position - groupStart;
                for (int phonemeIndex = 0; phonemeIndex < single.phonemes.Length; phonemeIndex++) {
                    var phoneme = single.phonemes[phonemeIndex];
                    phoneme.position += offset;
                    combined.Add(phoneme);
                }
            }

            // A multi-mora group can contain more phonemes than the original 0/1/2 slots.
            // Sort by time and assign unique indexes for the combined result.
            var ordered = combined
                .Select((phoneme, order) => new { phoneme, order })
                .OrderBy(item => item.phoneme.position)
                .ThenBy(item => item.order)
                .Select(item => item.phoneme)
                .ToArray();
            for (int i = 0; i < ordered.Length; i++) {
                ordered[i].index = i;
            }
            return new Result { phonemes = ordered };
        }

        private Result ProcessSingleMora(Note[] notes, Note? prev, Note? next, Note? prevNeighbour, Note? nextNeighbour, Note[] prevNeighbours) {
            var result = new List<Phoneme>();
            bool preCFlag = false;

            var note = notes[0];
            var currentLyric = ParseLyricFromNote(note);
            // ん has two optional allophones in some voicebanks: ン (velar/ng) and ン2 (bilabial/m).
            // Keep explicit phonetic hints untouched; otherwise choose the nasal from the following consonant.
            if (string.IsNullOrEmpty(note.phoneticHint) && IsNasalMora(currentLyric)) {
                var nasalVariant = GetNasalVariantForNext(nextNeighbour);
                if (!string.IsNullOrEmpty(nasalVariant)) {
                    currentLyric = nasalVariant;
                }
            }
            var currentAlias = ParseAliasFromLyric(currentLyric); // exclude useless characters
            var vcvpad = presamp.AliasRules.VCVPAD;
            var vcpad = presamp.AliasRules.VCPAD;
            var initial = $"-{vcvpad}{currentLyric}";
            var cfLyric = $"*{vcpad}{currentLyric}";

            var vowelUpper = Regex.Match(currentLyric, "[あいうえおんン]").Value ?? currentLyric;
            var glottalCVtests = new List<string> { $"・{vcpad}{vowelUpper}", $"・{vowelUpper}", $"{vowelUpper}・", $"-{vcvpad}{vowelUpper}・", $"-{vcvpad}{vowelUpper}", initial, currentLyric, vowelUpper };

            /*
            // Debug
            var prevtest = prevNeighbour != null ? prevNeighbour.Value.lyric.Normalize() : "";
            var nexttest = nextNeighbour != null ? nextNeighbour.Value.lyric.Normalize() : "";
            Log.Debug($"Prev: {prevtest}, Current: {currentLyric}, Next: {nexttest}");
            */

            // Convert 1st phoneme
            if (!string.IsNullOrEmpty(note.phoneticHint)) { // not convert
                var tests = new List<string> { currentLyric };
                if (checkOtoUntilHit(tests, note, out var oto)) {
                    currentLyric = oto.Alias;
                }
            } else if (prevNeighbour == null) { // beginning of phrase
                preCFlag = true;
                if (currentLyric.Contains("・")) {
                    var tests = new List<string> { $"-{vcvpad}{vowelUpper}・", $"・{vcpad}{vowelUpper}", $"{vowelUpper}・", $"・{vowelUpper}", $"-{vcvpad}{vowelUpper}", initial, currentLyric };
                    if (checkOtoUntilHit(tests, note, out var oto1)) {
                        currentLyric = oto1.Alias;
                    }
                } else {
                    var tests = new List<string> { initial, currentLyric };
                    if (checkOtoUntilHit(tests, note, out var oto)) {
                        currentLyric = oto.Alias;
                    }
                }
            } else { // middle of phrase
                var prevLyric = ParseLyricFromNote(prevNeighbour.Value);
                string prevAlias = ParseAliasFromLyric(prevLyric); // exclude useless characters

                string vcGlottalStop = "[aiueonN]" + vcpad + "・$"; // [a ・]
                if (prevLyric == "・" || Regex.IsMatch(prevLyric, vcGlottalStop)) {
                    // 喉切り(Glottal stop) prev is VC -> current is Glottal stop CV[・ あ][・あ][あ・][- あ][あ]
                    if (checkOtoUntilHit(glottalCVtests, note, out var oto)) {
                        currentLyric = oto.Alias;
                    }
                } else if (prevLyric.Contains("っ")) {
                    // try CV
                    var tests = new List<string> { currentLyric, initial };
                    if (checkOtoUntilHit(tests, note, out var oto)) {
                        currentLyric = oto.Alias;
                    }
                } else if (TryGetPresampPhoneme(prevAlias, out PresampPhoneme prevPhoneme)) {
                    if (currentLyric.Contains("・")) {
                        // Glottal stop
                        var tests = new List<string>();
                        UOto oto;

                        if (Regex.IsMatch(currentLyric, vcGlottalStop)) { // current is VC = 2nd phoneme is not needed
                            tests = new List<string> { currentLyric };
                            if (checkOtoUntilHit(tests, note, out oto)) {
                                return MakeSimpleResult(oto.Alias);
                            }
                        } else if (currentLyric == "・" && HasVowelAlias(prevAlias, prevPhoneme)) { // current is VC = 2nd phoneme is not needed
                            var vc = $"{prevPhoneme.Vowel}{vcpad}{currentLyric}";
                            tests = new List<string> { vc, currentLyric };
                            if (checkOtoUntilHit(tests, note, out oto)) {
                                return MakeSimpleResult(oto.Alias);
                            }
                        } else if (HasVowelAlias(prevAlias, prevPhoneme)) { // current is VCV
                            tests.Add($"{prevPhoneme.Vowel}{vcvpad}{currentLyric}");
                            tests.Add($"{prevPhoneme.Vowel}{vcvpad}{vowelUpper}・");
                            tests.Add($"{prevPhoneme.Vowel}{vcvpad}・{vowelUpper}");
                        }
                        tests.AddRange(glottalCVtests); // current is CV
                        if (checkOtoUntilHit(tests, note, out oto)) { // check VCV and CV
                            currentLyric = oto.Alias;
                        }
                    } else if (TryGetPresampPhoneme(currentLyric, out PresampPhoneme currentPhoneme) && currentPhoneme.IsPriority) {
                        // Priority: not VCV, VC (almost C)
                        var tests = new List<string> { currentLyric, initial };
                        if (checkOtoUntilHit(tests, note, out var oto)) {
                            currentLyric = oto.Alias;
                        }
                    } else if (HasVowelAlias(prevAlias, prevPhoneme)) {
                        string prevVow = GetVowelAlias(prevAlias, prevPhoneme);

                        if (currentLyric == "っ" && nextNeighbour != null) { // っ = current is VC
                            var nextLyric = ParseLyricFromNote(nextNeighbour.Value);
                            var nextAlias = ParseAliasFromLyric(nextLyric);

                            var axtu1 = $"{prevVow}{vcvpad}{currentLyric}"; // a っ
                            var axtu2 = $"{prevVow}{vcpad}{currentLyric}"; // a っ
                            var tests2 = new List<string> { axtu1, axtu2, currentLyric };
                            if (TryGetPresampPhoneme(nextAlias, out PresampPhoneme nextPhoneme) && nextPhoneme.HasConsonant) {
                                tests2.Insert(2, $"{prevVow}{vcpad}{nextPhoneme.Consonant}"); // VC
                            }
                            if (checkOtoUntilHit(tests2, note, out var oto2)) {
                                return MakeSimpleResult(oto2.Alias);
                            }
                        } else { // try VCV, VC
                            var vcv = $"{prevVow}{vcvpad}{currentLyric}";
                            var vc = $"{prevVow}{vcpad}{currentLyric}";
                            var tests = new List<string> { vcv, vc, cfLyric, currentLyric };
                            if (checkOtoUntilHit(tests, note, out var oto)) {
                                currentLyric = oto.Alias;
                            }
                        }
                    } else {
                        // try CV
                        var tests = new List<string> { currentLyric, initial };
                        if (checkOtoUntilHit(tests, note, out var oto)) {
                            currentLyric = oto.Alias;
                        }
                    }
                } else {
                    // try "- CV" (prev is R, breath, etc.)
                    preCFlag = true;
                    var tests = new List<string> { initial, currentLyric };
                    if (checkOtoUntilHit(tests, note, out var oto)) {
                        currentLyric = oto.Alias;
                    }
                }
            }
            result.Add(new Phoneme() { phoneme = currentLyric, index = 0 });

            // Insert "- C"
            if (string.IsNullOrEmpty(note.phoneticHint)
                && preCFlag
                && !currentLyric.Contains(vcvpad)
                && TryGetPresampPhoneme(currentAlias, out PresampPhoneme phoneme)
                && phoneme.HasConsonant
                && (presamp.Priorities == null || !presamp.Priorities.Contains(phoneme.Consonant))) {
                if (checkOtoUntilHit(new List<string> { $"-{vcvpad}{phoneme.Consonant}" }, note, 2, out var cOto, out var color)
                    && checkOtoUntilHit(new List<string> { currentLyric }, note, out var oto)) {
                    int endTick = notes[^1].position + notes[^1].duration;
                    var attr = note.phonemeAttributes?.FirstOrDefault(attr => attr.index == 0) ?? default;
                    var cLength = Math.Max(30, -timeAxis.MsToTickAt(-oto.Preutter, endTick) * (attr.consonantStretchRatio ?? GetParentConsonantStretchRatio()));

                    if (prevNeighbour != null) {
                        cLength = Math.Min(prevNeighbour.Value.duration / 2, cLength);
                    } else if (prev != null) {
                        cLength = Math.Min(note.position - prev.Value.position - prev.Value.duration, cLength);
                    }

                    result.Insert(0, new Phoneme() {
                        phoneme = cOto.Alias,
                        position = Convert.ToInt32(- cLength),
                        index = 2,
                        expressions = new List<PhonemeExpression>()
                    });
                    if (color != null) {
                        result[0].expressions.Add(new PhonemeExpression() { abbr = Core.Format.Ustx.CLR, value = (int)color });
                    }
                    if (presamp.CFlags == "p0") {
                        result.First(p => p.index == 2).expressions.Add(new PhonemeExpression() { abbr = Core.Format.Ustx.NORM, value = 0 });
                    }
                }
            }

            // Insert 2nd phoneme (when next doesn't have hint)
            if (nextNeighbour != null && string.IsNullOrEmpty(nextNeighbour.Value.phoneticHint)) {
                int totalDuration = notes.Sum(n => n.duration);
                if (timeAxis.TickPosToMsPos(totalDuration) < 100 && presamp.MustVC == false) {
                    return new Result { phonemes = result.ToArray() };
                }

                var nextLyric = ParseLyricFromNote(nextNeighbour.Value);
                var nextAlias = ParseAliasFromLyric(nextLyric); // exclude useless characters
                string vcPhoneme;
                int? vcColorIndex;

                // Without current vowel, VC cannot be created
                if (!TryGetPresampPhoneme(currentAlias, out PresampPhoneme currentPhoneme) || !HasVowelAlias(currentAlias, currentPhoneme)) {
                    return new Result { phonemes = result.ToArray() };
                }
                var vowel = GetVowelAlias(currentAlias, currentPhoneme);

                if (Regex.IsMatch(nextLyric, "[aiueonN]" + vcvpad) || Regex.IsMatch(nextLyric, "[aiueonN]" + vcpad)) {
                    // next is VCV or VC (VC is not needed)
                    return new Result { phonemes = result.ToArray() };
                } else {
                    if (nextLyric.Contains("・")) { // Glottal stop
                        if (nextLyric == "・") { // next is VC (VC is not needed)
                            return new Result { phonemes = result.ToArray() };
                        } else {
                            vowelUpper = Regex.Match(nextLyric, "[あいうえおんン]").Value;
                            if (vowelUpper == null) {
                                return new Result { phonemes = result.ToArray() };
                            }

                            // next is VCV (VC is not needed)
                            var tests = new List<string>();
                            tests.Add($"{vowel}{vcvpad}{nextLyric}");
                            tests.Add($"{vowel}{vcvpad}{vowelUpper}・");
                            tests.Add($"{vowel}{vcvpad}・{vowelUpper}");
                            glottalCVtests = new List<string> { $"・{vcpad}{vowelUpper}", $"・{vowelUpper}", $"{vowelUpper}・", $"-{vcvpad}{vowelUpper}・", $"-{vcvpad}{vowelUpper}", initial, nextLyric, vowelUpper };
                            tests.AddRange(glottalCVtests);
                            if (checkOtoUntilHit(tests, nextNeighbour.Value, out var oto1) && oto1.Alias.Contains($"{vowel}{vcvpad}")) {
                                return new Result { phonemes = result.ToArray() };
                            }

                            // next is CV (VC is needed)
                            tests = new List<string> { $"{vowel}{vcpad}・" };
                            if (checkOtoUntilHit(tests, note, 1, out oto1, out var color)) {
                                vcPhoneme = oto1.Alias;
                                vcColorIndex = color;
                            } else {
                                return new Result { phonemes = result.ToArray() };
                            }
                        }
                    } else {

                        // Without next consonant, VC cannot be created
                        if (!TryGetPresampPhoneme(nextAlias, out PresampPhoneme nextPhoneme) || !nextPhoneme.HasConsonant) {
                            return new Result { phonemes = result.ToArray() };
                        }
                        var consonant = nextPhoneme.Consonant;

                        // If next is not priority and is convertable VC, VC is not needed
                        // If next is VCV, VC is not needed
                        if (!nextPhoneme.IsPriority) {
                            var nextVCV = $"{vowel}{vcvpad}{nextAlias}";
                            var nextVC = $"{vowel}{vcpad}{nextAlias}";
                            var tests = new List<string> { nextVCV, nextVC, nextAlias };
                            if (checkOtoUntilHit(tests, nextNeighbour.Value, out var oto1)
                                && (Regex.IsMatch(oto1.Alias, "[aiueonN]" + vcvpad) || Regex.IsMatch(oto1.Alias, "[aiueonN]" + vcpad))) {
                                return new Result { phonemes = result.ToArray() };
                            }
                        }

                        // Insert VC
                        vcPhoneme = $"{vowel}{vcpad}{consonant}";
                        var vcPhonemes = new List<string> { vcPhoneme };
                        // find potential substitute symbol
                        if (substituteLookup.TryGetValue(consonant ?? string.Empty, out var con)) {
                            vcPhonemes.Add($"{vowel}{vcpad}{con}");
                        }
                        if (checkOtoUntilHit(vcPhonemes, note, 1, out var oto, out var color)) {
                            vcPhoneme = oto.Alias;
                            vcColorIndex = color;
                        } else {
                            return new Result { phonemes = result.ToArray() };
                        }
                    }
                }
                if (!string.IsNullOrEmpty(vcPhoneme)) {
                    int vcLength = 120;
                    int endTick = notes[^1].position + notes[^1].duration;
                    var nextAttr = nextNeighbour.Value.phonemeAttributes?.FirstOrDefault(attr => attr.index == 0) ?? default;
                    if (TryGetMappedOtoWithRoundRobin(nextLyric,
                        nextNeighbour.Value.tone + (nextAttr.toneShift ?? GetParentToneShift()),
                        nextAttr.voiceColor ?? GetParentVoiceColor(), nextNeighbour.Value, out var nextOto)) {
                        // If overlap is a negative value, vcLength is longer than Preutter
                        if (nextOto.Overlap < 0) {
                            vcLength = -timeAxis.MsToTickAt(-(nextOto.Preutter - nextOto.Overlap), endTick);
                        } else {
                            vcLength = -timeAxis.MsToTickAt(-nextOto.Preutter, endTick);
                        }
                    }
                    // Minimam is 30 tick, maximum is half of note
                    vcLength = Convert.ToInt32(Math.Min(totalDuration / 2, Math.Max(30, vcLength * (nextAttr.consonantStretchRatio ?? GetParentConsonantStretchRatio()))));

                    result.Add(new Phoneme() {
                        phoneme = vcPhoneme,
                        position = totalDuration - vcLength,
                        index = 1,
                        expressions = new List<PhonemeExpression>()
                    });
                    if (vcColorIndex != null) {
                        result.First(p => p.index == 1).expressions.Add(new PhonemeExpression() { abbr = Core.Format.Ustx.CLR, value = (int)vcColorIndex });
                    }
                }
            }

            return new Result { phonemes = result.ToArray() };
        }

        private bool checkOtoUntilHit(List<string> input, Note note, out UOto oto) {
            oto = default;
            var attr = note.phonemeAttributes?.FirstOrDefault(attr => attr.index == 0) ?? default;
            string color = attr.voiceColor ?? GetParentVoiceColor();
            int shift = attr.toneShift ?? GetParentToneShift();
            int? alt = attr.alternate ?? GetParentAlternate();

            var otos = new List<UOto>();
            foreach (string test in ExpandNasalAliases(input)) {
                if (TryGetCandidateOto(test, alt, note, note.tone + shift, color, out var candidate)) {
                    otos.Add(candidate);
                }
            }

            if (otos.Count > 0) {
                oto = otos.FirstOrDefault(candidate => candidate.IsColorMatch(color));
                if (oto == null) {
                    oto = otos.First();
                }
                return true;
            }
            return false;
        }

        private bool checkOtoUntilHit(List<string> input, Note note, int index, out UOto oto, out int? colorIndex) {
            oto = default;
            colorIndex = null;
            var attr = note.phonemeAttributes?.FirstOrDefault(attr => attr.index == index) ?? default;
            var attr0 = note.phonemeAttributes?.FirstOrDefault(attr => attr.index == 0) ?? default;
            string color = attr.voiceColor ?? attr0.voiceColor ?? GetParentVoiceColor();
            int shift = attr.toneShift ?? attr0.toneShift ?? GetParentToneShift();
            int? alt = attr.alternate ?? GetParentAlternate();

            var otos = new List<UOto>();
            foreach (string test in ExpandNasalAliases(input)) {
                if (TryGetCandidateOto(test, alt, note, note.tone + shift, color, out var candidate)) {
                    otos.Add(candidate);
                }
            }

            if (otos.Count > 0) {
                oto = otos.FirstOrDefault(candidate => candidate.IsColorMatch(color));
                if (oto != null) {
                    if (track != null && track.VoiceColorExp.options.Contains(color)) {
                        colorIndex = Array.IndexOf(track.VoiceColorExp.options, color);
                    }
                    return true;
                } else if (index != 1 && index != 2) { // Main phoneme is required, preC and VC are not required
                    oto = otos.First();
                    return true;
                }
            }
            return false;
        }

        private bool TryGetCandidateOto(string test, int? alternate, Note note, int tone, string color, out UOto oto) {
            // An explicitly entered RR alias is authoritative: don't append alternate data
            // or randomly replace its requested RR index.
            if (IsExplicitRrAlias(test)) {
                return TryGetMappedOtoWithRoundRobin(test, tone, color, note, out oto);
            }
            if (TryGetMappedOtoWithRoundRobin(test + alternate, tone, color, note, out oto)) {
                return true;
            }
            return TryGetMappedOtoWithRoundRobin(test, tone, color, note, out oto);
        }

        private bool TryGetMappedOtoWithRoundRobin(string alias, int tone, string color, Note note, out UOto oto) {
            oto = default;
            if (singer == null || string.IsNullOrEmpty(alias)) {
                return false;
            }

            // A manual RR alias must be looked up literally, never randomized or suffixed again.
            if (IsExplicitRrAlias(alias)) {
                return singer.TryGetMappedOto(alias, tone, color, out oto);
            }

            EnsureRrIndex();
            List<RrVariant> variants;
            lock (rrLock) {
                variants = rrVariantsByBase.TryGetValue(alias, out var found)
                    ? found.ToList()
                    : null;
            }

            // Mixed banks remain supported: if this logical alias has no RR variants,
            // use OpenUTau's ordinary mapped alias lookup exactly as before.
            if (variants == null || variants.Count == 0) {
                return singer.TryGetMappedOto(alias, tone, color, out oto);
            }

            // Keep only variants that OpenUTau can actually map for this note/tone/color.
            // Missing numbers are never fabricated from the largest discovered RR index.
            var usable = new List<KeyValuePair<RrVariant, UOto>>();
            foreach (var variant in variants) {
                if (singer.TryGetMappedOto(alias + variant.Suffix, tone, color, out var candidate)
                    && candidate != null) {
                    usable.Add(new KeyValuePair<RrVariant, UOto>(variant, candidate));
                }
            }
            if (usable.Count == 0) {
                // This base is RR-enabled in the OTO inventory, so don't silently return
                // an unsuffixed alias that may not exist in an RR-only bank.
                return false;
            }

            var usableVariants = usable.Select(pair => pair.Key).ToList();
            long selectedIndex = SelectRrIndex(alias, note.position, usableVariants);
            var selected = usable.FirstOrDefault(pair => pair.Key.Index == selectedIndex);
            if (selected.Value == null) {
                return false;
            }
            oto = selected.Value;
            return true;
        }

        private void EnsureRrIndex() {
            if (singer == null) {
                return;
            }
            var otoList = singer.Otos;
            int currentCount = otoList?.Count ?? 0;
            bool currentDirty = singer.OtoDirty;
            lock (rrLock) {
                if (ReferenceEquals(rrIndexedSinger, singer)
                    && ReferenceEquals(rrIndexedOtoList, otoList)
                    && rrIndexedOtoCount == currentCount
                    && rrIndexedOtoDirty == currentDirty) {
                    return;
                }
            }

            UOto[] otoSnapshot;
            try {
                otoSnapshot = otoList == null ? new UOto[0] : otoList.ToArray();
            } catch (InvalidOperationException) {
                // A concurrent OTO reload can mutate the list while it's being copied.
                // Keep the prior index and try again on the next lookup.
                return;
            } catch (ArgumentException) {
                // Some collection implementations report concurrent-copy races this way.
                return;
            }

            var rebuilt = new Dictionary<string, List<RrVariant>>(StringComparer.Ordinal);
            foreach (var otoEntry in otoSnapshot) {
                if (otoEntry == null) {
                    continue;
                }
                // Phonetic is OpenUTau's prefix/suffix-stripped alias when an OTO belongs
                // to a mapped subbank. Prefer it, but also accept the literal alias if the
                // singer implementation doesn't populate Phonetic.
                Match match = null;
                if (!string.IsNullOrEmpty(otoEntry.Phonetic)) {
                    match = rrSuffixRegex.Match(otoEntry.Phonetic);
                }
                if ((match == null || !match.Success) && !string.IsNullOrEmpty(otoEntry.Alias)) {
                    match = rrSuffixRegex.Match(otoEntry.Alias);
                }
                if (match == null || !match.Success || !long.TryParse(match.Groups["index"].Value, out long rrIndex)) {
                    continue;
                }
                string baseAlias = match.Groups["base"].Value;
                if (!rebuilt.TryGetValue(baseAlias, out var baseVariants)) {
                    baseVariants = new List<RrVariant>();
                    rebuilt.Add(baseAlias, baseVariants);
                }
                // Index, not spelling or list position, defines an RR variation. Avoid
                // duplicate entries if the OTO inventory exposes the same alias more than once.
                if (!baseVariants.Any(variant => variant.Index == rrIndex)) {
                    baseVariants.Add(new RrVariant {
                        Index = rrIndex,
                        Suffix = match.Groups["suffix"].Value,
                    });
                }
            }
            foreach (var baseVariants in rebuilt.Values) {
                baseVariants.Sort((left, right) => left.Index.CompareTo(right.Index));
            }

            lock (rrLock) {
                rrVariantsByBase = rebuilt;
                rrIndexedSinger = singer;
                rrIndexedOtoList = otoList;
                rrIndexedOtoCount = otoSnapshot.Length;
                rrIndexedOtoDirty = currentDirty;
            }
        }

        private long SelectRrIndex(string baseAlias, int notePosition, List<RrVariant> usableVariants) {
            var availableIndices = usableVariants.Select(variant => variant.Index).Distinct().ToList();
            if (availableIndices.Count == 0) {
                return -1;
            }

            lock (rrLock) {
                if (!rrSelectionsByBase.TryGetValue(baseAlias, out var history)) {
                    history = new SortedDictionary<int, RrSelection>();
                    rrSelectionsByBase.Add(baseAlias, history);
                }

                if (history.TryGetValue(notePosition, out var cached)
                    && availableIndices.Contains(cached.Index)) {
                    // Keep refreshes and render retries stable for the same note.
                    return cached.Index;
                }

                long? previousIndex = null;
                long? nextIndex = null;
                foreach (var pair in history) {
                    if (pair.Key < notePosition) {
                        previousIndex = pair.Value.Index;
                    } else if (pair.Key > notePosition) {
                        nextIndex = pair.Value.Index;
                        break;
                    }
                }

                var choices = availableIndices.ToList();
                if (previousIndex.HasValue && choices.Count > 1) {
                    var withoutPrevious = choices.Where(index => index != previousIndex.Value).ToList();
                    if (withoutPrevious.Count > 0) {
                        choices = withoutPrevious;
                    }
                }
                // Notes can be processed out of order. Avoid matching the next cached note
                // as well whenever that is possible without forcing a previous-note repeat.
                if (nextIndex.HasValue && choices.Count > 1) {
                    var withoutNext = choices.Where(index => index != nextIndex.Value).ToList();
                    if (withoutNext.Count > 0) {
                        choices = withoutNext;
                    }
                }

                // Plain per-note random picks can form conspicuous short loops (for example,
                // A-B-A-B) even with immediate repeats blocked. Count the last few selections
                // for this alias and give less-recently-used RR indices a higher probability.
                // This is deliberately weighted, not a strict shuffle bag: every remaining
                // choice still has a chance, so the result does not become a fixed cycle.
                var recentCounts = availableIndices.ToDictionary(index => index, index => 0);
                int examined = 0;
                foreach (var pair in history.Reverse()) {
                    if (pair.Key >= notePosition) {
                        continue;
                    }
                    if (examined >= RecentRrWindow) {
                        break;
                    }
                    examined++;
                    if (recentCounts.ContainsKey(pair.Value.Index)) {
                        recentCounts[pair.Value.Index]++;
                    }
                }

                var weightedChoices = new List<KeyValuePair<long, double>>();
                double totalWeight = 0.0;
                foreach (long index in choices) {
                    int recentUses = recentCounts.TryGetValue(index, out int count) ? count : 0;
                    // Inverse-square-ish weighting favours variety without making the
                    // sequence deterministic. A never/recently-unused variant weighs most.
                    double weight = 1.0 / Math.Pow(1.0 + recentUses, 1.5);
                    weightedChoices.Add(new KeyValuePair<long, double>(index, weight));
                    totalWeight += weight;
                }

                double roll = rrRandom.NextDouble() * totalWeight;
                long selected = weightedChoices[weightedChoices.Count - 1].Key;
                foreach (var weightedChoice in weightedChoices) {
                    roll -= weightedChoice.Value;
                    if (roll < 0.0) {
                        selected = weightedChoice.Key;
                        break;
                    }
                }

                history[notePosition] = new RrSelection { Index = selected };
                while (history.Count > MaxRrSelectionsPerAlias) {
                    history.Remove(history.Keys.First());
                }
                return selected;
            }
        }

        private static Random CreateRrRandom() {
            // Random() alone can receive similar time-based seeds when multiple phonemizers
            // are created quickly. Seed it from the platform RNG to make each instance's
            // random stream independent. Selection itself remains fast and runs under rrLock.
            try {
                using (var generator = RandomNumberGenerator.Create()) {
                    var seedBytes = new byte[4];
                    generator.GetBytes(seedBytes);
                    return new Random(BitConverter.ToInt32(seedBytes, 0));
                }
            } catch {
                // Defensive fallback for unusual runtimes where the platform RNG is unavailable.
                return new Random(unchecked(Environment.TickCount * 397 ^ Guid.NewGuid().GetHashCode()));
            }
        }

        private static bool IsExplicitRrAlias(string alias) {
            if (string.IsNullOrEmpty(alias)) {
                return false;
            }
            var match = rrSuffixRegex.Match(alias);
            return match.Success && long.TryParse(match.Groups["index"].Value, out _);
        }

        private void ClearRoundRobinState(bool clearSelections) {
            lock (rrLock) {
                rrVariantsByBase = new Dictionary<string, List<RrVariant>>(StringComparer.Ordinal);
                rrIndexedSinger = null;
                rrIndexedOtoList = null;
                rrIndexedOtoCount = -1;
                rrIndexedOtoDirty = false;
                if (clearSelections) {
                    rrSelectionsByBase.Clear();
                }
            }
        }

        private static readonly Dictionary<string, string> romajiMoraMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["a"]="あ", ["i"]="い", ["u"]="う", ["e"]="え", ["o"]="お",
            ["ka"]="か", ["ki"]="き", ["ku"]="く", ["ke"]="け", ["ko"]="こ",
            ["ga"]="が", ["gi"]="ぎ", ["gu"]="ぐ", ["ge"]="げ", ["go"]="ご",
            ["sa"]="さ", ["shi"]="し", ["si"]="し", ["shu"]="しゅ", ["syu"]="しゅ", ["sha"]="しゃ", ["sya"]="しゃ", ["sho"]="しょ", ["syo"]="しょ", ["su"]="す", ["se"]="せ", ["so"]="そ",
            ["za"]="ざ", ["ji"]="じ", ["zi"]="じ", ["ju"]="じゅ", ["zyu"]="じゅ", ["ja"]="じゃ", ["zya"]="じゃ", ["jo"]="じょ", ["zyo"]="じょ", ["zu"]="ず", ["ze"]="ぜ", ["zo"]="ぞ",
            ["ta"]="た", ["chi"]="ち", ["ti"]="ち", ["tsu"]="つ", ["tu"]="つ", ["te"]="て", ["to"]="と",
            ["da"]="だ", ["di"]="ぢ", ["du"]="づ", ["de"]="で", ["do"]="ど",
            ["na"]="な", ["ni"]="に", ["nu"]="ぬ", ["ne"]="ね", ["no"]="の",
            ["ha"]="は", ["hi"]="ひ", ["fu"]="ふ", ["hu"]="ふ", ["he"]="へ", ["ho"]="ほ",
            ["ba"]="ば", ["bi"]="び", ["bu"]="ぶ", ["be"]="べ", ["bo"]="ぼ",
            ["pa"]="ぱ", ["pi"]="ぴ", ["pu"]="ぷ", ["pe"]="ぺ", ["po"]="ぽ",
            ["ma"]="ま", ["mi"]="み", ["mu"]="む", ["me"]="め", ["mo"]="も",
            ["ya"]="や", ["yu"]="ゆ", ["yo"]="よ",
            ["ra"]="ら", ["ri"]="り", ["ru"]="る", ["re"]="れ", ["ro"]="ろ",
            ["wa"]="わ", ["wi"]="うぃ", ["we"]="うぇ", ["wo"]="を",
            ["kya"]="きゃ", ["kyu"]="きゅ", ["kyo"]="きょ", ["gya"]="ぎゃ", ["gyu"]="ぎゅ", ["gyo"]="ぎょ",
            ["jya"]="じゃ", ["jyu"]="じゅ", ["jyo"]="じょ",
            ["cha"]="ちゃ", ["chu"]="ちゅ", ["cho"]="ちょ", ["tya"]="ちゃ", ["tyu"]="ちゅ", ["tyo"]="ちょ",
            ["nya"]="にゃ", ["nyu"]="にゅ", ["nyo"]="にょ", ["hya"]="ひゃ", ["hyu"]="ひゅ", ["hyo"]="ひょ",
            ["bya"]="びゃ", ["byu"]="びゅ", ["byo"]="びょ", ["pya"]="ぴゃ", ["pyu"]="ぴゅ", ["pyo"]="ぴょ",
            ["mya"]="みゃ", ["myu"]="みゅ", ["myo"]="みょ", ["rya"]="りゃ", ["ryu"]="りゅ", ["ryo"]="りょ",
            ["kwa"]="くぁ", ["kwi"]="くぃ", ["kwu"]="くぅ", ["kwe"]="くぇ", ["kwo"]="くぉ",
            ["gwa"]="ぐぁ", ["gwi"]="ぐぃ", ["gwu"]="ぐぅ", ["gwe"]="ぐぇ", ["gwo"]="ぐぉ",
            ["tsa"]="つぁ", ["tsi"]="つぃ", ["tse"]="つぇ", ["tso"]="つぉ",
            ["fa"]="ふぁ", ["fi"]="ふぃ", ["fe"]="ふぇ", ["fo"]="ふぉ",
            ["va"]="ゔぁ", ["vi"]="ゔぃ", ["vu"]="ゔ", ["ve"]="ゔぇ", ["vo"]="ゔぉ",
            ["xya"]="ゃ", ["xyu"]="ゅ", ["xyo"]="ょ", ["lya"]="ゃ", ["lyu"]="ゅ", ["lyo"]="ょ",
            ["xtsu"]="っ", ["ltsu"]="っ", ["xtu"]="っ", ["ltu"]="っ", ["s"]="s", ["sh"]="sh", ["S"]="S", ["z"]="z", ["Z"]="Z", ["zh"]="zh",
        };

        private static readonly string[] romajiMoraPatterns = romajiMoraMap.Keys
            .OrderByDescending(key => key.Length)
            .ToArray();

        private bool TrySplitMultiMoraLyric(string lyric, out List<string> morae) {
            morae = new List<string>();
            if (string.IsNullOrEmpty(lyric) || lyric.Any(char.IsWhiteSpace)) {
                return false;
            }
            string normalized = lyric.Normalize();
            string hiragana = ConvertKatakanaToHiragana(normalized);
            if (hiragana.All(IsKanaMoraCharacter)) {
                morae = SplitKanaMorae(hiragana);
                return morae.Count > 0;
            }

            var parsed = new List<string>();
            int position = 0;
            while (position < normalized.Length) {
                // A doubled consonant is a small っ, except for 'nn', where the first n is ん.
                if (position + 1 < normalized.Length
                    && char.ToLowerInvariant(normalized[position]) == char.ToLowerInvariant(normalized[position + 1])
                    && IsRomajiConsonant(normalized[position])
                    && char.ToLowerInvariant(normalized[position]) != 'n') {
                    parsed.Add("っ");
                    position++;
                    continue;
                }
                if (char.ToLowerInvariant(normalized[position]) == 'n'
                    && position + 1 < normalized.Length
                    && (normalized[position + 1] == '\'' || normalized[position + 1] == '’')) {
                    parsed.Add("ん");
                    position += 2;
                    continue;
                }

                string match = romajiMoraPatterns.FirstOrDefault(pattern =>
                    position + pattern.Length <= normalized.Length
                    && string.Compare(normalized, position, pattern, 0, pattern.Length, StringComparison.OrdinalIgnoreCase) == 0);
                if (match != null) {
                    string mapped;
                    // Respect custom presamp replacements where available; otherwise use the built-in kana mapping.
                    bool hasCustomReplace = presamp?.Replace != null && presamp.Replace.Any(pair => pair.Key == match);
                    if (hasCustomReplace || string.Equals(match, "wo", StringComparison.OrdinalIgnoreCase)) {
                        mapped = match;
                    } else {
                        mapped = romajiMoraMap[match];
                    }
                    parsed.Add(mapped);
                    position += match.Length;
                    continue;
                }

                // 'n' before a consonant or at the end is the moraic nasal.
                if (char.ToLowerInvariant(normalized[position]) == 'n'
                    && (position + 1 == normalized.Length
                        || !IsRomajiVowel(normalized[position + 1]))) {
                    parsed.Add("ん");
                    position++;
                    continue;
                }
                return false;
            }
            morae = parsed;
            return morae.Count > 0;
        }

        private static List<string> SplitKanaMorae(string lyric) {
            var morae = new List<string>();
            foreach (char ch in lyric) {
                string value = ch.ToString();
                if (IsSmallKana(ch) && morae.Count > 0) {
                    morae[morae.Count - 1] += value;
                } else if (ch == 'ー' && morae.Count > 0) {
                    morae[morae.Count - 1] += value;
                } else {
                    morae.Add(value);
                }
            }
            return morae;
        }

        private static string ConvertKatakanaToHiragana(string value) {
            var chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++) {
                if (chars[i] >= 'ァ' && chars[i] <= 'ヶ') {
                    chars[i] = (char)(chars[i] - 0x60);
                }
            }
            return new string(chars);
        }

        private static bool IsKanaMoraCharacter(char ch) {
            return (ch >= 'ぁ' && ch <= 'ゖ') || ch == 'ー' || IsSmallKana(ch);
        }

        private static bool IsSmallKana(char ch) {
            return "ぁぃぅぇぉゃゅょゎゕゖ".Contains(ch);
        }

        private static bool IsRomajiVowel(char ch) {
            return "aiueoAIUEO".Contains(ch);
        }

        private static bool IsRomajiConsonant(char ch) {
            return char.IsLetter(ch) && !IsRomajiVowel(ch);
        }

        private string ParseLyricFromNote(Note note) {
            var lyric = note.lyric.Normalize(); // Normalize(): measures for Unicode
            if (!string.IsNullOrEmpty(note.phoneticHint)) {
                lyric = note.phoneticHint.Normalize();
            } else {

                if (string.Equals(lyric, "wo", StringComparison.OrdinalIgnoreCase)) {
                    return "を";
                }
                
                // replace (exact match)
                if (presamp.Replace != null) {
                    foreach (var pair in presamp.Replace) {
                        if (pair.Key == lyric) {
                            lyric = pair.Value;
                            break;
                        }
                    }
                }
            }
            return lyric;
        }

        private string ParseAliasFromLyric(string lyric) {
            // Keep the dedicated nasal aliases intact; presamp parsing may otherwise treat the trailing 2 as a suffix.
            if (IsNasalMora(lyric)) {
                return lyric;
            }
            string alias = presamp.ParseAlias(lyric)[1]; // 0:preVowel, 1:phoneme, 2:suffix
            if (alias != "・" && alias.Contains("・")) {
                alias = alias.Replace("・", "");
            }
            return alias;
        }

        private static bool IsNasalMora(string lyric) {
            return lyric == "ん" || lyric == "ン" || lyric == "ン2";
        }

        // Returns ン before velars (k/g series) and ン2 before bilabials (p/b/m series).
        // For other consonants, leave the lyric unchanged and let ordinary alias fallback apply.
        private string GetNasalVariantForNext(Note? next) {
            if (next == null) {
                return "ン";
            }
            if (!string.IsNullOrEmpty(next.Value.phoneticHint)) {
                return null;
            }
            var nextLyric = ParseLyricFromNote(next.Value);
            var nextAlias = ParseAliasFromLyric(nextLyric);
            string consonant = string.Empty;
            if (TryGetPresampPhoneme(nextAlias, out PresampPhoneme phoneme) && phoneme.HasConsonant) {
                consonant = (phoneme.Consonant ?? string.Empty).ToLowerInvariant();
            }

            if (StartsWithAny(consonant, "k", "g", "ky", "gy", "kw", "gw")) {
                return "ン";
            }
            if (StartsWithAny(consonant, "p", "b", "m")) {
                return "ン2";
            }

            // Kana fallback for banks whose presamp phoneme table doesn't describe the next lyric.
            if (StartsWithAny(nextLyric, "か", "き", "く", "け", "こ", "が", "ぎ", "ぐ", "げ", "ご")) {
                return "ン";
            }
            if (StartsWithAny(nextLyric, "ぱ", "ぴ", "ぷ", "ぺ", "ぽ", "ば", "び", "ぶ", "べ", "ぼ", "ま", "み", "む", "め", "も")) {
                return "ン2";
            }
            return null;
        }

        private static bool StartsWithAny(string value, params string[] prefixes) {
            if (string.IsNullOrEmpty(value)) {
                return false;
            }
            return prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasVowelAlias(string alias, PresampPhoneme phoneme) {
            return IsNasalMora(alias) || phoneme.HasVowel;
        }

        private static string GetVowelAlias(string alias, PresampPhoneme phoneme) {
            if (alias == "ン" || alias == "ン2") {
                return alias;
            }
            return phoneme.Vowel;
        }

        // Expand only nasal aliases, preserving the selected allophone as the first candidate.
        // This lets one phonemizer work with banks that contain ん, ン, ン2, or a subset of them.
        private static IEnumerable<string> ExpandNasalAliases(IEnumerable<string> aliases) {
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var alias in aliases) {
                if (string.IsNullOrEmpty(alias)) {
                    continue;
                }
                var candidates = new List<string> { alias };
                if (alias.Contains("ン2")) {
                    candidates.Add(alias.Replace("ン2", "ン"));
                    candidates.Add(alias.Replace("ン2", "ん"));
                } else if (alias.Contains("ン")) {
                    candidates.Add(alias.Replace("ン", "ン2"));
                    candidates.Add(alias.Replace("ン", "ん"));
                } else if (alias.Contains("ん")) {
                    candidates.Add(alias.Replace("ん", "ン"));
                    candidates.Add(alias.Replace("ん", "ン2"));
                }
                foreach (var candidate in candidates) {
                    if (emitted.Add(candidate)) {
                        yield return candidate;
                    }
                }
            }
        }

        private bool TryGetPresampPhoneme(string alias, out PresampPhoneme pPhoneme) {
            // Treat ン and ン2 as vowel-bearing nasal phonemes even when presamp.ini only defines ん.
            if (alias == "ン" || alias == "ン2") {
                if (presamp.PhonemeList.TryGetValue("ん", out pPhoneme)
                    || presamp.PhonemeList.TryGetValue(alias, out pPhoneme)) {
                    return true;
                }
                pPhoneme = default;
                return true;
            }
            if (presamp.PhonemeList.TryGetValue(alias, out pPhoneme)) {
                return true;
            } else {
                var match = Regex.Match(alias, @".+([ぁぃぅぇぉゃゅょ])");
                if (match.Success) {
                    return presamp.PhonemeList.TryGetValue(match.Groups[1].Value, out pPhoneme);
                }
            }
            return false;
        }
    }
}
