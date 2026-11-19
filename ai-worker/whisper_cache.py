"""Read-only inspection of a faster-whisper model in the Hugging Face cache.

Plans nothing destructive: it only reads metadata via scan_cache_dir and asks
HFCacheInfo.delete_revisions for a *plan* (DeleteCacheStrategy). It never calls
strategy.execute(), never downloads, never loads a model, never writes refs.

Usage:
    python whisper_cache.py --model small --revision <40-hex> [--cache-dir DIR]

Output: one JSON line on stdout, type "whisper-cache-info".
Exit codes: 0 = answer produced (present / missing / unavailable),
            2 = unsupported model or bad revision, 1 = unexpected I/O or library error
            (JSON error line on stderr; never reported as "missing").
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

from faster_whisper.utils import _MODELS  # alias -> repo_id, e.g. "small" -> "Systran/faster-whisper-small"
from huggingface_hub.errors import CacheNotFound
from huggingface_hub.utils import scan_cache_dir

_HEX40 = re.compile(r"^[0-9a-f]{40}$")
_REPO_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$")


def _fail(code: int, message: str) -> "None":
    print(json.dumps({"type": "whisper-cache-error", "error": message}, ensure_ascii=False), file=sys.stderr)
    sys.exit(code)


def resolve_model(model: str) -> tuple[str, str]:
    """Return (alias_or_repo_id_as_given, repo_id). Only official aliases / their repo ids."""
    if model in _MODELS:
        return model, _MODELS[model]
    # Explicit repo id: must be shaped like owner/name (no drive letters, backslashes, "..", extra slashes)
    # and must be one of faster-whisper's official targets.
    if _REPO_ID.match(model) and ".." not in model and model in set(_MODELS.values()):
        return model, model
    _fail(2, f"unsupported model: {model!r} (only faster-whisper official aliases or their official repo ids)")
    raise AssertionError  # unreachable


def _under(path: Path, root: Path) -> bool:
    try:
        return Path(path).resolve().is_relative_to(root.resolve())
    except OSError:
        return False


def build_report(model: str, revision: str, cache_dir: str | None) -> dict:
    given, repo_id = resolve_model(model)
    if not _HEX40.match(revision):
        _fail(2, "revision must be a 40-char lowercase hex commit hash")

    report: dict = {
        "type": "whisper-cache-info",
        "model": given,
        "repo_id": repo_id,
        "repo_type": "model",
        "revision": revision,
        "status": "missing",
        "integrity_verified": False,
        "managed": False,
        "cache_source": "explicit cache_dir argument" if cache_dir else "huggingface_hub default (HF_HUB_CACHE)",
        "cache_root_exists": True,
        "logical_bytes": None,
        "reclaimable_bytes": None,
        "reclaimable_note": "estimated by huggingface_hub DeleteCacheStrategy.expected_freed_size; plan only, nothing executed",
        "shared_note": (
            "logical_bytes is this revision's own file sizes and may overlap other revisions/repos; "
            "other applications using this cache are NOT detected"
        ),
        "can_uninstall": False,  # always false in this read-only helper; backend busy/auth checks not wired
        "plan_scope_verified": False,
        "reason": None,
        "warnings_total": 0,
        "warnings_affecting_target": 0,
        "note": "directory/metadata presence does not prove the model loads successfully",
    }

    try:
        info = scan_cache_dir(cache_dir)
    except CacheNotFound:
        report["cache_root_exists"] = False
        report["reason"] = "cache root does not exist"
        return report

    report["cache_root"] = str(info.cache_dir) if info.cache_dir else None
    repo_folder = "models--" + repo_id.replace("/", "--")
    report["warnings_total"] = len(info.warnings)
    related = [w for w in info.warnings if repo_folder in str(w)]
    report["warnings_affecting_target"] = len(related)

    matches = [r for r in info.repos if r.repo_type == "model" and r.repo_id == repo_id]
    if len(matches) > 1:
        report["status"] = "unavailable"
        report["reason"] = "multiple cached repos match target; metadata ambiguous"
        return report
    if not matches:
        if related:
            report["status"] = "unavailable"
            report["reason"] = "target repo cache metadata is corrupted; cannot confirm presence or absence"
        else:
            report["reason"] = "target repo not in cache"
        return report

    repo = matches[0]
    rev = next((r for r in repo.revisions if r.commit_hash == revision), None)
    if rev is None:
        if related:
            report["status"] = "unavailable"
            report["reason"] = "target repo has corrupted cache metadata; revision absence not reliable"
        else:
            report["reason"] = "repo cached but requested revision absent"
        return report

    report["status"] = "present"
    report["logical_bytes"] = rev.size_on_disk
    report["refs"] = sorted(rev.refs)

    if related:
        report["reason"] = "cache warnings affect target repo; sizes and uninstall plan are unreliable"
        report["logical_bytes"] = None
        return report

    # Cross-repo shared-reference check on the COMPLETE scan result (never a repo subset).
    sharing = [
        r.repo_id for r in info.repos
        if r is not repo and any(v.commit_hash == revision for v in r.revisions)
    ]
    if sharing:
        report["reason"] = (
            "same commit also present in other cached repo(s); shared-reference impact unknown, "
            "no uninstall plan generated"
        )
        report["shared_with_repos"] = sorted(sharing)
        return report

    # Plan computed from the full, unreduced scan info; only planned, never executed.
    strategy = info.delete_revisions(revision)
    paths = [*strategy.repos, *strategy.snapshots, *strategy.refs, *strategy.blobs, *strategy.files]
    if not paths:
        report["reason"] = "delete plan is empty; plan scope unverified, space unknown"
    elif not all(_under(p, repo.repo_path) for p in paths):
        report["reason"] = "delete plan touches paths outside the target repo directory; scope unclear, space unknown"
    else:
        report["plan_scope_verified"] = True
        report["reclaimable_bytes"] = int(strategy.expected_freed_size)
        report["plan_scope"] = {
            "whole_repo": repo.repo_path in strategy.repos,
            "snapshots": len(strategy.snapshots),
            "refs": len(strategy.refs),
            "blobs": len(strategy.blobs),
        }
        report["reason"] = (
            "plan scope verified within target repo only; can_uninstall stays false: "
            "helper is unmanaged, integrity unverified, no usage lease/user confirmation (service-layer busy/authorization check not wired)"
        )
    return report


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Read-only faster-whisper HF cache inspector")
    ap.add_argument("--model", required=True)
    ap.add_argument("--revision", required=True)
    ap.add_argument("--cache-dir", default=None)
    args = ap.parse_args(argv)
    try:
        report = build_report(args.model, args.revision, args.cache_dir)
    except SystemExit:
        raise
    except Exception as exc:  # surfaced as failure, never as a fake "missing"
        _fail(1, f"{type(exc).__name__}: {exc}")
        return 1
    print(json.dumps(report, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
