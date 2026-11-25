"""Offline English read-aloud assessment and practice history for the desktop client."""
from __future__ import annotations

import argparse
import base64
import binascii
import difflib
import functools
import hmac
import json
import logging
import os
from pathlib import Path
import re
import sqlite3
import sys
import threading
import time
import uuid
import wave
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# The bundled interpreter runs in isolated mode and omits the script directory.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from passages import PUNCT, TOKEN, locate, split_reference, timed_words
import demo_voices
import gb_reference
import reference_audio


PHONE_MODEL = "OpenPronounce/wav2vec2-base-phones-en"
ASR_MODEL = "facebook/wav2vec2-large-960h"
ENGINE = "OpenPronounce 1.1.0 · English distilled"
SAMPLE_RATE = 16000
MAX_SECONDS = 300
MAX_CHARACTERS = 6000
QUALITY = {"silent": "录音中没有足够的人声", "too quiet": "麦克风音量太低",
           "too short": "录音太短", "clipped": "声音削波，请降低输入音量",
           "noisy": "背景噪声较大"}


class InputError(ValueError):
    pass


def canonical_id(value):
    try:
        return str(uuid.UUID(str(value)))
    except (ValueError, TypeError, AttributeError) as exc:
        raise InputError("练习编号无效。") from exc


def reference_key(text):
    return " ".join(w.lower() for w in TOKEN.findall(text.translate(PUNCT)))


def checked_reference(request, *, allow_numbers=False):
    reference = str(request.get("reference") or "").strip()
    pattern = "[A-Za-z0-9]" if allow_numbers else "[A-Za-z]"
    if not re.search(pattern, reference) or len(reference) > MAX_CHARACTERS:
        raise InputError("请输入英文句子或段落（最多 6000 个字符）。")
    return reference


def prepare_reference(reference):
    """Prepare British scoring targets and independent British/American demonstration readings."""
    from openpronounce import g2p
    from openpronounce.phones import normalize_phones

    lexicon = g2p._lexicon("en")
    gb_reference.enable()
    sections, words = [], []
    for index, sentence in enumerate(split_reference(reference)):
        first = len(words)
        tokens = TOKEN.findall(sentence.translate(PUNCT))
        _, scoring_groups = gb_reference.phones_by_word(" ".join(tokens))
        for token, scoring_group in zip(tokens, scoring_groups, strict=True):
            symbols = lexicon.get(token.lower(), ())
            # SAPI writes HH as h, unstressed AH as ax, and places stress after the vowel.
            sapi = []
            for symbol in symbols:
                phone = symbol.rstrip("012").lower()
                sapi.append("h" if phone == "hh" else "ax" if symbol == "AH0" else phone)
                if symbol[-1] in "12":
                    sapi.append(symbol[-1])
            american_ipa, british_ipa = "", ""
            if len(token) <= 100:
                _, groups = g2p.phones_by_word(token)
                american_ipa = "".join(normalize_phones(groups[0], "en")) if groups else ""
                british_ipa = demo_voices.british_ipa(token)
            words.append({"index": len(words), "text": token, "ipa": "".join(scoring_group),
                          "guessed": gb_reference.is_guessed(token), "americanIpa": american_ipa,
                          "sapi": " ".join(sapi), "britishIpa": british_ipa})
        sections.append({"index": index, "reference": sentence, "firstWord": first, "wordCount": len(words)-first})
    return {"sections": sections, "words": words, "referenceAccent": "en-GB", "referenceProfile": gb_reference.PROFILE}


def configure_runtime(data):
    root = Path(__file__).resolve().parent
    model_root = root / "models"
    cache = data / "cache"
    cache.mkdir(exist_ok=True)
    os.environ.update({
        "HF_HOME": str(model_root), "HF_HUB_CACHE": str(model_root / "hub"),
        "HF_HUB_OFFLINE": "1", "TRANSFORMERS_OFFLINE": "1", "HF_HUB_DISABLE_TELEMETRY": "1",
        "HF_HUB_DISABLE_PROGRESS_BARS": "1", "TOKENIZERS_PARALLELISM": "false",
        "OPENPRONOUNCE_PHONEME_MODEL": PHONE_MODEL,
        "NUMBA_CACHE_DIR": str(cache / "numba"), "OMP_NUM_THREADS": "6",
        "MPLCONFIGDIR": str(cache / "mpl"),
    })


