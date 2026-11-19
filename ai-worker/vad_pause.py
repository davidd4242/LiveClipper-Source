"""Offline source-time Silero VAD. PCM and metadata stay in memory; no ASR/download."""
import argparse
import json
import math
import os
import re
import subprocess
import sys

os.environ.setdefault("HF_HUB_OFFLINE", "1")
RATE = 16000
VERSION = "silero-v6-local-v1"


def merge(ranges):
    result = []
    for start, end in sorted(ranges):
        if end <= start:
            continue
        if result and start <= result[-1]["end"] + 1e-7:
            result[-1]["end"] = max(end, result[-1]["end"])
        else:
            result.append({"start": start, "end": end})
    return result


def align_frames(pcm, frames, start, end):
    import numpy as np
    groups, measured, gaps, timing_uncertain = [], [], [], []
    chunks, group_start, previous_end, offset = [], None, start, 0
    for timestamp, count in frames:
        samples = pcm[offset:offset + count]
        offset += count
        # Integer resampler PTS has 1/16000 time base, avoiding pts_time rounding.
        delta = timestamp - previous_end
        if delta < -.1:
            raise RuntimeError("Overlapping/backward audio timestamps exceed 100ms; refusing ambiguous VAD mapping")
        if delta < -.5 / RATE:
            # Resampling/container rounding can overlap a few samples. Keep the
            # first measured samples, trim only duplicate source-time coverage,
            # and explicitly protect that ambiguous boundary.
            timing_uncertain.append((timestamp, previous_end))
            trim = round(-delta * RATE)
            samples = samples[trim:]
            if not len(samples):
                continue
            timestamp += trim / RATE
            count = len(samples)
            delta = timestamp - previous_end
        if delta > .5 / RATE:
            gaps.append({"start": previous_end, "end": timestamp,
                         "reason": "audio-pts-gap" if delta > .1 else "small-audio-pts-gap"})
            if chunks and delta <= .1:
                chunks.append(np.zeros(round(delta * RATE), dtype=np.float32))
            elif chunks:
                groups.append((group_start, np.concatenate(chunks)))
                chunks = []
        if not chunks:
            group_start = timestamp
        chunks.append(samples)
        previous_end = timestamp + count / RATE
        measured.append((timestamp, previous_end))
    if chunks:
        groups.append((group_start, np.concatenate(chunks)))
    if previous_end < end - .5 / RATE:
        gaps.append({"start": previous_end, "end": end, "reason": "missing-audio-tail"})
    return groups, measured, gaps, timing_uncertain


def run(args):
    import numpy as np
    from faster_whisper.vad import VadOptions, get_speech_timestamps, get_vad_model
    if not (math.isfinite(args.start) and math.isfinite(args.end)
            and 0 <= args.start < args.end and args.end - args.start <= 600):
        raise ValueError("VAD requires a source window of at most 600 seconds; split long sources with context")
    probe = subprocess.run([args.ffprobe, "-v", "error", "-show_streams", "-show_format", "-of", "json", args.input], capture_output=True, check=True)
    info = json.loads(probe.stdout)
    audio = [s for s in info["streams"] if s["codec_type"] == "audio"]
    if len(audio) != 1:
        raise ValueError("VAD requires exactly one audio stream")
    origin = float(info["format"].get("start_time", 0))
    if abs(origin) > .000001:
        raise ValueError("Nonzero container origin requires explicit source mapping")
    if args.end > float(info["format"]["duration"]) + .001:
        raise ValueError("VAD window exceeds media duration")
    # No async resampler: missing samples must remain visible in timestamps.
    filters = (f"atrim=start={args.start}:end={args.end},"
               "aresample=16000,aformat=sample_fmts=flt:channel_layouts=mono,ashowinfo")
    proc = subprocess.run([args.ffmpeg, "-hide_banner", "-nostats", "-v", "info", "-copyts",
                           "-ss", str(max(0, args.start - 1)), "-to", str(args.end), "-i", args.input, "-map", "0:a:0", "-vn",
                           "-af", filters, "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"], capture_output=True)
    log = proc.stderr.decode("utf-8", errors="replace")
    if proc.returncode:
        raise RuntimeError("FFmpeg PCM decode failed: " + log[-2000:])
    # A successful process can still report corruption. Do not label undecoded audio silence.
    if re.search(r"(?:error while decoding|invalid data found|corrupt|decode error)", log, re.I):
        raise RuntimeError("Audio decode corruption reported; VAD cannot certify this window")
    frames = [(int(t) / RATE, int(n)) for t, rate, n in re.findall(
        r"Parsed_ashowinfo[^\n]*?\bpts:(-?\d+)\s+pts_time:[^\n]*?rate:(\d+)[^\n]*?nb_samples:(\d+)", log)
        if int(rate) == RATE]
    pcm = np.frombuffer(proc.stdout, dtype="<f4")
    if not frames or sum(n for _, n in frames) != len(pcm):
        raise RuntimeError("Decoded PCM and source timestamp frame counts differ")
    groups, measured, gaps, timing_uncertain = align_frames(pcm, frames, args.start, args.end)
    options = VadOptions(threshold=.35, neg_threshold=.20, min_speech_duration_ms=0,
                         min_silence_duration_ms=200, speech_pad_ms=0)
    speech, uncertain = [], timing_uncertain[:]
    model = get_vad_model()  # Bundled ONNX, never download_model/WhisperModel.
    for base, samples in groups:
        for span in get_speech_timestamps(samples, options, sampling_rate=RATE):
            speech.append((max(args.start, base + span["start"] / RATE),
                           min(args.end, base + span["end"] / RATE)))
        padded = np.pad(samples, (0, (-len(samples)) % 512))
        for index, probability in enumerate(model(padded)):
            if probability >= .15:
                uncertain.append((max(args.start, base + index * 512 / RATE),
                                  min(args.end, base + min(len(samples), (index + 1) * 512) / RATE)))
    speech = merge(speech)
    weak = []
    for span in merge(uncertain):
        parts = [(span["start"], span["end"])]
        for voice in speech:
            remaining = []
            for start, end in parts:
                if voice["end"] <= start or voice["start"] >= end:
                    remaining.append((start, end))
                else:
                    if voice["start"] > start:
                        remaining.append((start, voice["start"]))
                    if voice["end"] < end:
                        remaining.append((voice["end"], end))
            parts = remaining
        weak.extend(parts)
    return {"version": VERSION, "sourceStart": args.start, "sourceEnd": args.end,
            "sampleRate": RATE, "speech": speech, "uncertain": merge(weak),
            "gaps": gaps, "measuredRanges": merge(measured),
            "warnings": ["VAD is speech evidence, not word alignment; retain uncertain intervals and missing samples"],
            "metadata": {"threshold": .35, "negThreshold": .20, "uncertainThreshold": .15,
                         "minSpeechDurationMs": 0, "minSilenceDurationMs": 200,
                         "speechPadMs": 0, "sampleCount": len(pcm)}}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for name in ("input", "ffmpeg", "ffprobe"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--start", type=float, required=True)
    parser.add_argument("--end", type=float, required=True)
    try:
        print(json.dumps(run(parser.parse_args()), ensure_ascii=True, allow_nan=False))
    except Exception as error:
        print("Local VAD failed: " + str(error), file=sys.stderr)
        sys.exit(1)
