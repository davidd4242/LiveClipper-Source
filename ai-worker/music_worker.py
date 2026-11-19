#!/usr/bin/env python3
"""LiveClipper music worker.

This file intentionally keeps the model adapter small:
- `builtin` uses only the Python standard library and always works;
- `musicgen` downloads/loads facebook/musicgen-small on explicit user consent.
"""
from __future__ import annotations

import argparse
import array
import hashlib
import importlib.util
import json
import math
import os
import random
import subprocess
import sys
import traceback
import wave
from pathlib import Path


def emit(progress: float, detail: str) -> None:
    print(f"PROGRESS {max(0.0, min(1.0, float(progress))):.4f} {detail}", flush=True)


def stable_seed(*parts: str) -> int:
    value = "\n".join(parts)
    return int(hashlib.sha256(value.encode("utf-8")).hexdigest()[:16], 16)


def note_frequency(midi: int) -> float:
    return 440.0 * (2.0 ** ((midi - 69) / 12.0))


def write_wav(path: Path, samples, sample_rate: int, channels: int = 1) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if hasattr(samples, "astype"):
        values = samples.astype("float32", copy=False).reshape(-1)
        values = values.clip(-1.0, 1.0)
        pcm = (values * 32767.0).astype("<i2").tobytes()
    else:
        pcm = array.array("h", (max(-32768, min(32767, int(round(float(x) * 32767.0)))) for x in samples)).tobytes()
    with wave.open(str(path), "wb") as target:
        target.setnchannels(channels)
        target.setsampwidth(2)
        target.setframerate(sample_rate)
        target.writeframes(pcm)


def write_musicgen_wav(path: Path, channels, sample_rate: int, duration: int) -> None:
    """MusicGen returns planar channels; PCM WAV stores interleaved frames."""
    import numpy as np

    frame_count = duration * sample_rate
    samples = channels[:, :frame_count]
    if samples.shape[1] < frame_count:
        samples = np.pad(samples, ((0, 0), (0, frame_count - samples.shape[1])))
    peak = float(np.max(np.abs(samples)))
    if peak > 0.92:
        samples = samples * (0.92 / peak)
    write_wav(path, samples.T, sample_rate, channels=samples.shape[0])


def read_wav_samples(path: Path) -> tuple[list[float], int]:
    """读取 16-bit PCM WAV（多声道取第一声道），返回 (样本, 采样率)。"""
    with wave.open(str(path), "rb") as source:
        channels = source.getnchannels()
        width = source.getsampwidth()
        rate = source.getframerate()
        frames = source.readframes(source.getnframes())
    if width != 2:
        return [], rate
    values = array.array("h")
    values.frombytes(frames)
    if sys.byteorder != "little":
        values.byteswap()
    if channels > 1:
        values = array.array("h", values[0::channels])
    return [max(-1.0, min(1.0, value / 32768.0)) for value in values], rate