@functools.lru_cache(maxsize=1)
def inference():
    return Inference()


class Inference:
    def __init__(self):
        import torch
        preference = os.environ.get("READALOUD_DEVICE", "auto").strip().lower()
        if preference not in ("auto", "cpu"):
            raise ValueError("READALOUD_DEVICE 可设为 auto 或 cpu。")
        torch.set_num_threads(min(6, os.cpu_count() or 1))
        note = "已指定 CPU" if preference == "cpu" else "未检测到可用的 CUDA 设备"
        if preference == "auto" and torch.cuda.is_available():
            try:
                # Running one kernel also tests support for the actual GPU architecture.
                torch.ones(1, device="cuda").add_(1).item()
                self.set_device("cuda", torch.cuda.get_device_name(), "")
                return
            except RuntimeError as exc:
                logging.warning("CUDA device probe failed: %s", exc)
                note = "CUDA 初始化失败，本次使用 CPU"
        self.set_device("cpu", "CPU", note)

    def set_device(self, kind, name, note):
        os.environ["OPENPRONOUNCE_DEVICE"] = kind
        self.device = {"kind": kind, "name": name, "note": note}

    def evaluate(self, samples, reference):
        from openpronounce.speech import compare_audio_with_text
        return self.run(compare_audio_with_text, samples, reference, lang=gb_reference.enable(), use_phone_model=True,
                        acoustic=False, transcription=True, prosody=False)

    def run(self, operation, *args, **options):
        try:
            return operation(*args, **options)
        except RuntimeError as exc:
            if self.device["kind"] != "cuda" or not is_cuda_memory_error(exc):
                raise
            logging.warning("CUDA memory exhausted. Retrying assessment on CPU: %s", exc)

        # The pinned library caches both networks and its device process-wide.
        # Leave the exception scope before releasing CUDA tensors held by its traceback.
        import gc
        import torch
        from openpronounce import device, phones, speech
        speech._load_models.cache_clear()
        phones._load_model.cache_clear()
        device.get_device.cache_clear()
        gc.collect()
        torch.cuda.empty_cache()
        self.set_device("cpu", "CPU", "显存不足，本次会话已切换到 CPU")
        return operation(*args, **options)


def is_cuda_memory_error(exc):
    import torch
    return isinstance(exc, torch.cuda.OutOfMemoryError) or any(message in str(exc).lower() for message in (
        "cuda out of memory", "cuda error: out of memory", "cublas_status_alloc_failed", "cudnn_status_alloc_failed"))


def read_wav(path):
    import numpy as np
    try:
        with wave.open(str(path), "rb") as audio:
            if (audio.getframerate(), audio.getnchannels(), audio.getsampwidth()) != (SAMPLE_RATE, 1, 2):
                raise InputError("评测录音需要 16 kHz、单声道、16 位 PCM WAV。")
            frames = audio.getnframes()
            if frames > SAMPLE_RATE * MAX_SECONDS:
                raise InputError("每次练习最长 5 分钟，请将更长的文章分段录制。")
            samples = np.frombuffer(audio.readframes(frames), dtype="<i2").astype("float32") / 32768
    except (wave.Error, EOFError) as exc:
        raise InputError("无法读取这段 WAV 录音。") from exc
    return samples


def get_peaks(samples):
    import numpy as np
    if not len(samples):
        return []
    values = [float(np.max(np.abs(section))) for section in np.array_split(samples, min(96, len(samples)))]
    peak = max(max(values), .001)
    return [round((v / peak) ** .65, 3) for v in values]


