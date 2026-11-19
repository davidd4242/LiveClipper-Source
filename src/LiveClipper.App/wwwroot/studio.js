let localVideos=[];
let audioAssets=[];
// 转译中的视频：eventId -> 最近一次任务进度（0-1）。行内进度条据此渲染，避免用户跑去任务窗口。
const localTranscribing={};
let transcribePolling=null;
let editorData=null;
let editorSegments=[];
let editorClipId='';
let editorListenUntil=0;
let editorEffectDelay=0;
let folderRequests=new Map();
let eventSearchTimer;
let editorTranscriptVersion='',editorAutoStamp='',editorAutoBusy=false;
// 有未保存改动时，自动同步不会用磁盘上的旧方案覆盖手上的编辑。
let editorDirty=false;
// 自动剪气口之前的时间线快照：为空表示当前视频没有可复原的剪气口。
let editorSilenceBackup=null,editorSilenceTrimmed=false;
const timelineDirty=()=>editorDirty;
function editorMarkDirty(){editorDirty=true}
function renderSilenceRestoreButton(){
  const button=$('editor-restore-silence');if(!button)return;
  const ready=Array.isArray(editorSilenceBackup)&&editorSilenceBackup.length>0;
  button.disabled=!ready;
  button.title=ready?`恢复到剪气口之前的时间线（${editorSilenceBackup.length} 段）`:'当前视频没有可复原的剪气口记录';
}

if(window.chrome?.webview){
  window.chrome.webview.addEventListener('message',e=>{
    const m=e.data;if(m?.type!=='folder-picked')return;
    const finish=folderRequests.get(m.requestId);if(finish){folderRequests.delete(m.requestId);finish(m.path);}
  });
}
async function pickFolder(role){
  if(!window.chrome?.webview)return window.prompt('请输入文件夹完整路径')||null;
  const requestId=crypto.randomUUID();
  return new Promise(resolve=>{folderRequests.set(requestId,resolve);window.chrome.webview.postMessage({type:'pick-folder',role,requestId});});
}
document.querySelectorAll('[data-pick-folder]').forEach(button=>button.addEventListener('click',async()=>{
  const role=button.dataset.pickFolder,path=await pickFolder(role);
  if(path)$(role==='video'?'video-folder':role==='subtitle'?'subtitle-folder':'output-folder').value=path;
}));

