"""Align a model reading without creating a learner assessment."""
from itertools import groupby
from math import gcd

from passages import TOKEN, PUNCT, locate, match_words, split_reference


def from_pcm(pcm, rate, sample_rate):
    import numpy as np
    samples = np.frombuffer(pcm, dtype="<i2").astype("float32") / 32768
    if rate != sample_rate:
        from scipy.signal import resample_poly
        divisor = gcd(rate, sample_rate)
        samples = resample_poly(samples, sample_rate // divisor, rate // divisor)
    return samples


def align(reference, heard, duration, expand):
    sentences = split_reference(reference)
    tokens = [TOKEN.findall(s.translate(PUNCT)) for s in sentences]
    groups = [[expand(word) for word in sentence] for sentence in tokens]
    units = [[w.lower() for group in sentence for w in group] for sentence in groups]
    mapping = match_words(units, heard)
    locations, _ = locate(units, heard, duration, mapping)
    matches, _ = mapping
    sections, words = [], []
    offset = 0
    for index, (sentence, token_list, expanded, unit, loc) in enumerate(zip(sentences,tokens,groups,units,locations,strict=True)):
        first = len(words)
        exact = sum(1 for item in matches[offset:offset+len(unit)] if item and item[1])
        usable = loc.status == "located" and exact * 2 >= len(unit)
        for token, group in zip(token_list,expanded,strict=True):
            part = matches[offset:offset+len(group)]
            offset += len(group)
            word = {"index":len(words), "text":token}
            if usable and part and all(item and item[1] for item in part):
                a, b = part[0][0], part[-1][0]
                if b-a+1 == len(part):
                    # CTC peaks sit inside the spoken word. Extend towards the neighbouring
                    # peaks without crossing them or the aligned sentence boundary.
                    start, end = heard[a][1], heard[b][2]
                    left = (heard[a-1][2]+start)/2 if a else 0
                    right = (end+heard[b+1][1])/2 if b+1<len(heard) else duration
                    start, end = max(loc.start,left,start-.14), min(loc.end,right,end+.16)
                    if end>start:
                        word.update(start=round(start,4),end=round(end,4))
            words.append(word)
        for _, run in groupby(words[first:], key=lambda w:w["text"].casefold()):
            repeated = list(run)
            if len(repeated)>1 and any("start" not in w for w in repeated):
                for w in repeated:
                    w.pop("start",None)
                    w.pop("end",None)
        section = {"index":index,"reference":sentence,"firstWord":first,"wordCount":len(token_list)}
        if usable:
            section.update(start=loc.start,end=loc.end)
        sections.append(section)
    return {"duration":round(duration,4),"sections":sections,"words":words}
