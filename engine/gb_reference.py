"""British read-aloud targets for the bundled English acoustic model."""
from functools import lru_cache
import json
from pathlib import Path
import re
import sys
import unicodedata

import demo_voices
from passages import PUNCT, TOKEN


LOCALE = "en-gb"
PROFILE = "en-GB-rp-v1"
_DATA = Path(__file__).with_name("lexicons")
_PHONES = re.compile(r"(?:aɪə|aʊə|eɪ|aɪ|ɔɪ|əʊ|aʊ|iə|ɪə|eə|ʊə|tʃ|dʒ|[^\sː̃])(?:ː|̃)*")
_IGNORED = str.maketrans("", "", "ˈˌ.-ʲ")
_COMMON = str.maketrans({"ɐ": "ə", "ᵻ": "ɪ", "ɫ": "l", "r": "ɹ", "g": "ɡ"})
_MODEL_SYMBOLS = {"ɪə":"iə", "ɪəː":"iəː", "ɛː":"eə"}
# Misaki's compact alphabet uses one character for each diphthong and affricate.
_MISAKI = str.maketrans({"A": "eɪ", "I": "aɪ", "Q": "əʊ", "W": "aʊ", "Y": "ɔɪ",
                         "ʧ": "tʃ", "ʤ": "dʒ", "ᵊ": "ə", "a": "æ"})
_VOWELS = frozenset("aeiouæɑɒɔəɛɜɪʊʌ")
_FORMS = {
    "a": ("ə", "eɪ"), "an": ("ən", "æn"), "the": ("ðə", "ði", "ðɪ"),
    "and": ("ænd", "ənd", "ən"), "to": ("tuː", "tə"), "of": ("ɒv", "əv"),
    "for": ("fɔː", "fə"), "or": ("ɔː", "ə"), "are": ("ɑː", "ə"),
    "was": ("wɒz", "wəz"), "were": ("wɜː", "wə"), "at": ("æt", "ət"),
    "can": ("kæn", "kən"), "that": ("ðæt", "ðət"), "have": ("hæv", "həv", "əv"),
    "has": ("hæz", "həz", "əz"), "had": ("hæd", "həd", "əd"),
    "from": ("fɹɒm", "fɹəm"), "you": ("juː", "jə"), "your": ("jɔː", "jə"),
}


def _tokens(ipa):
    text = unicodedata.normalize("NFC", ipa).translate(_IGNORED).translate(_COMMON)
    return tuple(_MODEL_SYMBOLS.get(p, p) for p in _PHONES.findall(text))


@lru_cache(maxsize=1)
def _lexicon():
    entries = {}
    def add(word, ipa):
        group = _tokens(ipa)
        if group:
            entries.setdefault(word, {})[group] = None

    with (_DATA / "en_UK.txt").open(encoding="utf-8") as source:
        for line in source:
            word, values = line.rstrip().split("\t")
            for value in values.strip("/").split(","):
                add(word, value)
    with (_DATA / "gb_gold.json").open(encoding="utf-8") as source:
        for word, forms in json.load(source).items():
            if isinstance(forms, str):
                forms = [forms]
            else:
                forms = [forms.get("DEFAULT"), *forms.values()]
            for value in forms:
                if value:
                    add(word, value.translate(_MISAKI))
    for word, forms in _FORMS.items():
        for value in forms:
            add(word, value)
    lookup = {word: tuple(groups) for word, groups in entries.items()}
    for word, forms in tuple(lookup.items()):
        lookup.setdefault(word.casefold(), forms)
    return lookup


@lru_cache(maxsize=4096)
def _options(word):
    lexicon = _lexicon()
    if word.casefold() in _FORMS:
        word = word.casefold()
    known = lexicon.get(word) or lexicon.get(word.casefold())
    if known:
        return known
    if len(word) > 100:
        return ((),)
    return (_tokens(demo_voices.british_ipa(word)),)


def is_guessed(word):
    word = word.casefold()
    return word not in _lexicon() and not (word.isdecimal() and len(word) <= 9)


@lru_cache(maxsize=512)
def _reference(text):
    spellings = TOKEN.findall(text.translate(PUNCT))
    words = tuple(word.casefold() for word in spellings)
    options = [_options(word) for word in spellings]
    # A written final r can link to a following vowel in connected speech.
    # Keep the unlinked variant available for a pause at the same word boundary.
    for i, word in enumerate(words[:-1]):
        if word.endswith(("r", "re")) and any(g and g[0][0] in _VOWELS for g in options[i+1]):
            linked = [g + ("ɹ",) for g in options[i] if g and g[-1][0] in _VOWELS]
            options[i] = tuple(dict.fromkeys((*options[i], *linked)))
    return words, tuple(options)


def phones_by_word(text, recognition=None):
    words, options = _reference(text)
    groups = [choices[0] for choices in options]
    if recognition is None or not any(len(choices)>1 for choices in options):
        return words, tuple(groups)

    from openpronounce import alignment, gop
    frames, vocab = recognition.log_posteriors, recognition.vocab
    base = gop._Labels(frames, vocab, groups, LOCALE)
    if not base.sequence or not alignment.fits(len(frames), base.sequence):
        return words, tuple(groups)
    spans, _ = alignment.forced_align(base.reduced, base.sequence)
    bounds = {}
    for word_index, _, _, indices in base.phones:
        start, end = spans[indices[0]][0], spans[indices[-1]][1]
        bounds[word_index] = (bounds.get(word_index, (start, end))[0], end)
    for i, choices in enumerate(options):
        if len(choices)==1 or i not in bounds:
            continue
        start, end = bounds[i]
        previous_end = bounds.get(i-1, (0, 0))[1]
        next_start = bounds.get(i+1, (len(frames), len(frames)))[0]
        window = frames[max(0, (previous_end+start)//2-1):min(len(frames), (end+next_start+1)//2+1)]
        # Compare CTC likelihood on the same frames, only among attested British forms.
        best = float("-inf")
        for candidate in choices:
            labels = gop._Labels(window, vocab, [candidate], LOCALE)
            if len(labels.phones)!=len(candidate) or not labels.sequence or not alignment.fits(len(window), labels.sequence):
                continue
            likelihood = alignment.log_likelihood(labels.reduced, labels.sequence)
            if likelihood > best:
                best, groups[i] = likelihood, candidate
    return words, tuple(groups)


@lru_cache(maxsize=1)
def enable():
    from openpronounce.languages import LANGUAGES, Language
    LANGUAGES[LOCALE] = Language(LOCALE, "en-gb-x-rp", LANGUAGES["en"].asr_model,
                                 "British English (RP)", pronunciation=sys.modules[__name__])
    return LOCALE