def spoken_tokens(text):
    words = []
    for token in TOKEN.findall(text.translate(PUNCT)):
        if token.isdigit():
            from openpronounce.g2p import number_words
            if len(token) <= 9 and not (len(token) > 1 and token.startswith("0")):
                words.extend(number_words(int(token)))
            else:
                words.extend(number_words(int(digit))[0] for digit in token)
        else:
            words.append(token)
    return words


def text_differences(reference, transcript):
    expected = spoken_tokens(reference)
    heard = spoken_tokens(transcript)
    matcher = difflib.SequenceMatcher(None, [w.lower() for w in expected], [w.lower() for w in heard], autojunk=False)
    labels = {"delete": "疑似漏读", "insert": "疑似多读", "replace": "转写差异"}
    items = []
    for operation, a, b, c, d in matcher.get_opcodes():
        if operation == "equal":
            continue
        left, right = " ".join(expected[a:b]), " ".join(heard[c:d])
        phrase = f"{left} → {right}" if left and right else left or right
        items.append(f"{labels[operation]}：{phrase}")
    return items


def quality_reasons(reasons):
    messages = []
    for reason in reasons:
        if reason.startswith("recording "):
            key = reason.removeprefix("recording ")
            message = QUALITY.get(key, key)
        elif reason.startswith("not said: "):
            message = "可能漏读了 " + reason.removeprefix("not said: ")
        elif "does not match" in reason:
            message = "录音与目标句匹配度低，请核对句子后重读"
        elif "not in the sentence" in reason:
            message = "检测到了明显超出原句的语音"
        else:
            message = str(reason)
        if message not in messages:
            messages.append(message)
    return messages


def ipa(value):
    return "".join(value) if isinstance(value, list) else str(value or "")


def seconds(value, duration):
    if value is None:
        return None
    return round(max(0, min(duration, float(value))), 4)


def analyze_clip(reference, samples):
    import numpy as np
    duration = len(samples) / SAMPLE_RATE
    peaks = get_peaks(samples)
    tokens = TOKEN.findall(reference.translate(PUNCT))
    insufficient = duration < .2
    silent = not len(samples) or float(np.max(np.abs(samples))) < .0005
    used_device = None
    if insufficient or silent:
        reason = "recording silent" if silent else "recording too short"
        raw = {"reliable": False, "reasons": [reason], "differences": {}, "transcribe": ""}
    else:
        runtime = inference()
        raw = runtime.evaluate(samples, " ".join(tokens))
        used_device = runtime.device
    differences = raw.get("differences") or {}
    indexed = {int(w["position"]): w for w in differences.get("words", [])}
    words = []
    for index, token in enumerate(tokens):
        w = indexed.get(index, {})
        words.append({
            "index": index, "text": token, "ipa": ipa(w.get("expected")),
            "start": seconds(w.get("start"), duration), "end": seconds(w.get("end"), duration),
            "flagged": bool(w.get("mispronounced")), "missing": bool(w.get("missing")),
            "guessed": bool(w.get("guessed")), "risk": float(w.get("confidence") or 0),
            "phones": [{"expected": ipa(p.get("expected")), "heard": ipa(p.get("heard")),
                        "risk": float(p.get("confidence") or 0),
                        "start": seconds(p.get("start"), duration), "end": seconds(p.get("end"), duration)}
                       for p in w.get("phones", [])],
        })
    reliable = bool(raw["reliable"] and raw.get("scores") and indexed)
    reasons = quality_reasons(raw.get("reasons", []))
    if not reliable and not reasons:
        reasons.append("这段录音未能可靠对齐，请完整读一遍。")
    result = {"referenceAccent": "en-GB", "referenceProfile": gb_reference.PROFILE,
              "transcript": raw.get("transcribe") or "", "reliable": reliable, "reasons": reasons,
              "words": words, "peaks": peaks, "duration": round(duration, 4),
              "scores": {k: round(float(raw["scores"][k]), 1) for k in ("total","accuracy","fluency","prosodic")}
                        if reliable else {},
              "textDiff": text_differences(reference, raw.get("transcribe") or "") if raw.get("transcribe") else [],
              "extra": [{"start": seconds(e.get("start"), duration), "end": seconds(e.get("end"), duration),
                         "heard": ipa(e.get("heard"))} for e in differences.get("extra", [])],
              "engine": ENGINE, "asrModel": ASR_MODEL, "phoneModel": PHONE_MODEL, "device": used_device}
    return result, raw


