"""Optional local ASR worker. Install: python -m pip install faster-whisper"""
import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path

from faster_whisper import WhisperModel
from faster_whisper.utils import download_model

os.environ.setdefault("HF_HUB_DISABLE_XET", "1")
os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")
OFFICIAL_ENDPOINT = "https://huggingface.co"
MIRROR_ENDPOINT = "https://hf-mirror.com"


def _download_with_retry(name: str, cache: str | None, attempts: int = 5, revision: str | None = None) -> str:
    last_error: Exception | None = None
    for attempt in range(1, attempts + 1):
        try:
            return download_model(name, cache_dir=cache, revision=revision)
        except Exception as exc:
            last_error = exc
            if attempt == attempts:
                break
            delay = min(30.0, 2.0**attempt)
            print(f"模型下载第 {attempt} 次失败：{exc}；{delay:.0f} 秒后重试。", file=sys.stderr)
            time.sleep(delay)
    assert last_error is not None
    raise last_error


def _current_endpoint() -> str:
    return os.environ.get("HF_ENDPOINT", OFFICIAL_ENDPOINT).rstrip("/")


def _emit_status(phase: str, source: str, model: str, revision: str | None) -> None:
    print(json.dumps(dict(type="model-status", phase=phase, source=source, model=model, revision=revision), ensure_ascii=False), file=sys.stderr, flush=True)


def resolve_model(name: str, revision: str | None = None, report=None) -> str:
    # report(phase, source) is only supplied by --prepare-only runs.
    report = report or (lambda phase, source: None)
    if Path(name).is_dir():
        return name
    cache = os.environ.get("HF_HUB_CACHE")
    # Reuse a complete cached model before requesting repository metadata.
    report("cache", "cache")
    try:
        cached = download_model(name, cache_dir=cache, local_files_only=True, revision=revision)
        if (Path(cached) / "model.bin").is_file() and (Path(cached) / "config.json").is_file():
            return cached
    except (OSError, ValueError):
        pass
    try:
        report("downloading", _current_endpoint())
        return _download_with_retry(name, cache, revision=revision)
    except Exception:
        # A child process reloads huggingface_hub constants with another endpoint.
        # The child is marked as a fallback run so it will not recursively retry all endpoints.
        if os.environ.get("LIVECLIPPER_HF_FALLBACK") == "1":
            raise
        current = _current_endpoint()
        endpoints = []
        if current != MIRROR_ENDPOINT:
            endpoints.append(MIRROR_ENDPOINT)
        if current != OFFICIAL_ENDPOINT:
            endpoints.append(OFFICIAL_ENDPOINT)
        for endpoint in endpoints:
            print(f"当前语音模型源下载失败，切换到 {endpoint} 继续下载。", file=sys.stderr)
            report("switching-source", endpoint)
            environment = dict(os.environ, HF_ENDPOINT=endpoint, LIVECLIPPER_HF_FALLBACK="1")
            result = subprocess.run([sys.executable, str(Path(__file__).resolve()), *sys.argv[1:]], env=environment)
            if result.returncode == 0:
                raise SystemExit(0)
        raise


def stamp(seconds: float) -> str:
    ms = max(0, round(seconds * 1000))
    hours, ms = divmod(ms, 3600000)
    minutes, ms = divmod(ms, 60000)
    secs, ms = divmod(ms, 1000)
    return f"{hours:02}:{minutes:02}:{secs:02},{ms:03}"


def transcript_row(segment, duration: float):
    # Model timestamp tokens can extend into padding beyond the decoded audio.
    end = min(segment.end, duration)
    if segment.start >= end:
        return None
    return dict(start=segment.start, end=end, text=segment.text.strip())


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("clip", nargs="?")
    parser.add_argument("output", nargs="?")
    parser.add_argument("--model", default="large-v3-turbo")
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--revision", default=None)
    parser.add_argument("--prepare-only", action="store_true")
    args = parser.parse_args()
    if not args.prepare_only and (not args.clip or not args.output):
        parser.error("clip and output are required unless --prepare-only is given")
    report = None
    source = "local"
    if args.prepare_only:
        def report(phase: str, new_source: str) -> None:
            nonlocal source
            if phase != "switching-source":
                source = new_source
            _emit_status(phase, new_source, args.model, args.revision)
    model_path = resolve_model(args.model, args.revision, report)
    if report:
        _emit_status("loading", source, args.model, args.revision)
    model = WhisperModel(model_path, device=args.device, compute_type="int8" if args.device == "cpu" else "default")
    if args.prepare_only:
        print(json.dumps(dict(type="model-prepared", model=args.model, revision=args.revision, model_path=str(model_path), ready=True), ensure_ascii=False), flush=True)
        return
    segments, info = model.transcribe(args.clip, vad_filter=True, condition_on_previous_text=False)
    rows = []
    for s in segments:
        row = transcript_row(s, info.duration)
        if row is not None:
            rows.append(row)
        # Machine-readable progress for the host process; duration may be unknown for exotic inputs.
        if info.duration:
            print(f"PROGRESS {min(0.99, s.end / info.duration):.3f}", file=sys.stderr, flush=True)
    out = Path(args.output)
    out.mkdir(parents=True, exist_ok=True)
    (out / "transcript.json").write_text(json.dumps({"language": info.language, "segments": rows}, ensure_ascii=False, indent=2), encoding="utf-8")
    srt = "\n\n".join(f"{i}\n{stamp(row['start'])} --> {stamp(row['end'])}\n{row['text']}" for i, row in enumerate(rows, 1))
    (out / "transcript.srt").write_text(srt + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