async function studioLoadLists(){
  try{
    [localVideos,audioAssets]=await Promise.all([api('/studio/videos'),api('/assets')]);
    renderLocalVideos();renderAudioAssets();renderEditorChoices();syncTranscribing();
    if(typeof advancedAssets==='function')advancedAssets();
    if(typeof previewRefresh==='function')previewRefresh();
    if(typeof loadAudioAttributions==='function')await loadAudioAttributions();
  }catch(error){toast(error.message)}
}
/// <summary>转译进度文案：当前百分比、实时耗时、分段信息与自动重试次数。</summary>
function transcribeDetail(info){
  const parts=[`转译中 ${Math.round(Math.max(.02,info?.progress||0)*100)}%`];
  const started=Date.parse(info?.startedAt||'');
  if(started)parts.push(`已用时 ${secs((Date.now()-started)/1000)}`);
  if(info?.message)parts.push(info.message);
  if(info?.retryCount>0)parts.push(`自动重试 ${info.retryCount} 次`);
  return parts.join(' · ');
}
function transcribeProgressHtml(clipId,eventId,info){
  const pct=Math.round(Math.max(.02,info?.progress||0)*100);
  return `<div class="local-transcribing" data-transcribing="${esc(eventId)}"><div class="local-progress-row"><div class="local-progress-track"><div style="width:${pct}%"></div></div><button type="button" class="text-link" data-local-retranscribe="${esc(clipId)}" title="中断这次转译并从头重新开始（耗时重新计时）">重新转译</button></div><small data-transcribe-detail="${esc(eventId)}">${esc(transcribeDetail(info))}</small></div>`;
}
function renderLocalVideos(){
  $('local-count').textContent=`${localVideos.length} 个`;
  const clips=snapshot?.clips||[];
  $('local-video-list').innerHTML=localVideos.map(v=>{
    const clip=clips.find(x=>x.id===v.clipId);
    const active=clip?localTranscribing[clip.eventId]:null;
    const transcribe=active!=null
      ?transcribeProgressHtml(v.clipId,clip.eventId,active)
      :clip?.srtPath?`<button type="button" class="text-link" data-transcript="${esc(v.clipId)}">文字预览</button><button type="button" class="text-link" data-local-retranscribe="${esc(v.clipId)}">重新转译</button>`:`<button type="button" class="text-link" data-local-transcribe="${esc(v.clipId)}">手动转写</button>`;
    return `<div class="clip-row"><div><strong>${esc(v.title)}</strong><small>${esc(v.path)} · ${clip?.srtPath?'字幕已匹配':'待匹配字幕'}</small></div><div class="actions"><button type="button" class="text-link" data-edit="${esc(v.clipId)}">编辑</button><button type="button" class="text-link" data-local-subtitle="${esc(v.clipId)}">匹配字幕</button><input type="file" hidden accept=".srt,.vtt,.ass,.ssa" data-subtitle-input="${esc(v.clipId)}">${transcribe}<button type="button" class="text-link" data-local-delete="${esc(v.clipId)}" title="删除这条导入记录与转写、分离缓存；原视频和导出成片不会被删除">删除记录</button></div></div>`;
  }).join('')||'<p class="empty">尚未导入本地录播</p>';
}
/// <summary>把某个 eventId 的行内进度与耗时更新到最新任务状态；元素不在就整体重渲染。</summary>
function updateTranscribeProgress(eventId,job){
  localTranscribing[eventId]={progress:Math.max(.02,job.progress||.02),startedAt:job.startedAt||localTranscribing[eventId]?.startedAt||null,message:job.message||'',retryCount:job.retryCount||0};
  const box=document.querySelector(`[data-transcribing="${CSS.escape(eventId)}"]`);
  if(!box){renderLocalVideos();return;}
  const pct=Math.round(Math.max(.02,job.progress||0)*100);
  box.querySelector('.local-progress-track>div').style.width=pct+'%';
  const detail=box.querySelector('[data-transcribe-detail]');if(detail)detail.textContent=transcribeDetail(localTranscribing[eventId]);
}
/// <summary>每秒刷新一次耗时显示，长时间转译也能看到真实用时在走。</summary>
function refreshTranscribeDetails(){
  document.querySelectorAll('[data-transcribe-detail]').forEach(el=>{
    const info=localTranscribing[el.dataset.transcribeDetail];if(info)el.textContent=transcribeDetail(info);
  });
}
/// <summary>轮询转译任务：更新行内进度与耗时；结束后刷新列表并提示结果。</summary>
function startTranscribePolling(){
  if(transcribePolling)return;
  transcribePolling=setInterval(async()=>{
    if(!Object.keys(localTranscribing).length){clearInterval(transcribePolling);transcribePolling=null;return;}
    try{
      const jobs=await api('/jobs');
      for(const eventId of Object.keys(localTranscribing)){
        const active=jobs.filter(j=>j.type==='transcribe'&&j.eventId===eventId&&(j.status==='pending'||j.status==='running')).sort((a,b)=>b.progress-a.progress)[0];
        if(active){updateTranscribeProgress(eventId,active);continue;}
        // 没有进行中的任务了：转译结束（成功或失败），刷新后按字幕是否存在判定结果。
        delete localTranscribing[eventId];
        const last=jobs.filter(j=>j.type==='transcribe'&&j.eventId===eventId).sort((a,b)=>Date.parse(b.finishedAt||b.startedAt||0)-Date.parse(a.finishedAt||a.startedAt||0))[0];
        await refresh();
        const clip=snapshot?.clips?.find(x=>x.eventId===eventId);
        const failed=clip&&!clip.srtPath;
        toast(failed?(last?.error?`转译未完成：${last.error}`:'转译未完成，请查看任务页的错误详情'):'转译完成，字幕已生成');
        if(editorClipId===clip?.id){try{await openStudioEditor(clip.id);}catch{}}
        renderLocalVideos();
      }
      refreshTranscribeDetails();
    }catch{}
  },1000);
}
/// <summary>页面加载/刷新时同步：已在后台运行的转译任务直接进入行内进度条状态。</summary>
async function syncTranscribing(){
  try{
    const jobs=await api('/jobs');
    for(const j of jobs)
      if(j.type==='transcribe'&&(j.status==='pending'||j.status==='running')){
        const previous=localTranscribing[j.eventId]||{};
        localTranscribing[j.eventId]={progress:Math.max(previous.progress||0,j.progress||.02),startedAt:j.startedAt||previous.startedAt||null,message:j.message||'',retryCount:j.retryCount||0};
      }
    if(Object.keys(localTranscribing).length){renderLocalVideos();startTranscribePolling();}
  }catch{}
}
function renderAudioAssets(){
  const cards=kind=>audioAssets.filter(x=>x.kind===kind).map(x=>`<div class="audio-card"><div><strong>${esc(x.name)}</strong><small>${esc(x.style)} · ${x.builtIn?'内置原创':x.style.startsWith('线上')?'线上下载':'用户导入'}</small><small data-local-audio-license="${esc(x.id)}"></small></div><audio controls preload="none" src="/api/assets/${encodeURIComponent(x.id)}/file"></audio><button type="button" class="quiet" data-use-audio="${esc(x.id)}" data-audio-kind="${kind}">用于剪辑</button></div>`).join('');
  $('music-list').innerHTML=cards('music')||'<p class="empty">暂无背景音乐</p>';
  $('effect-list').innerHTML=cards('effect')||'<p class="empty">暂无音效</p>';
  const currentMusic=$('editor-music').value,currentEffect=$('editor-effect').value;
  $('editor-music').innerHTML='<option value="">不添加</option>'+audioAssets.filter(x=>x.kind==='music').map(x=>`<option value="${esc(x.id)}">${esc(x.style)} · ${esc(x.name)}</option>`).join('');
  $('editor-effect').innerHTML='<option value="">不添加</option>'+audioAssets.filter(x=>x.kind==='effect').map(x=>`<option value="${esc(x.id)}">${esc(x.style)} · ${esc(x.name)}</option>`).join('');
  $('editor-music').value=currentMusic;$('editor-effect').value=currentEffect;
}
function renderEditorChoices(){
  const clips=snapshot?.clips||[];const current=$('editor-clip').value;
  const version=JSON.stringify(clips.map(c=>[c.id,localVideos.find(v=>v.clipId===c.id)?.title||c.eventId]));
  if($('editor-clip').dataset.version===version)return;
  $('editor-clip').dataset.version=version;
  $('editor-clip').innerHTML='<option value="">选择视频</option>'+clips.map(c=>{
    const local=localVideos.find(v=>v.clipId===c.id);return `<option value="${esc(c.id)}">${esc(local?.title||'高光 '+c.eventId.slice(0,8))}</option>`;
  }).join('');
  $('editor-clip').value=clips.some(x=>x.id===current)?current:'';
}
async function openStudioEditor(id){
  try{
    stopEditorAudio();if(typeof previewPauseAll==='function')previewPauseAll();else $('editor-video').pause();
    editorData=await api(`/studio/clips/${encodeURIComponent(id)}/editor`);editorClipId=id;
    editorSegments=(editorData.transcript?.segments||[]).map(x=>({...x,selected:true}));
    $('editor-clip').value=id;$('editor-empty').hidden=true;$('editor-workspace').hidden=false;show('editor');
    const player=$('editor-video');if(typeof previewResetPlayback==='function')previewResetPlayback();player.src=`/api/clips/${encodeURIComponent(id)}/video`;player.load();
    const duration=Math.max(1,Number(editorData.duration)||1);$('editor-duration').textContent=`视频长 ${duration.toFixed(1)} 秒`;
    for(const key of ['start','end']){$(`editor-${key}-range`).max=String(duration);$(`editor-${key}`).max=String(duration);}
    const plan=editorData.project||{};
    // 自动剪气口后的工程里带着剪气口前的时间线快照，重新打开编辑器也能复原。
    editorSilenceBackup=Array.isArray(plan.silenceTrimBackup)?plan.silenceTrimBackup.map(x=>({...x,id:x.id||crypto.randomUUID()})):null;
    editorSilenceTrimmed=!!plan.silenceTrimmed;
    renderSilenceRestoreButton();
    $('editor-start').value=plan.start??0;$('editor-end').value=plan.end??duration;
    $('editor-start-range').value=$('editor-start').value;$('editor-end-range').value=$('editor-end').value;
    $('editor-burn').checked=!!plan.burnSubtitles;$('editor-voice-mode').value=plan.voiceOnly?'vocals':'original';
    $('editor-voice-volume').value=plan.voiceVolume??1;$('editor-music-volume').value=plan.musicVolume??.25;$('editor-effect-volume').value=plan.effectVolume??.5;
    $('editor-effect-at').value=plan.effectAt??0;
    $('editor-music').value=plan.musicAssetId||'';$('editor-effect').value=plan.effectAssetId||'';
    if(Array.isArray(plan.selectedSegments))editorSegments.forEach((x,i)=>x.selected=plan.selectedSegments.includes(i));
    updateEditorRange();renderEditorSegments();renderStudioEvents(editorData.events||[]);renderAiAdvice(editorData.advice||null);await renderStudioRenders();
    if(typeof timelineLoadProject==='function')await timelineLoadProject(editorData,id);
    $('editor-status').textContent=editorData.stems?'人声分离已完成':'尚未分离人声';
    editorDirty=false;
    await editorSyncAutoState(true);
  }catch(error){toast(error.message)}
}
// 转录完成后自动同步：轮询字幕版本与自动方案，发现更新就重新载入编辑器。
async function editorSyncAutoState(adopt=false){
  if(!editorClipId)return null;
  let state;
  try{state=await api(`/studio/clips/${encodeURIComponent(editorClipId)}/transcript-state`)}catch{return null}
  const stamp=String(state.autoUpdatedAt||'');
  if(adopt){editorTranscriptVersion=String(state.transcriptVersion||'');editorAutoStamp=stamp;renderEditorAutoStatus(state);return state;}
  const changed=(String(state.transcriptVersion||''))!==editorTranscriptVersion||stamp!==editorAutoStamp;
  renderEditorAutoStatus(state);
  if(changed&&!editorAutoBusy){
    editorTranscriptVersion=String(state.transcriptVersion||'');editorAutoStamp=stamp;
    if(typeof timelineDirty==='function'&&timelineDirty()){
      // 用户手上有没保存的时间线改动，先不覆盖，只提示。
      toast('字幕已更新：保存或撤销当前时间线改动后会自动同步');
      return state;
    }
    await openStudioEditor(editorClipId);
    toast('字幕已更新，编辑器已同步');
  }
  return state;
}
function renderEditorAutoStatus(state){
  const node=$('editor-auto-status');if(!node)return;
  const trim=state.autoTrimmedSeconds?` · 已剪掉气口 ${Number(state.autoTrimmedSeconds).toFixed(1)} 秒`:'';
  const text={ready:`自动提炼完成 · 预选 ${state.autoKeepCount} 句${trim}`,
    partial:`自动提炼基本完成（部分步骤被跳过）· 预选 ${state.autoKeepCount} 句${trim}`,
    running:'正在自动提炼并 AI 分析字幕…',empty:'这个视频还没有字幕，先完成转录再自动提炼。',
    failed:'自动提炼失败，可点击「自动提炼并勾选」重试'}[state.autoStatus];
  node.textContent=text||'转录完成后会自动提炼、AI 分析并预先勾选建议保留的句子。';
}
function updateEditorRange(source){
  if(source==='start-range')$('editor-start').value=$('editor-start-range').value;
  if(source==='end-range')$('editor-end').value=$('editor-end-range').value;
  if(source==='start')$('editor-start-range').value=$('editor-start').value;
  if(source==='end')$('editor-end-range').value=$('editor-end').value;
  $('editor-range').textContent=`${Number($('editor-start').value).toFixed(1)} 秒 → ${Number($('editor-end').value).toFixed(1)} 秒`;
}
['start','end'].forEach(key=>{
  $(`editor-${key}`).addEventListener('input',()=>updateEditorRange(key));
  $(`editor-${key}-range`).addEventListener('input',()=>updateEditorRange(`${key}-range`));
});
function renderEditorSegments(){
  $('editor-segments').innerHTML=editorSegments.length?editorSegments.map((s,i)=>`<div class="segment-row"><input type="checkbox" data-segment-select="${i}" ${s.selected?'checked':''} aria-label="选择第 ${i+1} 句"><button type="button" class="quiet" data-audition="${i}" aria-label="试听第 ${i+1} 句">▶</button><input type="number" min="0" step="0.1" value="${Number(s.start).toFixed(1)}" data-segment-start="${i}" aria-label="开始秒数"><input type="number" min="0" step="0.1" value="${Number(s.end).toFixed(1)}" data-segment-end="${i}" aria-label="结束秒数"><textarea rows="2" data-segment-text="${i}" aria-label="第 ${i+1} 句文字">${esc(s.text)}</textarea><button type="button" class="quiet" data-timeline-hook="${i}">设为钩子</button></div>`).join(''):'<p class="empty">暂无字幕。可在本地素材页匹配字幕或手动转写。</p>';
}
function collectEditorSegments(){
  editorSegments=editorSegments.map((s,i)=>({...s,start:Number(document.querySelector(`[data-segment-start="${i}"]`)?.value),end:Number(document.querySelector(`[data-segment-end="${i}"]`)?.value),text:document.querySelector(`[data-segment-text="${i}"]`)?.value||'',selected:!!document.querySelector(`[data-segment-select="${i}"]`)?.checked}));
  if(editorSegments.some(x=>!Number.isFinite(x.start)||!Number.isFinite(x.end)||x.start<0||x.end<=x.start))throw new Error('字幕时间需满足结束时间大于开始时间');
}
async function saveEditorSegments(){
  if(!editorClipId)throw new Error('请先选择视频');collectEditorSegments();
  editorSegments.sort((a,b)=>a.start-b.start);renderEditorSegments();
  const preview=await api(`/studio/clips/${encodeURIComponent(editorClipId)}/transcript`,'PUT',{language:editorData.transcript?.language||'',segments:editorSegments.map(({start,end,text})=>({start,end,text}))});
  editorData.transcript=preview;
  // 自己保存字幕也会抬高版本号，先认领，避免自动同步把手上的时间线改动冲掉。
  editorDirty=false;await editorSyncAutoState(true);
  toast('字幕已保存');return preview;
}
function renderAiAdvice(advice){
  const panel=$('ai-advice');if(!panel)return;
  const summary=advice?.summary||'',captions=advice?.captions||[],tips=advice?.tips||[];
  if(!summary&&!captions.length&&!tips.length){panel.hidden=true;panel.innerHTML='';return;}
  panel.hidden=false;
  panel.innerHTML=`<div class="section-title"><h2>AI 总结与文案建议</h2><span class="muted">仅供参考，发布前请核对</span></div>`
    +(summary?`<p class="ai-summary">${esc(summary)}</p>`:'')
    +captions.map((text,i)=>`<div class="ai-caption"><p>${esc(text)}</p><button type="button" class="quiet" data-copy-caption="${i}">复制文案</button></div>`).join('')
    +(tips.length?`<div class="ai-tips"><strong>切片建议</strong><ul>${tips.map(x=>`<li>${esc(x)}</li>`).join('')}</ul></div>`:'');
  panel.querySelectorAll('[data-copy-caption]').forEach(button=>button.addEventListener('click',async()=>{
    const text=captions[Number(button.dataset.copyCaption)]||'';if(!text)return;
    try{await navigator.clipboard.writeText(text);}
    catch{const area=document.createElement('textarea');area.value=text;area.style.position='fixed';area.style.opacity='0';document.body.append(area);area.select();document.execCommand('copy');area.remove();}
    toast('文案已复制，可直接粘贴到发布页');
  }));
}
function renderStudioEvents(items){
  editorData.events=items;
  $('editor-events').innerHTML=items.map(x=>`<div class="event-row"><label class="event-select"><input type="checkbox" data-event-select="${esc(x.id)}"><span><strong>主题：${esc(x.title)}</strong><small>开始 ${secs(Number(x.start))} · 结束 ${secs(Number(x.end))} · ${esc(x.source)}</small><small>简介：${esc(x.summary)}</small>${x.hook?`<small>开头：${esc(x.hook)}</small>`:''}</span></label><div class="event-buttons"><button class="quiet" type="button" data-use-event="${esc(x.id)}" title="把上方剪辑范围设为该事件的起止时间，并把剪辑时间线裁剪为只剩该范围内的片段（范围外删除，可撤销）">选取并裁剪时间线</button><button class="quiet" type="button" data-add-event="${esc(x.id)}">加入时间线</button></div></div>`).join('')||'<p class="empty">暂无事件。可按字幕提炼或手动建立。</p>';
}
async function reloadStudioEvents(){if(!editorClipId)return;const keyword=$('event-search').value.trim();renderStudioEvents(await api(`/studio/events?clipId=${encodeURIComponent(editorClipId)}&keyword=${encodeURIComponent(keyword)}`));}
function editorPlan(){
  if(!editorClipId)throw new Error('请先选择视频');collectEditorSegments();
  const plan={start:Number($('editor-start').value),end:Number($('editor-end').value),burnSubtitles:$('editor-burn').checked,selectedSegments:editorSegments.flatMap((s,i)=>s.selected?[i]:[]),voiceOnly:$('editor-voice-mode').value==='vocals',musicAssetId:$('editor-music').value||null,effectAssetId:$('editor-effect').value||null,voiceVolume:Number($('editor-voice-volume').value),musicVolume:Number($('editor-music-volume').value),effectVolume:Number($('editor-effect-volume').value),effectAt:Number($('editor-effect-at').value)};
  if(!(plan.start>=0&&plan.end>plan.start))throw new Error('剪辑结束时间必须晚于开始时间');
  // 剪气口备份随工程一起保存，重新打开编辑器后仍可复原。
  const silence={silenceTrimBackup:Array.isArray(editorSilenceBackup)&&editorSilenceBackup.length?editorSilenceBackup:null,silenceTrimmed:!!(editorSilenceTrimmed&&editorSilenceBackup?.length)};
  return typeof timelinePlan==='function'?{...plan,...timelinePlan(),...silence}:{...plan,...silence};
}
/// <summary>只保存剪辑方案（不动字幕），供「复原自动剪气口」这类时间线操作立即落盘。</summary>
async function persistEditorProject(){
  if(!editorClipId)throw new Error('请先选择视频');
  collectEditorSegments();
  await api(`/studio/clips/${encodeURIComponent(editorClipId)}/editor`,'PUT',editorPlan());
  editorDirty=false;
}
function stopEditorAudio(){
  clearTimeout(editorEffectDelay);editorEffectDelay=0;
  for(const id of ['editor-vocals-preview','editor-music-preview','editor-effect-preview'])$(id).pause();
  if(typeof previewStopAudio==='function')previewStopAudio();
}
async function seekAudio(element,seconds){
  if(element.readyState<1)await new Promise((resolve,reject)=>{
    element.addEventListener('loadedmetadata',resolve,{once:true});
    element.addEventListener('error',()=>reject(new Error('试听音频无法加载')),{once:true});
    element.load();
  });
  element.currentTime=seconds;
}
async function previewEditorMix(){
  const plan=editorPlan(),player=$('editor-video');stopEditorAudio();player.pause();
  editorListenUntil=plan.end;player.currentTime=plan.start;
  player.muted=plan.voiceOnly;player.volume=Math.min(1,plan.voiceVolume);
  if(plan.voiceOnly){
    if(!editorData?.stems)throw new Error('请先运行 Demucs 人声分离');
    const voice=$('editor-vocals-preview');voice.src=`/api/studio/clips/${encodeURIComponent(editorClipId)}/stems/vocals`;await seekAudio(voice,plan.start);voice.volume=Math.min(1,plan.voiceVolume);
  }
  const music=audioAssets.find(x=>x.id===plan.musicAssetId);
  if(music){const element=$('editor-music-preview');element.src=`/api/assets/${encodeURIComponent(music.id)}/file`;await seekAudio(element,0);element.volume=Math.min(1,plan.musicVolume);}
  const effect=audioAssets.find(x=>x.id===plan.effectAssetId);
  if(effect){const element=$('editor-effect-preview');element.src=`/api/assets/${encodeURIComponent(effect.id)}/file`;await seekAudio(element,0);element.volume=Math.min(1,plan.effectVolume);}
  await player.play();
  if(plan.voiceOnly)$('editor-vocals-preview').play().catch(error=>toast(error.message));
  if(music)$('editor-music-preview').play().catch(error=>toast(error.message));
  if(effect)editorEffectDelay=setTimeout(()=>$('editor-effect-preview').play().catch(()=>{}),plan.effectAt*1000);
}
async function renderStudioRenders(){
  if(!editorClipId)return;const renders=(await api('/studio/renders')).filter(x=>x.clipId===editorClipId);
  $('editor-renders').innerHTML=renders.map(x=>`<div class="event-row"><div><strong>${esc(x.outputPath.split(/[\\/]/).pop())}</strong><small>${esc(x.status==='completed'?'已完成':x.status==='failed'?'失败：'+x.error:'正在生成')}</small></div>${x.status==='completed'?`<a class="text-link" href="/api/studio/renders/${encodeURIComponent(x.id)}/file" download>下载成片</a>`:''}</div>`).join('')||'<p class="empty">尚无成片</p>';
}
function aiConnection(){return {provider:$('ai-provider').value,endpoint:$('ai-endpoint').value.trim(),model:$('ai-model').value.trim(),apiKey:$('ai-key').value.trim()||null};}
async function pollModelDownload(){
  try{const state=await api('/ai/download');$('ai-download-status').textContent=state.state==='idle'?'':`${state.model} · ${state.progress}% · ${state.detail||state.state}`;}
  catch{}
}

