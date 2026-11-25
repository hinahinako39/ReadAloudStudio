"""Offline British demonstration voices and their spoken word transcriptions."""
from base64 import b64encode
from functools import lru_cache
from pathlib import Path
import re
import threading
import unicodedata


VOICES = {"gb-cori": "cori", "gb-northern": "northern_english_male"}
_CACHE = Path(__file__).parent / "models" / "hub" / "models--rhasspy--piper-voices"
# Both bundled model configs specify this English pronunciation dictionary.
_GB_PHONES = "en-gb-x-rp"
_VOWELS = re.compile(r"eɪ|aɪ|ɔɪ|əʊ|aʊ|ɪə|eə|ʊə|[aeiouɑɐɒæɔəɛɜɪʊʌ̩]")
_lock = threading.Lock()  # eSpeak has process-wide voice selection.


@lru_cache(maxsize=1)
def _phonemizer():
    from piper.phonemize_espeak import EspeakPhonemizer
    return EspeakPhonemizer()


@lru_cache(maxsize=4096)
def british_ipa(word):
    with _lock:
        groups = _phonemizer().phonemize(_GB_PHONES, word)
    ipa = unicodedata.normalize("NFC", " ".join("".join(group) for group in groups)).strip()
    if len(_VOWELS.findall(ipa)) == 1:
        ipa = ipa.replace("ˈ", "").replace("ˌ", "")
    return ipa


@lru_cache(maxsize=len(VOICES))
def _load(voice_id):
    from piper import PiperVoice
    name = VOICES[voice_id]
    revision = (_CACHE / "refs" / "main").read_text(encoding="utf-8").strip()
    folder = _CACHE / "snapshots" / revision / "en" / "en_GB" / name / "medium"
    return PiperVoice.load(folder / f"en_GB-{name}-medium.onnx")


def synthesize(voice_id, text):
    # Practice text is plain English. Brackets in an article are literal punctuation.
    text = text.replace("[[", " ").replace("]]", " ")
    with _lock:
        voice = _load(voice_id)
        sample_rate = voice.config.sample_rate
        gap = bytes(2 * round(sample_rate * .22))
        pcm = gap.join(chunk.audio_int16_bytes for chunk in voice.synthesize(text))
    if not pcm:
        raise ValueError("这段文字没有生成可播放的声音。")
    return {"pcm": b64encode(pcm).decode("ascii"), "sampleRate": sample_rate}
