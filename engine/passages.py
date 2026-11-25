"""Segmentation and time anchors for a continuous English read-aloud recording."""
from dataclasses import dataclass
import re


TOKEN = re.compile(r"[A-Za-z0-9]+(?:'[A-Za-z0-9]+)*")
PUNCT = str.maketrans({"’": "'", "‘": "'", "ʼ": "'", "＇": "'", "–": "-", "—": "-"})
BOUNDARY = re.compile(r"""[.!?…。！？]+["'”’»)\]]*(?=\s|$)|\r\n|[\r\n]""")
TITLES = {"mr", "mrs", "ms", "dr", "prof", "st", "sr", "jr", "vs", "e.g", "i.e"}


def split_reference(reference):
    """Keep the user's punctuation. Bound very long sentences by clause or word."""
    sentences = []
    start = 0
    for boundary in BOUNDARY.finditer(reference):
        stop = boundary.end()
        if boundary.group() == "." and reference[stop:].strip():
            before = reference[start:boundary.start()]
            last = re.search(r"([A-Za-z.]+)$", before)
            name = last[1] if last else ""
            if name.lower() in TITLES or (len(name) == 1 and name.isupper() and name != "I"):
                continue
            if re.fullmatch(r"(?:[A-Za-z]\.)+[A-Za-z]", name):
                following = reference[stop:].lstrip().split(maxsplit=1)[0].strip('"“').lower()
                if following not in {"i", "we", "you", "he", "she", "it", "they", "the", "then", "this", "that"}:
                    continue
        if TOKEN.search(reference[start:stop].translate(PUNCT)):
            sentences.append(reference[start:stop].strip())
        start = stop
    if TOKEN.search(reference[start:].translate(PUNCT)):
        sentences.append(reference[start:].strip())
    units = []
    for text in sentences:
        words = list(TOKEN.finditer(text.translate(PUNCT)))
        while len(words) > 42:
            cut = next((i for i in range(36, 17, -1)
                        if re.search(r"[,;:—–]", text[words[i-1].end():words[i].start()])), 36)
            at = words[cut].start()
            units.append(text[:at].strip())
            text = text[at:]
            words = list(TOKEN.finditer(text.translate(PUNCT)))
        units.append(text)
    return units


def timed_words(samples, sample_rate):
    """Decode one continuous CTC stream, discarding overlapping window edges."""
    import torch
    from openpronounce import speech
    processor = speech._get_processor()
    model = speech._get_model_ctc()
    device = next(model.parameters()).device
    stride = model.config.inputs_to_logits_ratio
    step, context = 12 * sample_rate, 2 * sample_rate
    ids = []
    with torch.inference_mode():
        for core in range(0, len(samples), step):
            stop = min(core + step, len(samples))
            left, right = max(0, core - context), min(len(samples), stop + context)
            inputs = processor(samples[left:right], sampling_rate=sample_rate, return_tensors="pt").input_values
            predicted = model(inputs.to(device)).logits[0].argmax(dim=-1)
            begin = (core - left) // stride
            end = (stop - left) // stride if stop < len(samples) else None
            ids.extend(predicted[begin:end].cpu().tolist())
    decoded = processor.tokenizer.decode(ids, output_word_offsets=True)
    seconds = stride / sample_rate
    return [(item["word"], item["start_offset"] * seconds, item["end_offset"] * seconds)
            for item in decoded.word_offsets if item["word"].strip()]


@dataclass(frozen=True)
class Location:
    status: str
    start: float | None = None
    end: float | None = None
    transcript: str = ""


def match_words(units, heard):
    """Map individual reference occurrences to decoded word indices in order."""
    from rapidfuzz.distance import Levenshtein
    expected = [w.lower().replace("'", "") for unit in units for w in unit]
    actual = [w[0].lower().replace("'", "") for w in heard]
    matches = [None] * len(expected)
    edits = Levenshtein.opcodes(expected, actual)
    for tag, a, b, c, d in edits:
        if tag in ("equal", "replace"):
            for i, j in zip(range(a, b), range(c, d)):
                matches[i] = (j, tag == "equal")
    return matches, edits


def locate(units, heard, duration, alignment=None):
    """Map numbered reference occurrences to actual ASR words in chronological order."""
    matches, edits = alignment if alignment is not None else match_words(units, heard)
    offset = 0
    locations = []
    for unit in units:
        pairs = [m for m in matches[offset:offset+len(unit)] if m is not None]
        offset += len(unit)
        if not pairs:
            locations.append(Location("missing" if heard else "unlocated"))
            continue
        first, last = pairs[0][0], pairs[-1][0]
        transcript = " ".join(w[0] for w in heard[first:last+1])
        correct = sum(equal for _, equal in pairs)
        # Exact words are anchors, not grades. Inside a credible interval the separate
        # phone model still evaluates substitutions that the word recognizer misses.
        if correct < (1 if len(unit) <= 2 else 2) or 3 * correct < len(unit):
            locations.append(Location("unlocated", transcript=transcript))
            continue
        before = (heard[first-1][2] + heard[first][1])/2 if first else 0
        after = (heard[last][2] + heard[last+1][1])/2 if last+1 < len(heard) else duration
        start = max(0, before, heard[first][1] - .24)
        end = min(duration, after, heard[last][2] + .27)
        partial = len(unit)-len(pairs) >= 2 and 5*len(pairs) < 3*len(unit)
        locations.append(Location("partial" if partial else "located", round(start, 4), round(end, 4), transcript))
    # If a consecutive identical sentence was skipped, the recording cannot tell
    # which identical occurrence was intended. Keep both unscored.
    i = 0
    while i < len(units):
        j = i + 1
        while j < len(units) and units[j] == units[i]:
            j += 1
        if j-i > 1 and any(item.status != "located" for item in locations[i:j]):
            locations[i:j] = [Location("ambiguous") for _ in range(j-i)]
        i = j
    extra_runs = [(c, d) for tag, a, b, c, d in edits if tag == "insert"]
    return locations, extra_runs