$('import-form').addEventListener('submit',async e=>{e.preventDefault();$('import-result').textContent='正在导入…';try{const result=await api('/studio/import','POST',{videoDirectory:$('video-folder').value.trim(),subtitleDirectory:$('subtitle-folder').value.trim()||null,outputDirectory:$('output-folder').value.trim()});$('import-result').textContent=`新增 ${result.added} 个，已有 ${result.existing} 个；匹配字幕 ${result.matchedSubtitles} 个`;await refresh();await studioLoadLists();}catch(error){$('import-result').textContent=error.message}});
$('audio-import-form').addEventListener('submit',async e=>{e.preventDefault();const file=$('audio-file').files[0];if(!file)return;const body=new FormData();body.append('file',file);try{const response=await fetch(`/api/assets/import?kind=${encodeURIComponent($('audio-kind').value)}&style=${encodeURIComponent($('audio-style').value.trim()||'自定义')}`,{method:'POST',body});if(!response.ok){const data=await response.json();throw new Error(data.error||'导入失败')}toast('音频已导入');$('audio-import-form').reset();await studioLoadLists()}catch(error){toast(error.message)}});
$('editor-load').addEventListener('click',()=>{if($('editor-clip').value)openStudioEditor($('editor-clip').value)});
$('editor-preview-range').addEventListener('click',()=>previewEditorRange().catch(error=>toast(error.message)));
$('editor-preview-mix').addEventListener('click',()=>previewEditorMix().catch(error=>toast(error.message)));
$('editor-video').addEventListener('timeupdate',()=>{if(editorListenUntil&&$('editor-video').currentTime>=editorListenUntil){if(typeof previewPauseAll==='function')previewPauseAll();else $('editor-video').pause();stopEditorAudio();editorListenUntil=0;}});
['voice','music','effect'].forEach(kind=>$(`editor-${kind}-volume`).addEventListener('input',()=>{
  const volume=Math.min(1,Number($(`editor-${kind}-volume`).value));
  if(kind==='voice'){$('editor-video').volume=volume;$('editor-vocals-preview').volume=volume;}
  else $(`editor-${kind}-preview`).volume=volume;
}));
$('editor-save-subtitles').addEventListener('click',async()=>{try{await saveEditorSegments()}catch(error){toast(error.message)}});
$('editor-clean').addEventListener('click',async()=>{try{await saveEditorSegments();const clean=await api(`/studio/clips/${encodeURIComponent(editorClipId)}/transcript/clean`,'POST');editorSegments=clean.segments.map(x=>({...x,selected:true}));renderEditorSegments();toast('已清理口头禅，请检查并保存字幕')}catch(error){toast(error.message)}});
if($('editor-auto-plan'))$('editor-auto-plan').addEventListener('click',async()=>{
  if(!editorClipId)return toast('请先选择视频');
  const button=$('editor-auto-plan');button.disabled=true;editorAutoBusy=true;
  if($('editor-auto-status'))$('editor-auto-status').textContent='正在提炼字幕并进行 AI 分析…';
  try{
    await saveEditorSegments();
    const report=await api(`/studio/clips/${encodeURIComponent(editorClipId)}/auto-plan`,'POST');
    editorAutoBusy=false;await openStudioEditor(editorClipId);
    const ok=report.status==='ready'||report.status==='partial';
    if($('editor-auto-status'))$('editor-auto-status').textContent=ok?`自动提炼完成 · 事件 ${Number(report.start||0).toFixed(1)}–${Number(report.end||0).toFixed(1)} 秒 · 预选 ${report.keepCount} / ${report.segmentCount} 句 · ${report.source==='ai'?'AI 分析':'规则提炼'}${report.trimmedSeconds?` · 已剪掉气口 ${Number(report.trimmedSeconds).toFixed(1)} 秒`:''}${report.error?` · ${report.error}`:''}`:report.error||'自动提炼未完成';
    toast(ok?'已按建议勾选句子，未勾选的句子已在时间线里分割删除':report.error||'自动提炼未完成');
  }catch(error){if($('editor-auto-status'))$('editor-auto-status').textContent=error.message;toast(error.message)}
  finally{editorAutoBusy=false;button.disabled=false}
});
if($('editor-trim-silence'))$('editor-trim-silence').addEventListener('click',async()=>{
  if(!editorClipId)return toast('请先选择视频');
  const button=$('editor-trim-silence');button.disabled=true;
  const status=$('editor-status');
  // 检测要解码整条音轨，长视频需要几十秒；轮询进度并把百分比显示出来，避免界面看起来像卡死了。
  const poll=setInterval(()=>{
    api(`/studio/clips/${encodeURIComponent(editorClipId)}/trim-silence-progress`)
      .then(x=>{if(status)status.textContent=`正在检测静音 ${Math.min(99,Math.round(Number(x.progress)*100))}%，视频越长耗时越久…`})
      .catch(()=>{});
  },500);
  try{
    if(typeof timelinePlan!=='function')throw new Error('时间线未就绪');
    if(status)status.textContent='正在检测静音…';
    // 剪之前先留一份时间线快照，「复原自动剪气口」用它还原。
    const before=cloneTimeline();
    const result=await api(`/studio/clips/${encodeURIComponent(editorClipId)}/trim-silence`,'POST',{timeline:timelinePlan().timeline});
    if(!result.timeline?.length)throw new Error('剪气口后时间线为空，请调整参数');
    timelineCheckpoint();
    timelineItems=result.timeline.map(x=>({...x,id:x.id||crypto.randomUUID()}));
    timelineSelection=timelineItems[0]?.id||'';timelineTime=0;timelineRender();
    await timelineSeek(0,false);
    if(Number(result.trimmedSeconds)>0&&before.length){
      editorSilenceBackup=before;editorSilenceTrimmed=true;
      try{await persistEditorProject();}
      catch(error){editorMarkDirty();}
    }
    renderSilenceRestoreButton();
    $('editor-status').textContent=`已剪掉 ${result.silenceCount} 处气口，共 ${Number(result.trimmedSeconds).toFixed(1)} 秒${editorSilenceBackup?'，可随时复原':''}`;
    toast(result.trimmedSeconds>0?`自动剪气口完成，缩短 ${Number(result.trimmedSeconds).toFixed(1)} 秒`:'没有找到可删除的气口');
  }catch(error){if(status)status.textContent='';toast(error.message)}
  finally{clearInterval(poll);button.disabled=false}
});
// 复原自动剪气口：把时间线恢复到剪气口之前并立即落盘。
if($('editor-restore-silence'))$('editor-restore-silence').addEventListener('click',async()=>{
  if(!editorClipId)return toast('请先选择视频');
  if(!Array.isArray(editorSilenceBackup)||!editorSilenceBackup.length)return toast('当前视频没有可复原的剪气口记录');
  const button=$('editor-restore-silence'),restoredCount=editorSilenceBackup.length;button.disabled=true;
  try{
    if(typeof timelineCheckpoint==='function')timelineCheckpoint();
    timelineItems=editorSilenceBackup.map(x=>({...x,id:x.id||crypto.randomUUID()}));
    editorSilenceBackup=null;editorSilenceTrimmed=false;
    timelineSelection=timelineItems[0]?.id||'';timelineTime=0;timelineRender();
    let saved=true;
    try{await persistEditorProject();}catch(error){saved=false;editorMarkDirty();}
    try{await timelineSeek(0,false);}catch{}
    $('editor-status').textContent=saved?`已复原剪气口前的时间线（${restoredCount} 段）并保存`:`已复原剪气口前的时间线（${restoredCount} 段），请点「保存剪辑方案」`;
    toast(saved?'已复原自动剪气口':'已复原时间线，请记得保存剪辑方案');
  }catch(error){toast(error.message)}
  finally{renderSilenceRestoreButton();}
});
$('event-search').addEventListener('input',()=>{clearTimeout(eventSearchTimer);eventSearchTimer=setTimeout(()=>reloadStudioEvents().catch(error=>toast(error.message)),300)});
$('event-extract').addEventListener('click',async()=>{try{await saveEditorSegments();const events=await api('/studio/events/extract','POST',{clipId:editorClipId});$('event-status').textContent=`提炼 ${events.length} 个事件`;await reloadStudioEvents()}catch(error){toast(error.message)}});
$('event-ai').addEventListener('click',async()=>{if(!editorClipId)return;try{$('event-status').textContent='AI 正在分析字幕…';await saveEditorSegments();const result=await api('/ai/analyze','POST',{clipId:editorClipId,connection:aiConnection()});renderAiAdvice(result.advice||null);const advice=result.advice&&((result.advice.summary||'').length||(result.advice.captions||[]).length||(result.advice.tips||[]).length);$('event-status').textContent=`AI 生成 ${result.events?.length??0} 个事件`+(result.adviceError?` · 文案建议失败：${result.adviceError}`:advice?' · 总结与文案建议已生成，见事件列表上方':'');await reloadStudioEvents()}catch(error){$('event-status').textContent=error.message;toast(error.message)}});
$('event-rerank').addEventListener('click',async()=>{if(!editorClipId)return;try{$('event-status').textContent='正在重跑候选排序…';const result=await api('/ai/rerank','POST',{clipId:editorClipId,connection:aiConnection()});$('event-status').textContent=`排序完成，共 ${result.events?.length??0} 个候选事件（复用分块缓存，边界已按音频峰值与语句对齐优化）`;await reloadStudioEvents();toast('候选排序已更新')}catch(error){$('event-status').textContent=error.message;toast(error.message)}});
$('event-create').addEventListener('click',async()=>{try{const title=$('event-title').value.trim();if(!title)throw new Error('请先输入事件标题');await api('/studio/events','POST',{clipId:editorClipId,start:Number($('editor-start').value),end:Number($('editor-end').value),title,summary:'',hook:''});$('event-title').value='';await reloadStudioEvents();toast('事件已建立')}catch(error){toast(error.message)}});
$('event-merge').addEventListener('click',async()=>{try{const ids=[...document.querySelectorAll('[data-event-select]:checked')].map(x=>x.dataset.eventSelect);await api('/studio/events/merge','POST',{clipId:editorClipId,ids});await reloadStudioEvents();toast('事件已合并')}catch(error){toast(error.message)}});
$('editor-separate').addEventListener('click',async()=>{try{await api(`/studio/clips/${encodeURIComponent(editorClipId)}/separate`,'POST');$('editor-status').textContent='Demucs 人声分离已加入任务';toast('人声分离已开始')}catch(error){toast(error.message)}});
$('editor-save').addEventListener('click',async()=>{try{await saveEditorSegments();await api(`/studio/clips/${encodeURIComponent(editorClipId)}/editor`,'PUT',editorPlan());editorDirty=false;$('editor-status').textContent='剪辑方案已保存';toast('剪辑方案已保存')}catch(error){toast(error.message)}});
$('editor-render').addEventListener('click',async()=>{try{await saveEditorSegments();await api(`/studio/clips/${encodeURIComponent(editorClipId)}/render`,'POST',editorPlan());$('editor-status').textContent='正在后台生成成片';await renderStudioRenders();toast('成片任务已创建')}catch(error){toast(error.message)}});
$('ai-refresh-models').addEventListener('click',async()=>{try{$('ai-download-status').textContent='正在读取模型…';const models=await api('/ai/models','POST',aiConnection());$('ai-model-options').innerHTML=models.map(x=>`<option value="${esc(x)}"></option>`).join('');$('ai-download-status').textContent=`找到 ${models.length} 个模型`;if(!models.includes($('ai-model').value)&&models.length)$('ai-model').value=models[0]}catch(error){$('ai-download-status').textContent=error.message}});
$('ai-pull-model').addEventListener('click',async()=>{try{if($('ai-provider').value!=='ollama')throw new Error('请先选择本机 Ollama');const model=$('ai-model').value.trim()||'qwen2.5:1.5b';await api('/ai/download','POST',{model});toast(`开始下载 ${model}`);await pollModelDownload()}catch(error){toast(error.message)}});
$('ai-provider').addEventListener('change',()=>{const local=$('ai-provider').value==='ollama';$('ai-key').disabled=local;$('ai-endpoint').disabled=local;$('ai-pull-model').hidden=!local;$('ai-start-runtime').hidden=!local;$('ai-runtime-status').hidden=!local;});
$('ai-start-runtime').onclick=async()=>{const button=$('ai-start-runtime');button.disabled=true;$('ai-runtime-status').textContent='正在检查并启动本机服务…';try{const state=await api('/ai/runtime/start','POST');$('ai-runtime-status').textContent=state.detail;toast('Ollama 已连接，可以下载或使用模型');}catch(e){$('ai-runtime-status').textContent=e.message;}finally{button.disabled=false}};
document.addEventListener('click',async e=>{
  const edit=e.target.closest('button[data-edit]');if(edit){await openStudioEditor(edit.dataset.edit);return;}
  const match=e.target.closest('button[data-local-subtitle]');if(match){document.querySelector(`[data-subtitle-input="${match.dataset.localSubtitle}"]`)?.click();return;}
  const transcribe=e.target.closest('button[data-local-transcribe],button[data-local-retranscribe]');if(transcribe){const clip=snapshot?.clips?.find(x=>x.id===(transcribe.dataset.localTranscribe||transcribe.dataset.localRetranscribe));if(clip){
    // 重新转译会中断正在运行的那一次并从头排队，界面上的耗时随之重新计时。
    const restart=transcribe.dataset.localRetranscribe!==undefined;
    try{await api(`/transcribe/${encodeURIComponent(clip.eventId)}${restart?'/restart':''}`,'POST');localTranscribing[clip.eventId]={progress:.02,startedAt:new Date().toISOString(),message:restart?'已中断，正在重新排队…':'正在排队…',retryCount:0};renderLocalVideos();startTranscribePolling();toast(restart?'已中断并重新开始转译':'转译任务已开始，进度显示在列表里');}catch(error){toast(error.message)}}
    return;}
  const use=e.target.closest('button[data-use-audio]');if(use){if(!editorClipId){toast('请先在剪辑编辑器载入视频');return;}const select=$(`editor-${use.dataset.audioKind==='music'?'music':'effect'}`);select.value=use.dataset.useAudio;select.dispatchEvent(new Event('change',{bubbles:true}));show('editor');toast('已选用音频');return;}
  const event=e.target.closest('button[data-use-event]');if(event){const item=editorData?.events?.find(x=>x.id===event.dataset.useEvent);if(item){$('editor-start').value=item.start;$('editor-end').value=item.end;updateEditorRange('start');updateEditorRange('end');$('editor-video').currentTime=item.start;
    // 选取事件范围后，时间线自动只保留该范围内的主视频片段：范围外的片段直接删除，跨界片段按边界裁剪。
    // 结果可随时用时间线工具栏的「撤销」恢复；钩子、插入素材等其它素材不受影响。
    if(typeof timelineKeepSelection==='function'){try{timelineKeepSelection()}catch(x){toast(x.message)}}
    if(typeof timelineSeek==='function'&&timelineItems?.length)timelineSeek(0,false).catch(()=>{});
  }return;}
  const audition=e.target.closest('button[data-audition]');if(audition){try{await previewAuditionSegment(Number(audition.dataset.audition))}catch(error){toast(error.message)}return;}
});
document.addEventListener('change',async e=>{
  const input=e.target.closest('input[data-subtitle-input]');if(!input?.files?.[0])return;
  const id=input.dataset.subtitleInput,body=new FormData();body.append('file',input.files[0]);
  try{const response=await fetch(`/api/studio/clips/${encodeURIComponent(id)}/subtitle`,{method:'POST',body});if(!response.ok){const data=await response.json();throw new Error(data.error||'字幕读取失败')}toast('字幕已匹配');await refresh();renderLocalVideos();if(editorClipId===id)await openStudioEditor(id)}catch(error){toast(error.message)}finally{input.value=''}
});
document.querySelectorAll('.nav[data-page="recordings"],.nav[data-page="audio"],.nav[data-page="editor"]').forEach(x=>x.addEventListener('click',studioLoadLists));
document.querySelectorAll('.nav:not([data-page="editor"])').forEach(x=>x.addEventListener('click',()=>{stopEditorAudio();if(typeof previewPauseAll==='function')previewPauseAll();else $('editor-video').pause();}));
setInterval(refreshTranscribeDetails,1000);
studioLoadLists();setTimeout(()=>$('ai-provider').dispatchEvent(new Event('change')),400);setInterval(async()=>{
  if(document.querySelector('#page-editor.active')){
    renderStudioRenders().catch(()=>{});
    if(editorClipId&&!editorData?.stems){
      try{const state=await api(`/studio/clips/${encodeURIComponent(editorClipId)}/stems`);if(state.ready){editorData.stems=true;$('editor-status').textContent='人声分离已完成';}}catch{}
    }
    if(editorClipId&&!editorAutoBusy)await editorSyncAutoState().catch(()=>{});
  }
  pollModelDownload();
},5000);
// 每次切到剪辑编辑器窗口都自动同步一次：字幕版本或自动方案有更新时立即重新载入（手改未保存的内容不受影响）。
document.querySelector('.nav[data-page="editor"]').addEventListener('click',()=>{
  if(editorClipId&&!editorAutoBusy)editorSyncAutoState().catch(()=>{});
});
