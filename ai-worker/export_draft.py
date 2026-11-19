"""Export an editable Jianying draft from LiveClipper's portable manifest.

v2 在 v1（V1 主轨 + 可编辑转场）之上，把片段滤镜、运镜（入场动画）、
淡入淡出与文字样式（字体/字号/颜色/描边/位置）写成草稿里可编辑的素材；
manifest 缺少这些字段时退回 v1 行为（转场可编辑、字幕走 SRT 导入）。
"""
import argparse
import json
import os
import shutil
import sys

# 与渲染器滤镜近似对应的剪映滤镜；kind 由 C# 端 BuildFilters 给出。
FILTER_NAMES = {"warm": "暖黄", "cool": "冷调CCD", "mono": "高清黑白", "vivid": "鲜艳增色"}
# 运镜映射成入场动画（剪映没有真正的“推镜”基础运镜，用缩放动画近似）。
MOTION_INTRO = {"push": "放大", "shake": "轻微抖动"}
# 渲染器字体名 → 剪映 FontType 成员；找不到时退回经典雅黑。
FONT_ALIASES = {
    "Microsoft YaHei": "经典雅黑",
    "微软雅黑": "经典雅黑",
    "PingFang SC": "经典雅黑",
    "Source Han Sans": "SourceHanSansCN_Normal",
}
SUBTITLE_Y = {"bottom": -0.72, "middle": 0.0, "top": 0.72}


def _rgb(value, fallback=(1.0, 1.0, 1.0)):
    if not isinstance(value, str):
        return fallback
    text = value.strip().lstrip("#")
    if len(text) != 6:
        return fallback
    try:
        return tuple(int(text[index:index + 2], 16) / 255.0 for index in (0, 2, 4))
    except ValueError:
        return fallback


def _number(value, fallback=0.0):
    try:
        return float(value)
    except (TypeError, ValueError):
        return fallback


def _text_style(pjd, style):
    style = style if isinstance(style, dict) else {}
    size = min(40.0, max(1.0, _number(style.get("size"), 8.0)))
    outline = max(0.0, _number(style.get("outline")))
    border = pjd.TextBorder(width=min(100.0, outline * 26.667)) if outline > 0 else None
    return pjd.TextStyle(size=size, color=_rgb(style.get("color")), align=1), border