def shift_times(items, offset):
    for item in items:
        for key in ("start", "end"):
            if item.get(key) is not None:
                item[key] = round(item[key] + offset, 4)


def analyze(reference, path):
    import numpy as np
    samples = read_wav(path)
    duration = len(samples) / SAMPLE_RATE
    texts = split_reference(reference)
    if len(texts) <= 1 and duration <= 60:
        return analyze_clip(reference, samples)

    no_voice = duration < .2 or not len(samples) or float(np.max(np.abs(samples))) < .0005
    runtime = None if no_voice else inference()
    heard = runtime.run(timed_words, samples, SAMPLE_RATE) if runtime else []
    devices = {runtime.device["kind"]: runtime.device} if runtime else {}
    locations, insertions = locate([[w.lower() for w in spoken_tokens(t)] for t in texts], heard, duration)
    sections, words, raw_sections = [], [], []
    for index, (text, loc) in enumerate(zip(texts, locations)):
        tokens = TOKEN.findall(text.translate(PUNCT))
        start_word = len(words)
        part = {"index": index, "reference": text, "firstWord": start_word, "wordCount": len(tokens),
                "start": loc.start, "end": loc.end, "status": loc.status, "reliable": False, "scores": {},
                "reasons": [], "transcript": loc.transcript, "textDiff": [], "extra": [], "peaks": [], "device": None}
        raw = None
        if loc.status == "located" and .2 <= loc.end - loc.start <= 45:
            first_sample, last_sample = round(loc.start*SAMPLE_RATE), round(loc.end*SAMPLE_RATE)
            offset = first_sample / SAMPLE_RATE
            clip, raw = analyze_clip(text, samples[first_sample:last_sample])
            for key in ("transcript", "reliable", "scores", "reasons", "textDiff", "extra", "peaks", "device"):
                part[key] = clip[key]
            part["status"] = "scored" if clip["reliable"] else "review"
            if part["device"]:
                devices[part["device"]["kind"]] = part["device"]
            shift_times(clip["words"], offset)
            shift_times(part["extra"], offset)
            for w in clip["words"]:
                w["index"] += start_word
                shift_times(w["phones"], offset)
            words.extend(clip["words"])
        else:
            messages = {"missing": "转写中没有找到这句，可能漏读。",
                        "unlocated": "转写与这句缺少足够的对应词，无法可靠定位。",
                        "partial": "只定位到这句的一部分，暂不评分，可回听核对。",
                        "ambiguous": "原文有连续重复的句子，无法判断读的是哪一次。"}
            part["reasons"] = ["录音中没有足够的人声" if no_voice else
                               messages.get(loc.status, "这句的音频跨度不适合可靠精评，请回听核对。")]
            if loc.status == "located":
                part["status"] = "review"
            if loc.transcript:
                part["textDiff"] = text_differences(text, loc.transcript)
            if loc.start is not None:
                part["peaks"] = get_peaks(samples[round(loc.start*SAMPLE_RATE):round(loc.end*SAMPLE_RATE)])
            words.extend({"index": start_word+i, "text": t, "start": None, "end": None, "phones": []}
                         for i, t in enumerate(tokens))
        sections.append(part)
        raw_sections.append(raw)

    transcript = " ".join(w[0] for w in heard)
    extra = [{"start": heard[a][1], "end": heard[b-1][2], "heard": " ".join(w[0] for w in heard[a:b])}
             for a, b in insertions]
    substantial_extra = any(b-a >= 3 for a, b in insertions) or sum(b-a for a, b in insertions) > max(2, .2*len(words))
    count = sum(s["reliable"] for s in sections)
    reliable = bool(sections and count == len(sections) and not substantial_extra)
    reasons = []
    if not heard:
        reasons.append("录音中没有足够的人声" if no_voice else "未识别出可定位的英文语音")
    if count < len(sections):
        reasons.append(f"有 {len(sections)-count} 句待核对，暂不计算整段参考分")
    if substantial_extra:
        reasons.append("转写含明显多读的内容，暂不计算整段参考分")
    scores = {k: round(sum(s["scores"][k]*s["wordCount"] for s in sections)/len(words), 1)
              for k in ("total", "accuracy", "fluency", "prosodic")} if reliable else {}
    device = next(iter(devices.values()), None) if len(devices) <= 1 else {
        "kind": "mixed", "name": "GPU / CPU", "note": "部分分句在显存不足后改用 CPU 完成"}
    return ({"referenceAccent": "en-GB", "referenceProfile": gb_reference.PROFILE,
             "transcript": transcript, "words": words, "duration": round(duration, 4), "peaks": get_peaks(samples),
             "reliable": reliable, "scores": scores, "reasons": reasons, "sections": sections,
             "sentenceCount": len(sections), "assessedCount": count, "extra": extra,
             "textDiff": text_differences(reference, transcript) if transcript else [],
             "engine": ENGINE, "asrModel": ASR_MODEL, "phoneModel": PHONE_MODEL, "device": device},
            {"sections": raw_sections, "recognizedWords": heard})


