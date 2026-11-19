export const EDITOR_NEXT_NOTICE_LABELS: Readonly<Record<string, string>> = Object.freeze({
  active_sequence_only: '当前只保存当前序列；暂不支持多序列或嵌套序列的编辑保存',
  sequence_catalog_included: '工程包含序列目录；非当前序列保留原值，不代表支持多序列编辑',
  host_contract_v2_incomplete: '部分能力尚在接入；未支持的编辑会明确提示，不会静默丢弃',
  engine_rehosted: '编辑器已重新连接宿主',
  adapter_pending: '编辑器连接尚未就绪，请稍后重试',
  captions_projected_from_fcproj: '字幕已从原工程载入',
  unknown_transition_id: '该转场尚未接入渲染',
  unsupported_effect: '该效果暂不支持渲染，工程中的原值仍保留',
  render_warning_check_failed: '暂时未能完成渲染能力检查，请核对导出结果',
  overlap_unresolved: '片段重叠尚未解决，请检查时间线',
  media_asset_retained: '素材已沿用现有绑定',
  media_unbound: '素材尚未连接，请检查源文件是否可用',
  media_missing_source_id: '素材缺少来源标识，请重新导入',
  media_required: '此片段需要可用素材',
  clip_key_allocator_unavailable: '片段标识服务尚未就绪，请重新载入',
  engine_not_ready: '编辑器尚未就绪，请稍后重试',
  engine_snapshot_missing: '暂时无法读取工程内容，请重新载入',
  engine_snapshot_revision_missing: '工程修订尚未确认，请重新载入后再保存',
  engine_save_session_inactive: '保存会话已结束，请重新载入工程',
  empty_timeline_unsupported: '当前工程不支持保存空时间线，服务器内容未改变。请在时间线中按 Ctrl+Z 撤销剪切或删除，保留至少一个片段后重试保存',
  empty_timeline_has_annotations: '清空全部事件会影响已有的独立字幕、标记或效果定位，本次保存未完成，工程内容仍保留。请先处理相关字幕、标记或效果，或在时间线中保留至少一个事件后重试保存',
  effects_archived_not_rendered: '片段效果已保存在工程中，但本次导出不会渲染',
  gpu_effects_archived_not_rendered: 'GPU 效果已保存在工程中，但本次导出不会渲染',
});

// Replace complete diagnostic tokens, including optional colon-delimited payloads.
// The untouched original remains available in TechnicalDetails; unknown codes stay readable.
export function editorNextNoticeText(message: string): string {
  return message.replace(/\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+(?:[:：][^;；\s）)]*)?/g, token => {
    const code = token.split(/[:：]/, 1)[0];
    return Object.hasOwn(EDITOR_NEXT_NOTICE_LABELS, code) ? EDITOR_NEXT_NOTICE_LABELS[code] : token;
  });
}

export function editorNextTechnicalText(messages: readonly string[]): string {
  return [...new Set(messages.filter(message => message.trim().length > 0))].join('\n');
}
