"""Local fixtures only; requires existing FFmpeg and pyJianYingDraft 0.3.0."""
import copy
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from export_jianying_v3 import export_manifest
from pyJianYingDraft.exceptions import SegmentOverlap


class JianyingV3Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root = Path(tempfile.mkdtemp(prefix="JianyingV3Tests-"))
        ffmpeg = Path(os.environ["LOCALAPPDATA"]) / "Microsoft/WinGet/Links/ffmpeg.exe"
        cls.video = cls.root / "源 视频.mp4"
        cls.audio = cls.root / "独立 音频.wav"
        cls.mka = cls.root / "stream-copy.mka"
        subprocess.run([str(ffmpeg), "-hide_banner", "-y", "-f", "lavfi", "-i", "color=red:s=160x90:r=25:d=4", "-f", "lavfi", "-i", "sine=frequency=500:duration=4", "-c:v", "libx264", "-ac", "2", "-c:a", "aac", str(cls.video)], check=True, capture_output=True)
        subprocess.run([str(ffmpeg), "-hide_banner", "-y", "-i", str(cls.video), "-vn", "-c:a", "pcm_s16le", str(cls.audio)], check=True, capture_output=True)

        subprocess.run([str(ffmpeg), "-hide_banner", "-y", "-copyts", "-i", str(cls.video), "-map", "0:a:0", "-vn", "-c:a", "copy", str(cls.mka)], check=True, capture_output=True)

    def manifest(self):
        final = self.root / "future-final-media"
        media = [{"id": "v", "path": str(final / self.video.name), "stagedPath": str(self.video)},
                 {"id": "a", "path": str(final / self.audio.name), "stagedPath": str(self.audio)}]
        segments = [{"id": f"s{i}", "sourceId": "v" if i <= 3 else "a", "track": i,
                     "sourceStartUs": 1234567, "durationUs": 500000, "targetStartUs": i*1000000,
                     "volume": 1.04, "videoEnabled": True, "audioEnabled": i != 2} for i in range(1, 7)]
        segments.append(dict(segments[0], id="repeat", targetStartUs=2000000))
        return {"schemaVersion": 3, "name": "独立剪映工程", "width": 160, "height": 90, "fps": 30,
                "media": media, "segments": segments,
                "captions": [{"startUs": i*1000000, "durationUs": 1500000, "text": f"字幕 {i}"} for i in range(327)]}

    def test_real03_multitrack_repeated_material_precise_time_overlapping327(self):
        manifest = self.manifest(); before = copy.deepcopy(manifest)
        summary = export_manifest(manifest, self.root / "multitrack-draft")
        content = json.loads(Path(summary["draftContent"]).read_text(encoding="utf-8"))
        self.assertEqual(manifest, before)
        self.assertEqual(summary["segmentCount"], 7); self.assertEqual(summary["captionCount"], 327)
        self.assertEqual(summary["videoMaterialCount"], 1); self.assertEqual(summary["audioMaterialCount"], 1)
        self.assertEqual(summary["captionTrackCount"], 2)
        self.assertEqual([t["name"] for t in content["tracks"][:6]], ["V1", "V2", "V3", "A1", "A2", "A3"])
        videos = content["materials"]["videos"]; audios = content["materials"]["audios"]
        self.assertEqual(videos[0]["path"], manifest["media"][0]["path"])
        self.assertEqual(audios[0]["path"], manifest["media"][1]["path"])
        self.assertFalse(Path(videos[0]["path"]).exists())
        self.assertNotIn(str(self.video), json.dumps(content, ensure_ascii=False))
        for index, track in enumerate(content["tracks"][:6]):
            segment = track["segments"][0]
            self.assertEqual(segment["source_timerange"], {"start": 1234567, "duration": 500000})
            self.assertEqual(segment["target_timerange"], {"start": (index+1)*1000000, "duration": 500000})
            self.assertEqual(segment["volume"], 0 if index == 1 else 1.04)
            self.assertEqual(segment["render_index"], index)
        self.assertEqual(len({s["material_id"] for t in content["tracks"][:3] for s in t["segments"]}), 1)
        captions = sorted((s["target_timerange"] for t in content["tracks"] if t["type"] == "text" for s in t["segments"]), key=lambda x:x["start"])
        self.assertEqual(captions, [{"start": i*1000000, "duration":1500000} for i in range(327)])
        text_segments = [s for t in content["tracks"] if t["type"] == "text" for s in t["segments"]]
        self.assertEqual(len(text_segments), 327)
        for segment in text_segments:
            self.assertEqual(segment["clip"]["transform"], {"x": 0.0, "y": -0.8})
            self.assertEqual(segment["clip"]["scale"], {"x": 1.0, "y": 1.0})
        for track in content["tracks"][:3]:
            for segment in track["segments"]:
                self.assertEqual(segment["clip"]["transform"], {"x": 0.0, "y": 0.0})
        self.assertEqual(content["fps"], 30)
        meta = json.loads((self.root / "multitrack-draft/draft_meta_info.json").read_text(encoding="utf-8"))
        self.assertEqual(content["duration"], 327_500_000)
        self.assertEqual(meta["tm_duration"], content["duration"])

    def test_stream_copy_mka_preserves_nonzero_source_and_final_audio_path(self):
        manifest = self.manifest()
        manifest["media"][0].update(audioPath=str(self.root / "final-audio.mka"), audioStagedPath=str(self.mka))
        for segment in manifest["segments"]:
            segment["sourceId"] = "v"
        summary = export_manifest(manifest, self.root / "mka-draft")
        content = json.loads(Path(summary["draftContent"]).read_text(encoding="utf-8"))
        self.assertEqual(len(content["materials"]["audios"]), 1)
        self.assertEqual(content["materials"]["audios"][0]["path"], manifest["media"][0]["audioPath"])
        for track in content["tracks"][3:6]:
            self.assertEqual(track["segments"][0]["source_timerange"], {"start":1234567,"duration":500000})
        meta = json.loads((self.root / "mka-draft/draft_meta_info.json").read_text(encoding="utf-8"))
        self.assertEqual(meta["draft_name"], manifest["name"])

    def test_final_manifest_null_staged_path_uses_existing_final_media(self):
        manifest = self.manifest()
        for medium in manifest["media"]:
            medium["path"] = medium["stagedPath"]
            medium["stagedPath"] = None
        summary = export_manifest(manifest, self.root / "final-manifest")
        self.assertEqual(summary["segmentCount"], 7)

    def test_new_drafts_have_unique_identity_shared_by_content_and_metadata(self):
        ids = []
        for name in ("identity-one", "identity-two"):
            summary = export_manifest(self.manifest(), self.root / name)
            content = json.loads(Path(summary["draftContent"]).read_text(encoding="utf-8"))
            meta = json.loads((self.root / name / "draft_meta_info.json").read_text(encoding="utf-8"))
            self.assertEqual(meta["draft_id"], content["id"])
            self.assertEqual(summary["draftId"], content["id"])
            self.assertEqual(summary["contentVersion"], content["version"])
            self.assertEqual(summary["contentVersion"], 360000)
            ids.append(content["id"])
        self.assertNotEqual(ids[0], ids[1])

    def test_hidden_rejected_before_directory_exists(self):
        manifest = self.manifest(); manifest["segments"][0]["videoEnabled"] = False
        output = self.root / "hidden"
        with self.assertRaisesRegex(ValueError, "Hidden or disabled"):
            export_manifest(manifest, output)
        self.assertFalse(output.exists())

    def test_video_on_audio_track_rejected_not_silently_skipped(self):
        manifest = self.manifest(); manifest["segments"][3]["sourceId"] = "v"
        output = self.root / "video-as-audio"
        with self.assertRaises(ValueError):
            export_manifest(manifest, output)
        self.assertFalse(output.exists())

    def test_overlapping_media_track_rejected_without_publishing(self):
        manifest = self.manifest(); manifest["segments"][-1]["targetStartUs"] = 1100000
        output = self.root / "overlap"
        with self.assertRaises(SegmentOverlap):
            export_manifest(manifest, output)
        self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