class PracticeStore:
    _results = """SELECT a.id, a.reference, a.sentence_key, a.created,
                  coalesce(v.result_json, a.result_json) AS result_json
                  FROM attempts a LEFT JOIN assessment_versions v ON v.attempt_id=a.id AND v.profile=?"""

    def __init__(self, data):
        self.data = data
        self.db = data / "practice.sqlite3"
        for sub in ("drafts", "recordings", "example-drafts", "examples"):
            (data / sub).mkdir(parents=True, exist_ok=True)
        self.assessment_lock = threading.Lock()
        with self.connect() as db:
            db.execute("PRAGMA journal_mode=WAL")
            db.execute("""CREATE TABLE IF NOT EXISTS attempts (
                id TEXT PRIMARY KEY, reference TEXT NOT NULL, sentence_key TEXT NOT NULL,
                created TEXT NOT NULL, result_json TEXT NOT NULL, raw_json TEXT NOT NULL)""")
            db.execute("CREATE INDEX IF NOT EXISTS sentence_history ON attempts(sentence_key, created DESC)")
            db.execute("""CREATE TABLE IF NOT EXISTS assessment_versions (
                attempt_id TEXT NOT NULL REFERENCES attempts(id), profile TEXT NOT NULL,
                result_json TEXT NOT NULL, raw_json TEXT NOT NULL, PRIMARY KEY(attempt_id, profile))""")
            db.execute("""CREATE TABLE IF NOT EXISTS example_tracks (
                id TEXT PRIMARY KEY, reference TEXT NOT NULL, created TEXT NOT NULL, track_json TEXT NOT NULL)""")
            db.execute("CREATE INDEX IF NOT EXISTS example_text ON example_tracks(reference,created DESC)")

    def connect(self):
        return sqlite3.connect(self.db, timeout=10)

    def history(self):
        with self.connect() as db:
            rows = db.execute(f"""SELECT id, reference, sentence_key, created,
                json_extract(result_json,'$.reliable'),json_extract(result_json,'$.scores.total'),
                json_extract(result_json,'$.duration'), json_extract(result_json,'$.sentenceCount'),
                json_extract(result_json,'$.assessedCount'), json_extract(result_json,'$.referenceAccent'),
                json_extract(result_json,'$.sentencePractice')
                FROM ({self._results}) ORDER BY created DESC""", (gb_reference.PROFILE,))
            return [{"id": r[0], "reference": r[1], "sentenceKey": r[2], "created": r[3],
                     "reliable": bool(r[4]), "total": r[5], "duration": r[6],
                     "sentenceCount": r[7] or 0, "assessedCount": r[8] or 0,
                     "referenceAccent": r[9] or "en-US", "sentencePractice":json.loads(r[10]) if r[10] else None} for r in rows]

    def get(self, attempt_id):
        with self.connect() as db:
            row = db.execute(f"SELECT result_json FROM ({self._results}) WHERE id=?",
                             (gb_reference.PROFILE, attempt_id)).fetchone()
        if row:
            item = json.loads(row[0])
            item.setdefault("referenceAccent", "en-US")
            return item
        return None

    def _score_recording(self, attempt_id, reference, path, original_date=None, origin=None):
        started = time.monotonic()
        result, raw = analyze(reference, path)
        now = datetime.now(timezone.utc).isoformat()
        result.update(id=attempt_id, reference=reference, sentenceKey=reference_key(reference),
                      created=original_date or now, assessedAt=now, elapsed=round(time.monotonic()-started, 2))
        if origin is not None:
            result["sentencePractice"] = origin
        def serialize(value):
            return json.dumps(value, ensure_ascii=False, allow_nan=False, separators=(",", ":"))
        return result, serialize(result), serialize(raw)

    def assess(self, request):
        attempt_id = canonical_id(request.get("id"))
        reference = checked_reference(request, allow_numbers=request.get("sentencePractice") is not None)
        with self.assessment_lock:
            origin = self.sentence_origin(request.get("sentencePractice"), reference)
            existing = self.get(attempt_id)
            if existing:
                if existing["reference"] != reference or existing.get("sentencePractice") != origin:
                    raise InputError("这次录音已用于另一条目标句。")
                return existing
            staged = self.data / "drafts" / (attempt_id + ".wav")
            saved = self.data / "recordings" / (attempt_id + ".wav")
            path = saved if saved.is_file() else staged
            if not path.is_file():
                raise InputError("找不到待评测录音，请重新录制或导入。")
            result, result_json, raw_json = self._score_recording(attempt_id, reference, path, origin=origin)
            # Move before committing. A retry can finish an interrupted commit from the saved WAV.
            if path == staged:
                os.replace(staged, saved)
            with self.connect() as db:
                db.execute("INSERT INTO attempts VALUES (?,?,?,?,?,?)",
                           (attempt_id, reference, result["sentenceKey"], result["created"], result_json, raw_json))
            return result

    def sentence_origin(self, origin, reference):
        if origin is None:
            return None
        if not isinstance(origin, dict):
            raise InputError("单句重练与原文不匹配。")
        passage = checked_reference({"reference":origin.get("passage")})
        index = origin.get("sectionIndex")
        sentences = split_reference(passage)
        parent_id = origin.get("parentId")
        if parent_id is not None:
            parent_id = canonical_id(parent_id)
            parent = self.get(parent_id)
            if not parent or parent["reference"] != passage or parent.get("sentencePractice"):
                raise InputError("单句重练与原文不匹配。")
            sentences = [s["reference"] for s in parent.get("sections", [])]
        if type(index) is not int or index<0 or index>=len(sentences) or sentences[index] != reference:
            raise InputError("单句重练与原文不匹配。")
        return {"passage":passage,"sectionIndex":index,"parentId":parent_id}

    def rescore(self, request):
        attempt_id = canonical_id(request.get("id"))
        with self.assessment_lock:
            original = self.get(attempt_id)
            if not original:
                raise InputError("找不到这次练习。")
            if original.get("referenceProfile") == gb_reference.PROFILE:
                return original
            path = self.data / "recordings" / (attempt_id + ".wav")
            if not path.is_file():
                raise InputError("找不到这次练习的原声，无法重评。")
            _, result_json, raw_json = self._score_recording(attempt_id, original["reference"], path,
                                                            original["created"], original.get("sentencePractice"))
            # Keep the original result and raw evidence. Concurrent requests share one committed revision.
            with self.connect() as db:
                db.execute("INSERT OR IGNORE INTO assessment_versions VALUES (?,?,?,?)",
                           (attempt_id, gb_reference.PROFILE, result_json, raw_json))
            return self.get(attempt_id)

    @staticmethod
    def example_key(reference):
        return reference.replace("\r\n","\n").replace("\r","\n").strip()

    def examples(self, request):
        reference = self.example_key(checked_reference(request))
        with self.connect() as db:
            rows = db.execute("SELECT track_json FROM example_tracks WHERE reference=? ORDER BY created DESC",(reference,))
            return [json.loads(row[0]) for row in rows]

    @staticmethod
    def align_example(reference, samples):
        import numpy as np
        duration = len(samples)/SAMPLE_RATE
        heard = (inference().run(timed_words,samples,SAMPLE_RATE)
                 if duration>=.15 and np.max(np.abs(samples))>=.0005 else [])
        return reference_audio.align(reference, heard, duration, spoken_tokens)

    def example_timing(self, request):
        text = checked_reference(request, allow_numbers=True)
        rate = request.get("sampleRate")
        try:
            pcm = base64.b64decode(request.get("pcm",""), validate=True)
        except (ValueError,TypeError,binascii.Error) as exc:
            raise InputError("无法读取这段 WAV 录音。") from exc
        if type(rate) is not int or not 8000<=rate<=96000 or not pcm or len(pcm)%2 or len(pcm)>2*rate*MAX_SECONDS:
            raise InputError("无法读取这段 WAV 录音。")
        with self.assessment_lock:
            return self.align_example(text, reference_audio.from_pcm(pcm,rate,SAMPLE_RATE))

    def import_example(self, request):
        track_id = canonical_id(request.get("id"))
        reference = self.example_key(checked_reference(request))
        name = str(request.get("name") or "").strip()[:180]
        def committed(db):
            row = db.execute("SELECT reference,track_json FROM example_tracks WHERE id=?",(track_id,)).fetchone()
            if row:
                if row[0] != reference:
                    raise InputError("范读与当前原文不匹配。")
                return json.loads(row[1])
            return None
        staged = self.data/"example-drafts"/(track_id+".wav")
        saved = self.data/"examples"/(track_id+".wav")
        with self.assessment_lock:
            with self.connect() as db:
                # Guard the short file read against a concurrent import moving this WAV.
                db.execute("BEGIN IMMEDIATE")
                if existing := committed(db):
                    return existing
                path = saved if saved.is_file() else staged
                if not path.is_file():
                    raise InputError("找不到导入的范读录音。")
                samples = read_wav(path)
            track = self.align_example(reference, samples)
            if not any(section.get("end") is not None for section in track["sections"]):
                raise InputError("范读与当前原文不匹配。")
            track.update(id=track_id,name=name)
            with self.connect() as db:
                db.execute("BEGIN IMMEDIATE")
                if existing := committed(db):
                    return existing
                if not saved.is_file():
                    os.replace(staged,saved)
                db.execute("INSERT INTO example_tracks VALUES(?,?,?,?)",(track_id,reference,
                           datetime.now(timezone.utc).isoformat(),json.dumps(track,ensure_ascii=False,allow_nan=False)))
            return track


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def send_json(self, code, obj):
        body = json.dumps(obj, ensure_ascii=False, allow_nan=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionError):
            pass

    def authorized(self):
        if not hmac.compare_digest(self.headers.get("X-Session", ""), self.server.token):
            self.send_json(403, {"error": "无效会话。"})
            return False
        return True

    def do_GET(self):
        if not self.authorized():
            return
        try:
            if self.path == "/health":
                self.send_json(200, {"engine": ENGINE, "offline": True, "device": inference().device})
            elif self.path == "/history":
                self.send_json(200, self.server.store.history())
            elif self.path.startswith("/attempt/"):
                item = self.server.store.get(canonical_id(self.path.split("/")[-1]))
                self.send_json(200 if item else 404, item or {"error": "找不到这次练习。"})
            else:
                self.send_json(404, {"error": "未知操作。"})
        except InputError as exc:
            self.send_json(400, {"error": str(exc)})
        except Exception:
            logging.exception("Reading local practice history failed")
            self.send_json(500, {"error": "无法读取本地练习记录。"})

    def do_POST(self):
        if not self.authorized():
            return
        if self.path not in ("/assess", "/rescore", "/prepare", "/demo", "/examples", "/examples/import", "/examples/timing"):
            self.send_json(404, {"error": "未知操作。"})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            limit = 32*1024*1024 if self.path == "/examples/timing" else 131072
            if not 0 < length <= limit:
                raise InputError("请求长度无效。")
            request = json.loads(self.rfile.read(length))
            if not isinstance(request, dict):
                raise InputError("请求格式无效。")
            if self.path == "/demo":
                voice = request.get("voice")
                if not isinstance(voice, str) or voice not in demo_voices.VOICES:
                    raise InputError("请选择列表中的英音示范声音。")
                phrase = checked_reference(request, allow_numbers=True)
                if any(len(w) > 100 for w in TOKEN.findall(phrase.translate(PUNCT))):
                    raise InputError("示范文本中有超过 100 个字符的连续单词，请核对原文。")
                result = demo_voices.synthesize(voice, phrase)
            elif self.path == "/prepare":
                text = checked_reference(request, allow_numbers=bool(request.get("exampleReference")))
                result = prepare_reference(text)
                result["examples"] = self.server.store.examples({"reference":request.get("exampleReference") or text})
            elif self.path == "/rescore":
                result = self.server.store.rescore(request)
            elif self.path == "/examples":
                result = self.server.store.examples(request)
            elif self.path == "/examples/import":
                result = self.server.store.import_example(request)
            elif self.path == "/examples/timing":
                result = self.server.store.example_timing(request)
            else:
                result = self.server.store.assess(request)
            self.send_json(200, result)
        except (InputError, json.JSONDecodeError) as exc:
            self.send_json(400, {"error": str(exc)})
        except Exception as exc:
            logging.exception("Local request failed: %s", self.path)
            message = {"/demo": "本地示范语音未能生成。", "/prepare": "未能准备文本读音。",
                       "/assess": "本地评测未完成，录音已保留。",
                       "/rescore": "英式重评未完成，可稍后重试。"}.get(self.path,"范读时间定位未完成，请稍后重试。")
            self.send_json(500, {"error": f"{message}{type(exc).__name__}: {str(exc)[:300]}"})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", type=Path, required=True)
    args = parser.parse_args()
    data = args.data.resolve()
    data.mkdir(parents=True, exist_ok=True)
    configure_runtime(data)
    logging.basicConfig(filename=data / "engine.log", encoding="utf-8", level=logging.WARNING,
                        format="%(asctime)s %(levelname)s %(message)s")
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    server.store = PracticeStore(data)
    server.token = os.environ.pop("READALOUD_SESSION_TOKEN")
    # A blocking CRT read on Windows can hold a file-descriptor lock while a
    # numerical extension DLL initializes. Observe the parent pipe without reading it.
    def watch_parent():
        import ctypes
        import msvcrt
        from ctypes import wintypes
        pipe = wintypes.HANDLE(msvcrt.get_osfhandle(sys.stdin.fileno()))
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        if kernel.GetFileType(pipe) != 3:
            return
        while kernel.PeekNamedPipe(pipe, None, 0, None, None, None):
            time.sleep(.25)
        server.shutdown()
    threading.Thread(target=watch_parent, daemon=True).start()
    print(json.dumps({"port": server.server_port}), flush=True)
    try:
        inference()
        server.serve_forever()
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