def _font(pjd, style):
    name = (style if isinstance(style, dict) else {}).get("font")
    if not name:
        return pjd.FontType.经典雅黑
    member = FONT_ALIASES.get(str(name), str(name))
    return pjd.FontType.__members__.get(member, pjd.FontType.经典雅黑)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    args = parser.parse_args()
    try:
        import pyJianYingDraft as pjd
    except ImportError as exc:
        raise RuntimeError("请在设置所选 Python 环境安装 pyJianYingDraft==0.3.0") from exc
    root = os.path.dirname(os.path.abspath(args.manifest))
    with open(args.manifest, encoding="utf-8-sig") as stream:
        manifest = json.load(stream)
    draft_name = manifest.get("draftName") or "JianyingDraft"
    folder = pjd.DraftFolder(root)
    script = folder.create_draft(draft_name, manifest["width"], manifest["height"],
                                 round(manifest["fps"]), allow_replace=True)
    project = manifest["project"]
    volume = project.get("VoiceVolume", 1)
    timeline = [item for item in (project.get("Timeline") or [])
                if int(item.get("Track", 1)) == 1]
    transition_by_item = {}
    for transition in project.get("Transitions") or []:
        item_id = transition.get("ItemId")
        if item_id:
            transition_by_item[item_id] = transition
    transition_names = {"cross-dissolve": "叠化", "dip-to-black": "闪黑", "dip-to-white": "闪白"}
    script.append_track(pjd.TrackSpec(pjd.TrackType.video, name="画面"))
    # 渲染成片里相邻片段在转场处是重叠的（xfade），草稿里两段必须相邻：
    # 裁掉前一片段尾部与后一片段等长的重叠量，并在前一片段上挂一个可编辑转场。
    parts = [part for part in manifest["parts"]
             if str(part.get("file", "")).startswith("part-")]
    filter_count = fade_count = animation_count = 0
    for position, part in enumerate(parts):
        next_part = parts[position + 1] if position + 1 < len(parts) else None
        overlap = 0.0
        if next_part is not None:
            overlap = max(0.0, part["start"] + part["duration"] - next_part["start"])
        duration = part["duration"] - overlap
        if duration <= 1e-6:
            continue
        material = pjd.VideoMaterial(os.path.join(root, part["file"]))
        script.add_material(material)
        segment = pjd.VideoSegment(material, pjd.trange(round(part["start"] * 1e6),
                                  round(duration * 1e6)), volume=volume)
        if next_part is not None and overlap > 1e-6:
            next_item = timeline[position + 1] if position + 1 < len(timeline) else {}
            transition = transition_by_item.get(next_item.get("Id")) or {}
            name = transition_names.get(transition.get("Kind"), "叠化")
            segment.add_transition(pjd.TransitionType.__members__[name],
                                   duration=round(overlap * 1e6))
        for entry in part.get("filters") or []:
            entry = entry if isinstance(entry, dict) else {}
            name = FILTER_NAMES.get(str(entry.get("kind") or ""))
            if name and name in pjd.FilterType.__members__:
                segment.add_filter(pjd.FilterType.__members__[name],
                                   intensity=min(100.0, max(0.0, _number(entry.get("intensity"), 100.0))))
                filter_count += 1
        animation = part.get("animation") if isinstance(part.get("animation"), dict) else {}
        fade_in = max(0.0, _number(animation.get("fadeIn")))
        fade_out = max(0.0, _number(animation.get("fadeOut")))
        if fade_in > 1e-6 or fade_out > 1e-6:
            segment.add_fade(round(fade_in * 1e6), round(fade_out * 1e6))
            fade_count += 1
        intro = MOTION_INTRO.get(str(animation.get("motion") or "none"))
        if intro and intro in pjd.IntroType.__members__:
            segment.add_animation(pjd.IntroType.__members__[intro],
                                  duration=round(min(0.6, duration) * 1e6))
            animation_count += 1
        script.add_segment(segment, track="画面")
    # v2：文字（字幕句 + 片内标题）带样式写成可编辑文字素材；字幕/标题分两条轨道，
    # 避免同一轨道的时间段重叠；没有 texts 时退回 SRT 导入。
    texts = manifest.get("texts") or []
    total_duration = round(_number(manifest.get("duration")) * 1e6)
    text_track_ends = {}
    text_count = 0
    for entry in texts:
        if not isinstance(entry, dict):
            continue
        text = str(entry.get("text") or "").strip()
        begin = round(max(0.0, _number(entry.get("start"))) * 1e6)
        length = min(round(max(0.0, _number(entry.get("duration"))) * 1e6), max(0, total_duration - begin))
        track_name = "标题" if str(entry.get("source") or "subtitle") == "title" else "字幕"
        if track_name not in text_track_ends:
            script.append_track(pjd.TrackSpec(pjd.TrackType.text, name=track_name))
            text_track_ends[track_name] = 0
        begin = max(begin, text_track_ends[track_name])
        length = min(length, max(0, total_duration - begin))
        if not text or length <= 0:
            continue
        style = entry.get("style") if isinstance(entry.get("style"), dict) else {}
        text_style, border = _text_style(pjd, style)
        transform_y = _number(entry.get("transformY"), SUBTITLE_Y.get(style.get("position"), -0.72))
        segment = pjd.TextSegment(text, pjd.trange(begin, length), font=_font(pjd, style),
                                  style=text_style, border=border,
                                  clip_settings=pjd.ClipSettings(
                                      transform_x=_number(entry.get("transformX")),
                                      transform_y=transform_y))
        script.add_segment(segment, track=track_name)
        text_track_ends[track_name] = begin + length
        text_count += 1
    if text_count == 0:
        srt = os.path.join(root, "subtitles.srt")
        if os.path.getsize(srt) > 0:
            script.import_srt(srt, "字幕")
    assets = manifest["assets"]
    music = project.get("MusicAssetId")
    if music and music in assets:
        script.append_track(pjd.TrackSpec(pjd.TrackType.audio, name="配乐"))
        material = pjd.AudioMaterial(os.path.join(root, assets[music]))
        script.add_material(material)
        total = round(manifest["duration"] * 1e6)
        offset = 0
        while offset < total:
            duration = min(material.duration, total - offset)
            script.add_segment(pjd.AudioSegment(material, pjd.trange(offset, duration),
                               volume=project.get("MusicVolume", .25)), track="配乐")
            offset += duration
    sounds = []
    effect = project.get("EffectAssetId")
    if effect:
        sounds.append((effect, project.get("EffectAt", 0)))
    for item, part in zip(project.get("Timeline") or [], manifest["parts"]):
        if item.get("SoundAssetId"):
            sounds.append((item["SoundAssetId"], part["start"]))
    for index, (asset_id, start) in enumerate(sounds):
        if asset_id not in assets:
            continue
        name = f"音效{index + 1}"
        script.append_track(pjd.TrackSpec(pjd.TrackType.audio, name=name))
        material = pjd.AudioMaterial(os.path.join(root, assets[asset_id]))
        script.add_material(material)
        duration = min(material.duration, round((manifest["duration"] - start) * 1e6))
        if duration > 0:
            script.add_segment(pjd.AudioSegment(material, pjd.trange(round(start * 1e6), duration),
                               volume=project.get("EffectVolume", .5)), track=name)
    script.save()
    with open(os.path.join(root, "export-origin.json"), "w", encoding="utf-8") as stream:
        json.dump({"root": root}, stream, ensure_ascii=False)
    with open(os.path.join(root, "relocate-draft.ps1"), "w", encoding="utf-8-sig") as stream:
        stream.write(r'''$ErrorActionPreference = 'Stop'
$taskOriginFile = Join-Path $PSScriptRoot 'export-origin.json'
$taskOriginalRoot = (Get-Content -LiteralPath $taskOriginFile -Raw | ConvertFrom-Json).root
$taskOldJson = (ConvertTo-Json -InputObject ([string]$taskOriginalRoot) -Compress).Trim([char]'"')
$taskNewJson = (ConvertTo-Json -InputObject ([string]$PSScriptRoot) -Compress).Trim([char]'"')
$taskDraftDirectory = Join-Path $PSScriptRoot '__DRAFT_NAME__'
foreach ($taskFile in Get-ChildItem -LiteralPath $taskDraftDirectory -Filter '*.json' -File) {
    $taskContent = [IO.File]::ReadAllText($taskFile.FullName)
    $taskContent = $taskContent.Replace($taskOldJson, $taskNewJson).Replace($taskOriginalRoot.Replace('\','/'), $PSScriptRoot.Replace('\','/'))
    $null = ConvertFrom-Json -InputObject $taskContent
    [IO.File]::WriteAllText($taskFile.FullName, $taskContent, [Text.UTF8Encoding]::new($false))
}
[IO.File]::WriteAllText($taskOriginFile, (ConvertTo-Json -InputObject @{root=$PSScriptRoot}), [Text.UTF8Encoding]::new($false))
Write-Host 'Draft material paths updated. Copy __DRAFT_NAME__ into the Jianying draft directory.'
'''.replace("__DRAFT_NAME__", draft_name.replace("'", "''")))
    cover = os.path.join(root, "cover.png")
    if os.path.exists(cover):
        shutil.copy2(cover, os.path.join(root, draft_name, "draft_cover.png"))
    print(json.dumps({"draft": os.path.join(root, draft_name),
                      "segments": len(parts),
                      "filters": filter_count,
                      "fades": fade_count,
                      "animations": animation_count,
                      "texts": text_count,
                      "textMode": "styled" if text_count else "srt"}, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        sys.exit(1)
