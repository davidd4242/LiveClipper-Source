"""Editable Jianying v3 export using the installed pyJianYingDraft 0.3 API."""
import argparse
import importlib.metadata
import json
import math
import uuid
from pathlib import Path


def export_manifest(manifest, draft_directory):
    import pyJianYingDraft as pjd

    if importlib.metadata.version("pyJianYingDraft") != "0.3.0":
        raise ValueError("Jianying v3 requires pyJianYingDraft 0.3.0")
    if manifest["schemaVersion"] != 3:
        raise ValueError("Expected manifest schemaVersion 3")
    for key in ("width", "height", "fps"):
        if type(manifest[key]) is not int or manifest[key] <= 0:
            raise ValueError(f"{key} must be a positive integer")
    output = Path(draft_directory).absolute()
    if output.exists():
        raise FileExistsError(f"Draft directory already exists: {output}")
    media = {item["id"]: item for item in manifest["media"]}
    if len(media) != len(manifest["media"]):
        raise ValueError("Duplicate media id")
    for item in media.values():
        if not Path(item["path"]).is_absolute() or not Path(item.get("stagedPath") or item["path"]).is_absolute():
            raise ValueError("Media path and stagedPath must be absolute")
    script = pjd.ScriptFile(manifest["width"], manifest["height"], manifest["fps"], False)
    tracks = {}
    for index in range(1, 7):
        kind = pjd.TrackType.video if index <= 3 else pjd.TrackType.audio
        tracks[index] = script.append_track(pjd.TrackSpec(kind, name=f"V{index}" if index <= 3 else f"A{index-3}"))
    materials = {}
    ids = set()
    for item in manifest["segments"]:
        if item["id"] in ids:
            raise ValueError("Duplicate segment id")
        ids.add(item["id"])
        track = item["track"]
        if type(track) is not int or track not in tracks:
            raise ValueError("Segment track must be 1..6")
        if item["videoEnabled"] is not True:
            raise ValueError("Hidden or disabled segments cannot be preserved by Jianying v3")
        if type(item["audioEnabled"]) is not bool:
            raise ValueError("audioEnabled must be boolean")
        for key in ("sourceStartUs", "durationUs", "targetStartUs"):
            if type(item[key]) is not int or item[key] < (1 if key == "durationUs" else 0):
                raise ValueError(f"Invalid {key}")
        volume = item["volume"]
        if not isinstance(volume, (int, float)) or not math.isfinite(volume) or volume < 0:
            raise ValueError("Invalid linear volume")
        source = media[item["sourceId"]]
        key = (item["sourceId"], "video" if track <= 3 else "audio")
        if key not in materials:
            material_type = pjd.VideoMaterial if track <= 3 else pjd.AudioMaterial
            final_path = source.get("audioPath") if track > 3 else None
            final_path = final_path or source["path"]
            staged_path = (source.get("audioStagedPath") or source.get("audioPath")) if track > 3 and source.get("audioPath") else None
            staged_path = staged_path or source.get("stagedPath") or source["path"]
            if not Path(final_path).is_absolute() or not Path(staged_path).is_absolute():
                raise ValueError("Audio material paths must be absolute")
            material = material_type(staged_path, material_name=Path(final_path).name)
            material.path = final_path
            materials[key] = material
        segment_type = pjd.VideoSegment if track <= 3 else pjd.AudioSegment
        segment = segment_type(materials[key], pjd.Timerange(item["targetStartUs"], item["durationUs"]),
                               source_timerange=pjd.Timerange(item["sourceStartUs"], item.get("sourceDurationUs", item["durationUs"])),
                               volume=volume if item["audioEnabled"] else 0.0)
        script.add_segment(segment, track=tracks[track])
    caption_ends = []
    caption_tracks = []
    captions = sorted(enumerate(manifest["captions"]), key=lambda pair: (pair[1]["startUs"], pair[0]))
    for _, caption in captions:
        start, duration = caption["startUs"], caption["durationUs"]
        if type(start) is not int or start < 0 or type(duration) is not int or duration <= 0 or not caption["text"].strip():
            raise ValueError("Invalid caption")
        index = next((i for i, end in enumerate(caption_ends) if end <= start), len(caption_ends))
        if index == len(caption_ends):
            caption_ends.append(0)
            caption_tracks.append(script.append_track(pjd.TrackSpec(pjd.TrackType.text, name=f"Captions {index+1}")))
        # ClipSettings uses half-canvas coordinates; Jianying's subtitle baseline
        # is y=-0.8 (bottom center), not TextSegment's title-like origin (0, 0).
        script.add_segment(pjd.TextSegment(caption["text"], pjd.Timerange(start, duration),
                           clip_settings=pjd.ClipSettings(transform_x=0.0, transform_y=-0.8)),
                           track=caption_tracks[index])
        caption_ends[index] = start + duration
    # Build all materials and segments before publishing the new draft directory.
    script.content["id"] = str(uuid.uuid4()).upper()
    serialized = script.dumps()
    content = json.loads(serialized)
    output.parent.mkdir(parents=True, exist_ok=True)
    created = pjd.DraftFolder(str(output.parent)).create_draft(output.name, manifest["width"], manifest["height"], manifest["fps"], maintrack_adsorb=False)
    Path(created.save_path).write_text(serialized, encoding="utf-8")
    meta_path = output / "draft_meta_info.json"
    meta = json.loads(meta_path.read_text(encoding="utf-8"))
    if "draft_name" not in meta:
        raise ValueError("Installed draft metadata template lacks draft_name")
    meta["draft_name"] = manifest["name"]
    meta["draft_id"] = content["id"]
    meta["tm_duration"] = content["duration"]
    meta_path.write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
    return {"schemaVersion": 3, "contentVersion": content["version"], "draftId": content["id"], "draftDirectory": str(output), "draftContent": str(Path(created.save_path)),
            "segmentCount": len(ids), "captionCount": len(captions), "captionTrackCount": len(caption_tracks),
            "videoMaterialCount": len(script.materials.videos), "audioMaterialCount": len(script.materials.audios)}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--draft-directory", required=True)
    args = parser.parse_args()
    with open(args.manifest, encoding="utf-8-sig") as stream:
        manifest = json.load(stream)
    print(json.dumps(export_manifest(manifest, args.draft_directory), ensure_ascii=False))


if __name__ == "__main__":
    main()