def estimate_bpm_beats(samples: list[float], sample_rate: int, frame_rate: int = 100) -> tuple[float | None, list[float], float]:
    """能量包络 + 归一化自相关的纯 Python 节拍估计；不可靠时返回 (None, [], 0)。"""
    if sample_rate <= 0 or len(samples) < sample_rate:
        return None, [], 0.0
    frame_size = max(1, sample_rate // frame_rate)
    count = len(samples) // frame_size
    if count < 8:
        return None, [], 0.0
    energy = []
    for index in range(count):
        chunk = samples[index * frame_size:(index + 1) * frame_size]
        energy.append(math.sqrt(sum(value * value for value in chunk) / frame_size))
    peak = max(energy)
    if peak < 1e-5:
        return None, [], 0.0
    energy = [value / peak for value in energy]
    onset = [0.0] + [max(0.0, energy[index] - energy[index - 1]) for index in range(1, count)]
    onset_peak = max(onset)
    if onset_peak <= 0:
        return None, [], 0.0
    onset = [value / onset_peak for value in onset]
    min_lag = max(2, int(round(60 * frame_rate / 220)))
    max_lag = min(count - 2, int(round(60 * frame_rate / 55)))
    if max_lag <= min_lag:
        return None, [], 0.0
    best_score = 0.0
    best_lag = 0
    for lag in range(min_lag, max_lag + 1):
        dot = energy_a = energy_b = 0.0
        for index in range(count - lag):
            dot += onset[index] * onset[index + lag]
            energy_a += onset[index] * onset[index]
            energy_b += onset[index + lag] * onset[index + lag]
        if dot <= 0:
            continue
        score = dot / math.sqrt(energy_a * energy_b + 1e-12)
        if score > best_score:
            best_score = score
            best_lag = lag
    if best_lag < min_lag or best_score < 0.15:
        return None, [], 0.0
    # 自相关常在二倍周期（半速）取到最大值；若半周期的相关性也足够高，优先更快的节拍。
    if best_lag >= min_lag * 2:
        half = best_lag // 2
        dot = energy_a = energy_b = 0.0
        for index in range(count - half):
            dot += onset[index] * onset[index + half]
            energy_a += onset[index] * onset[index]
            energy_b += onset[index + half] * onset[index + half]
        half_score = dot / math.sqrt(energy_a * energy_b + 1e-12) if dot > 0 else 0.0
        if half_score >= best_score * 0.75:
            best_lag, best_score = half, half_score
    bpm = 60.0 * frame_rate / best_lag
    duration = len(samples) / sample_rate
    beat = 60.0 / bpm
    search = min(count - 1, best_lag * 2)
    phase = max(range(search + 1), key=lambda index: onset[index])
    beats: list[float] = []
    time = max(0.0, phase / frame_rate - beat / 2)
    while time < duration - 1e-6:
        beats.append(round(time, 4))
        time += beat
    return round(bpm, 2), beats, round(best_score, 3)


def emit_result(payload: dict) -> None:
    """给 C# 侧解析的结构化分析结果（单行 JSON，失败时 bpm=null 降级）。"""
    print("RESULT " + json.dumps(payload, ensure_ascii=False), flush=True)


def emit_analysis(path: Path, method: str, confidence: float | None = None) -> None:
    samples, rate = read_wav_samples(path)
    bpm, beats, score = estimate_bpm_beats(samples, rate)
    emit_result({
        "bpm": bpm,
        "beats": beats,
        "method": method,
        "confidence": round(confidence if confidence is not None else score, 3),
        "durationSeconds": round(len(samples) / rate, 3) if rate else 0,
        "degraded": bpm is None,
    })


def install_packages(with_runtime: bool) -> None:
    required = [("huggingface_hub", "huggingface_hub")]
    if with_runtime:
        required.extend([("numpy", "numpy"), ("torch", "torch"), ("transformers", "transformers")])
    missing = [package for module, package in required if importlib.util.find_spec(module) is None]
    if not missing:
        return
    emit(0.02, "正在安装 Python 运行依赖，首次可能较慢…")
    env = os.environ.copy()
    env["PIP_DISABLE_PIP_VERSION_CHECK"] = "1"
    subprocess.check_call(
        [sys.executable, "-m", "pip", "install", "--upgrade", "--disable-pip-version-check", *missing],
        env=env,
    )


def prepare_model(args: argparse.Namespace) -> int:
    target = Path(args.target).expanduser().resolve()
    target.mkdir(parents=True, exist_ok=True)
    emit(0.01, "正在检查下载组件…")
    install_packages(bool(args.with_runtime))
    emit(0.18, "正在连接模型下载源…")
    from huggingface_hub import snapshot_download

    kwargs = {"repo_id": args.model, "local_dir": str(target)}
    try:
        snapshot_download(**kwargs, local_dir_use_symlinks=False)
    except TypeError:
        snapshot_download(**kwargs)
    emit(0.94, "正在写入模型清单…")
    marker = {
        "model": args.model,
        "target": str(target),
        "installed": True,
        "runtime": bool(args.with_runtime),
    }
    (target / ".liveclipper-model.json").write_text(json.dumps(marker, ensure_ascii=False, indent=2), encoding="utf-8")
    emit(1.0, "模型下载完成")
    return 0


def preset_for(genre: str, style: str) -> dict:
    text = f"{genre} {style}".lower()
    presets = [
        (("电子", "electro", "edm", " synth"), {"tempo": 128, "minor": True, "progression": [0, 3, 5, 4], "drive": 1.15}),
        (("电影", "史诗", "epic", "cinematic", "管弦"), {"tempo": 88, "minor": True, "progression": [0, 5, 3, 4], "drive": 1.0}),
        (("舒缓", "治愈", "ambient", "chill", "轻音乐"), {"tempo": 76, "minor": False, "progression": [0, 4, 5, 3], "drive": 0.72}),
        (("摇滚", "rock", "metal"), {"tempo": 126, "minor": True, "progression": [0, 3, 4, 5], "drive": 1.2}),
        (("爵士", "jazz", "swing"), {"tempo": 104, "minor": False, "progression": [0, 5, 1, 4], "drive": 0.9}),
        (("中国", "国风", "古风", "chinese", "oriental"), {"tempo": 82, "minor": False, "progression": [0, 2, 5, 3], "pentatonic": True, "drive": 0.88}),
        (("轻快", "欢快", "流行", "pop", "upbeat"), {"tempo": 124, "minor": False, "progression": [0, 5, 3, 4], "drive": 1.08}),
    ]
    for keys, value in presets:
        if any(key in text for key in keys):
            return value
    return {"tempo": 100, "minor": False, "progression": [0, 5, 3, 4], "drive": 0.95}


def add_note(buffer: list[float], sample_rate: int, start: int, duration: float, frequency: float, amplitude: float, harmonics=(1.0, 0.22, 0.08)) -> None:
    begin = max(0, int(start))
    end = min(len(buffer), int(start + duration * sample_rate))
    if end <= begin:
        return
    attack = max(0.008, min(0.08, duration * 0.18))
    release = max(0.04, min(0.35, duration * 0.48))
    length = max(1, end - begin)
    for index in range(begin, end):
        local = (index - begin) / sample_rate
        remaining = (end - index) / sample_rate
        envelope = min(1.0, local / attack, remaining / release)
        value = 0.0
        for harmonic_index, weight in enumerate(harmonics, start=1):
            value += weight * math.sin(2.0 * math.pi * frequency * harmonic_index * local)
        buffer[index] += amplitude * envelope * value


def generate_builtin(args: argparse.Namespace) -> int:
    seconds = max(5, min(180, int(args.duration)))
    sample_rate = 22050
    total = int(seconds * sample_rate)
    buffer = [0.0] * total
    seed = stable_seed(args.genre, args.style, args.content, args.theme, str(seconds))
    rng = random.Random(seed)
    preset = preset_for(args.genre, args.style)
    tempo = float(preset["tempo"])
    beat = 60.0 / tempo
    bar = beat * 4.0
    root = 45 if preset.get("minor") else 48
    if "中国" in args.genre or "国风" in args.genre or "古风" in args.genre:
        scale = [0, 2, 4, 7, 9]
    elif preset.get("minor"):
        scale = [0, 2, 3, 5, 7, 8, 10]
    else:
        scale = [0, 2, 4, 5, 7, 9, 11]
    progression = preset["progression"]
    drive = float(preset.get("drive", 1.0))
    emit(0.08, "正在编排和声与旋律…")

    bar_samples = max(1, int(bar * sample_rate))
    for bar_index, start in enumerate(range(0, total, bar_samples)):
        chord_root = root + progression[bar_index % len(progression)]
        chord_notes = [chord_root, chord_root + (3 if preset.get("minor") else 4), chord_root + 7]
        for chord_note in chord_notes:
            add_note(buffer, sample_rate, start, min(bar * 1.08, seconds), note_frequency(chord_note), 0.035 * drive, (1.0, 0.16, 0.05))
        for step in range(8):
            position = start + int(step * beat / 2.0 * sample_rate)
            melody_note = chord_root + 12 + scale[rng.randrange(len(scale))]
            if step % 3 != 0 or rng.random() < 0.5:
                add_note(buffer, sample_rate, position, beat * 0.38, note_frequency(melody_note), 0.055 * drive, (1.0, 0.25, 0.1))
        for beat_index in range(4):
            position = start + int(beat_index * beat * sample_rate)
            add_note(buffer, sample_rate, position, 0.16, note_frequency(chord_root - 12), 0.12 * drive, (1.0, 0.12))
            if beat_index in (0, 2):
                add_note(buffer, sample_rate, position, 0.14, 58.0, 0.16 * drive, (1.0, 0.05))
            else:
                add_note(buffer, sample_rate, position, 0.11, 180.0, 0.055 * drive, (1.0, 0.2))
            for half in range(2):
                hat = position + int(half * beat * 0.5 * sample_rate)
                add_note(buffer, sample_rate, hat, 0.045, 7800.0 + rng.randrange(-500, 500), 0.018 * drive, (1.0,))

    peak = max(1e-6, max(abs(value) for value in buffer))
    scale_factor = 0.84 / peak
    fade = min(int(sample_rate * 1.2), total // 4)
    for index in range(total):
        fade_in = min(1.0, index / max(1, fade))
        fade_out = min(1.0, (total - index - 1) / max(1, fade))
        buffer[index] *= scale_factor * fade_in * fade_out
    emit(0.82, "正在写入 WAV 音频…")
    write_wav(Path(args.output), buffer, sample_rate)
    beat_grid = [round(index * beat, 4) for index in range(int(seconds / beat) + 1) if index * beat < seconds]
    emit_result({
        "bpm": round(tempo, 2),
        "beats": beat_grid,
        "method": "builtin-preset",
        "confidence": 1.0,
        "durationSeconds": float(seconds),
        "degraded": False,
    })
    emit(1.0, "音乐生成完成")
    return 0


def translated_prompt(genre: str, style: str, content: str, theme: str) -> str:
    replacements = {
        "流行": "pop", "电子": "electronic", "摇滚": "rock", "爵士": "jazz", "古典": "classical",
        "轻快": "upbeat", "舒缓": "calm", "治愈": "healing", "史诗": "epic", "电影": "cinematic",
        "中国风": "Chinese traditional", "国风": "Chinese traditional", "古风": "ancient Chinese",
        "赛博": "cyberpunk", "温暖": "warm", "悲伤": "sad", "搞笑": "quirky", "紧张": "tense",
        "开播": "streaming opening", "直播": "live stream", "剪辑": "video editing", "高光": "highlight reel",
    }
    parts = []
    for label, value in (("genre", genre), ("style", style), ("content", content), ("theme", theme)):
        translated = value
        for chinese, english in replacements.items():
            translated = translated.replace(chinese, f"{english} {chinese}")
        parts.append(f"{label}: {translated}")
    return "; ".join(parts) + ". Instrumental background music, no vocals, clean mix, loopable, high quality."


def generate_musicgen(args: argparse.Namespace) -> int:
    model_dir = Path(args.model_dir).expanduser().resolve()
    if not model_dir.exists():
        raise FileNotFoundError("本地 MusicGen 模型目录不存在，请先在创建音乐窗口下载模型")
    duration = max(5, min(30, int(args.duration)))
    emit(0.05, "正在加载本地 AI 模型…")
    import torch
    from transformers import AutoProcessor, MusicgenForConditionalGeneration

    device = "cuda" if torch.cuda.is_available() else "cpu"
    dtype = torch.float16 if device == "cuda" else torch.float32
    processor = AutoProcessor.from_pretrained(str(model_dir), local_files_only=True)
    model = MusicgenForConditionalGeneration.from_pretrained(str(model_dir), local_files_only=True, torch_dtype=dtype, use_safetensors=True if args.external_safetensors else None).to(device)
    model.eval()
    prompt = translated_prompt(args.genre, args.style, args.content, args.theme)
    inputs = processor(text=[prompt], padding=True, return_tensors="pt")
    inputs = {key: value.to(device) if hasattr(value, "to") else value for key, value in inputs.items()}
    max_new_tokens = min(1500, max(64, int(duration * 50)))
    emit(0.18, "正在生成音乐片段…")
    with torch.inference_mode():
        audio = model.generate(**inputs, do_sample=True, guidance_scale=3.0, max_new_tokens=max_new_tokens)
    channels = audio[0].detach().cpu().float().numpy()
    sample_rate = int(model.config.audio_encoder.sampling_rate)
    emit(0.88, "正在写入 WAV 音频…")
    write_musicgen_wav(Path(args.output), channels, sample_rate, duration)
    emit_analysis(Path(args.output), "musicgen-autocorrelation")
    emit(1.0, "音乐生成完成")
    return 0


def analyze_audio(args: argparse.Namespace) -> int:
    path = Path(args.input)
    if not path.exists():
        raise FileNotFoundError(f"音频文件不存在：{path}")
    emit(0.1, "正在分析节拍…")
    emit_analysis(path, "python-autocorrelation")
    emit(1.0, "节拍分析完成")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="LiveClipper local music worker")
    commands = parser.add_subparsers(dest="command", required=True)

    prepare = commands.add_parser("prepare")
    prepare.add_argument("--model", required=True)
    prepare.add_argument("--target", required=True)
    prepare.add_argument("--with-runtime", action="store_true")
    prepare.set_defaults(handler=prepare_model)

    generate = commands.add_parser("generate")
    generate.add_argument("--engine", choices=["builtin", "musicgen"], required=True)
    generate.add_argument("--output", required=True)
    generate.add_argument("--duration", type=int, default=30)
    generate.add_argument("--genre", default="cinematic")
    generate.add_argument("--style", default="calm")
    generate.add_argument("--content", default="background music")
    generate.add_argument("--theme", default="live stream")
    generate.add_argument("--external-safetensors", action="store_true")
    generate.add_argument("--model-dir", default="")
    generate.set_defaults(handler=lambda args: generate_builtin(args) if args.engine == "builtin" else generate_musicgen(args))

    analyze = commands.add_parser("analyze")
    analyze.add_argument("--input", required=True)
    analyze.set_defaults(handler=analyze_audio)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    return int(args.handler(args))


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("PROGRESS 0.0 已取消", flush=True)
        raise SystemExit(130)
    except Exception as exc:
        print(f"ERROR {exc}", file=sys.stderr, flush=True)
        traceback.print_exc(file=sys.stderr)
        raise SystemExit(1)
