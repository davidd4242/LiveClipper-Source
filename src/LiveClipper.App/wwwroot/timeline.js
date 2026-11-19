let timelineItems=[],timelineSelection='',timelineTime=0,timelineZoom=5,timelinePlaying=false,timelineIndex=0,timelineLoading=false;
let timelineHistory=[],timelineFuture=[],timelineMeta=new Map(),filePickRequests=new Map();
let timelineSentenceMode=false,timelineSentenceRange=null;
const cloneTimeline=()=>JSON.parse(JSON.stringify(timelineItems));
const timelineTotal=()=>timelineItems.reduce((n,x)=>n+x.end-x.start,0);
const timelineLabel=id=>localVideos.find(x=>x.clipId===id)?.title||('高光 '+(snapshot?.clips.find(x=>x.id===id)?.eventId||id).slice(0,8));
const timelineRound=n=>Math.round(n*1000)/1000;
const timelineCurrent=()=>timelineItems.find(x=>x.id===timelineSelection);
// 未保存的改动：自动同步只在没有手改时间线时才重新载入，避免冲掉编辑。
// 所有时间线改动都会先建立检查点，顺便标记「有未保存改动」。
function timelineCheckpoint(){timelineHistory.push(cloneTimeline());if(timelineHistory.length>60)timelineHistory.shift();timelineFuture=[];if(typeof editorMarkDirty==='function')editorMarkDirty();}
function timelineRestore(undo){const from=undo?timelineHistory:timelineFuture,to=undo?timelineFuture:timelineHistory;if(!from.length)return;to.push(cloneTimeline());timelineItems=from.pop();timelineSelection=timelineItems[0]?.id||'';timelineRender();if(timelineSelection)timelineSelect(timelineSelection).catch(e=>toast(e.message));}
function timelineOffset(id){let offset=0;for(const x of timelineItems){if(x.id===id)return offset;offset+=x.end-x.start;}return offset;}
async function timelineEnsureSource(id){if(timelineMeta.has(id))return timelineMeta.get(id);const data=await api('/studio/clips/'+encodeURIComponent(id)+'/editor');timelineMeta.set(id,data);return data;}
async function timelineSource(id){
  // 双缓冲：standby 元素可能已经载入目标源（预载下一片段），优先切换过去而不是重新加载。
  const standby=typeof previewStandby==='function'?previewStandby():null;
  const ready=[player,standby].filter(Boolean).find(x=>x.dataset.source===id&&x.readyState>=1);
  if(ready){if(ready!==player&&typeof previewActivate==='function')previewActivate(ready);return;}
  timelineLoading=true;
  try{
    await new Promise((resolve,reject)=>{player.addEventListener('loadedmetadata',resolve,{once:true});player.addEventListener('error',()=>reject(new Error('视频无法预览，请检查文件是否存在')),{once:true});player.src='/api/clips/'+encodeURIComponent(id)+'/video';player.dataset.source=id;player.load();});
  }finally{timelineLoading=false;}
}

// WebView2/Chromium 在“设置 currentTime 后立刻 play()”时偶发停留在 ended 状态。
// 这里等待 seek 完成再播放，并给 exact-seek 加一个极小偏移，解决自动剪气口后首次播放不动的问题。
async function timelineSetCurrentTime(player,target,maxTime){
  target=Math.max(0,Math.min(target,Math.max(0,maxTime-.001)));
  if(!Number.isFinite(target))throw new Error('播放位置无效');
  // 精确落在剪切边界/结尾时，Chromium 偶发停在不可播关键帧；加 1ms 只影响不可感知的位置。
  if(target>0)target=Math.min(target+.001,maxTime-.001);
  player.currentTime=target;
  await new Promise(resolve=>{
    let settled=false;const finish=()=>{if(settled)return;settled=true;player.removeEventListener('seeked',finish);player.removeEventListener('error',finish);resolve();};
    player.addEventListener('seeked',finish,{once:true});player.addEventListener('error',finish,{once:true});
    if(!player.seeking)setTimeout(finish,50);
    setTimeout(finish,1500);
  });
}
async function timelineLoadProject(data,id){
  timelinePlaying=false;timelineMeta.set(id,data);
  if(typeof window.__fitDashboard==='function')window.__fitDashboard();
  if(typeof previewResetPlayback==='function')previewResetPlayback();
  player.dataset.source=id;
  timelineItems=(data.project?.timeline||[{id:crypto.randomUUID(),clipId:id,start:data.project?.start??0,end:data.project?.end??data.duration,kind:'video'}]).map(x=>({...x,id:x.id||crypto.randomUUID()}));
  // 工程里可能留着已被删除的源视频：先把失效片段摘掉，避免整条时间线都加载不出来。
  const known=new Set([id,...(snapshot?.clips||[]).map(x=>x.id)]);
  const dropped=timelineItems.filter(x=>!known.has(x.clipId)).length;
  if(dropped)timelineItems=timelineItems.filter(x=>known.has(x.clipId));
  if(!timelineItems.length)timelineItems=[{id:crypto.randomUUID(),clipId:id,start:0,end:data.duration,kind:'video'}];
  if(dropped)toast(`已移除 ${dropped} 个失效片段（源视频已不存在）`);
  timelineSelection=timelineItems[0]?.id||'';timelineTime=0;timelineHistory=[];timelineFuture=[];
  timelineZoom=Math.max(5,Math.min(40,Math.round((($('timeline-scroll').clientWidth||720)-40)/Math.max(.01,timelineTotal())*2)/2));$('timeline-zoom').value=timelineZoom;
  timelineSentenceMode=!!data.project?.sentenceMode;timelineSentenceRange={start:data.project?.start??0,end:data.project?.end??data.duration};
  const p=data.project||{};$('timeline-font').value=p.subtitleFont||'Microsoft YaHei';$('timeline-fontsize').value=p.subtitleSize||22;$('timeline-color').value=p.subtitleColor||'#ffffff';$('timeline-subpos').value=p.subtitlePosition||'bottom';$('timeline-resolution').value=String(p.resolution??720);$('timeline-fps').value=String(p.fps??30);$('timeline-bitrate').value=String(p.bitrateKbps||0);
  $('editor-burn').checked=!!p.burnSubtitles;$('editor-voice-mode').value=p.voiceOnly?'vocals':'original';$('editor-music').value=p.musicAssetId||'';$('editor-effect').value=p.effectAssetId||'';$('editor-voice-volume').value=p.voiceVolume??1;$('editor-music-volume').value=p.musicVolume??.25;$('editor-effect-volume').value=p.effectVolume??.5;$('editor-effect-at').value=p.effectAt??0;
  if(Array.isArray(p.selectedSegments)){editorSegments.forEach((x,i)=>x.selected=p.selectedSegments.includes(i));renderEditorSegments();}
  // 单个源加载失败（文件被移动/删除）不应中断其余部分。
  await Promise.all([...new Set(timelineItems.map(x=>x.clipId))].map(id=>timelineEnsureSource(id).catch(()=>null)));
  timelineAssetChoices();timelineRender();timelineVariants();
  if(typeof advancedProjectLoaded==='function')advancedProjectLoaded(data);
  if(typeof previewProjectLoaded==='function')previewProjectLoaded();
  await timelineSeek(0,false);
}
function timelineAssetChoices(){
  const select=$('timeline-source');if(!select)return;const previous=select.value;
  const version=JSON.stringify((snapshot?.clips||[]).map(x=>[x.id,timelineLabel(x.id)]));if(select.dataset.version===version)return;select.dataset.version=version;
  select.innerHTML=(snapshot?.clips||[]).map(x=>'<option value="'+esc(x.id)+'">'+esc(timelineLabel(x.id))+'</option>').join('');select.value=previous||editorClipId;
}
function timelineRender(){
  const total=timelineTotal(),scale=timelineZoom,width=Math.max(680,total*scale+60);$('timeline-canvas').style.width=width+'px';
  const step=scale>15?2:scale>7?5:scale>2?10:30;let ticks='';
  for(let t=0;t<=total+step;t+=step)ticks+='<span style="left:'+t*scale+'px">'+short(t)+'</span>';
  $('timeline-ruler').innerHTML=ticks;let offset=0;let video='',audio='',subtitles='';
  for(const item of timelineItems){
    const left=offset*scale,w=(item.end-item.start)*scale,active=item.id===timelineSelection?' selected':'',kind=item.kind==='hook'?'片头钩子':item.kind==='insert'?'插入素材':'视频';
    video+='<div class="timeline-clip '+esc(item.kind||'video')+active+'" data-timeline-item="'+esc(item.id)+'" style="left:'+left+'px;width:'+w+'px" title="'+esc(timelineLabel(item.clipId))+' · '+item.start.toFixed(2)+'–'+item.end.toFixed(2)+' 秒"><i class="trim-handle left" data-trim="start"></i><span class="clip-name">'+esc(kind)+' · '+esc(timelineLabel(item.clipId))+'</span><small>'+item.start.toFixed(1)+' → '+item.end.toFixed(1)+' 秒</small><i class="trim-handle right" data-trim="end"></i></div>';
    audio+='<div class="timeline-audio'+active+'" data-timeline-item="'+esc(item.id)+'" style="left:'+left+'px;width:'+w+'px">'+(item.kind==='hook'?'钩子音频':'关联原音')+'</div>';
    const meta=timelineMeta.get(item.clipId);const segments=item.clipId===editorClipId?editorSegments:(meta?.transcript?.segments||[]);
    segments.forEach(s=>{if(item.clipId===editorClipId&&item.kind!=='hook'&&s.selected===false)return;const start=Math.max(item.start,s.start),end=Math.min(item.end,s.end);if(end>start)subtitles+='<div class="timeline-caption" data-timeline-parent="'+esc(item.id)+'" style="left:'+((offset+start-item.start)*scale)+'px;width:'+Math.max(2,(end-start)*scale)+'px" title="'+esc(s.text)+'">'+esc(s.text)+'</div>';});
    offset+=item.end-item.start;
  }
  $('timeline-video-track').innerHTML=video||'<p class="timeline-empty">拖入素材，或选择素材后点击加入</p>';$('timeline-audio-track').innerHTML=audio;$('timeline-caption-track').innerHTML=subtitles;
  const music=audioAssets.find(x=>x.id===$('editor-music').value);$('timeline-music-track').innerHTML=music?'<div class="timeline-music" style="width:'+total*scale+'px">'+esc(music.name)+'</div>':'';
  $('timeline-playhead').style.left=timelineTime*scale+'px';$('timeline-time').textContent=secs(timelineTime)+' / '+secs(total);$('timeline-count').textContent=timelineItems.length+' 个片段 · '+total.toFixed(2)+' 秒';
  const item=timelineCurrent();$('timeline-selected').textContent=item?timelineLabel(item.clipId)+' · 源范围 '+item.start.toFixed(2)+'–'+item.end.toFixed(2)+' 秒':'选择片段可修改范围';
  $('timeline-undo').disabled=!timelineHistory.length;$('timeline-redo').disabled=!timelineFuture.length;
  if(typeof advancedEffectSelection==='function')advancedEffectSelection();
  if(typeof previewRefresh==='function')previewRefresh();
}
async function timelineSelect(id,seek=true){
  timelineSelection=id;const item=timelineCurrent();if(!item)return;const meta=await timelineEnsureSource(item.clipId);
  ['start','end'].forEach(k=>{$('editor-'+k).max=meta.duration;$('editor-'+k+'-range').max=meta.duration;$('editor-'+k).value=item[k];});updateEditorRange('start');updateEditorRange('end');timelineRender();
  if(seek)await timelineSeek(timelineOffset(id),false);
}
async function timelineSeek(time,play=false){
  if(!timelineItems.length)return;
  time=Math.max(0,Math.min(time,timelineTotal()-.001));
  // 落段判断：目标恰在片段边界时必须归入"下一段"。此前严格小于的判断在浮点累加误差下
  // 会把边界时间落回前一段末尾（差 1e-4 秒），seek 回上一段 → 1ms 后又触发边界 → 死循环，
  // 表现为剪气口后播放永久卡死。
  let offset=0,index=timelineItems.length-1;const eps=1e-4;
  for(let i=0;i<timelineItems.length;i++){
    const end=offset+timelineItems[i].end-timelineItems[i].start;
    if(time<end-eps){index=i;break;}
    if(time<end+eps){index=Math.min(i+1,timelineItems.length-1);break;}
    offset=end;
  }
  offset=timelineOffset(timelineItems[index].id);
  const item=timelineItems[index];
  // 同源连续播放：剪气口后相邻片段来自同一素材，优先用预载好的 standby 元素做零等待切换；
  // 不满足条件时退回原地 seek（WebView2 下跳切 seek 可能短暂卡帧）。
  const standby=typeof previewStandby==='function'?previewStandby():null;
  const sameSource=!player.ended&&player.readyState>=1&&player.dataset.source===item.clipId;
  const standbyReady=standby&&standby!==player&&standby.readyState>=1&&standby.dataset.source===item.clipId;
  const seamless=play&&timelinePlaying&&!player.paused&&(sameSource||standbyReady);
  timelineIndex=index;timelineSelection=item.id;timelineTime=time;editorListenUntil=0;
  if(seamless){
    let jumped=false;
    // 只有「明确为这一段预载好」的 standby 才允许无缝切换，位置容差收紧到 60ms：
    // 此前 ±0.5s 的容差会让停在上一段末尾的元素被误认为就位，造成双元素乒乓、进度回摆。
    if(standbyReady&&previewArmIndex===index&&typeof previewBufferedJump==='function')jumped=await previewBufferedJump(item,time-offset);
    if(!jumped&&sameSource){
      // 剪气口后的同源跳切也要等待 seek 完成；否则 WebView2 可能继续消费旧位置并短暂卡死。
      await timelineSetCurrentTime(player,item.start+time-offset,item.end);
      // WebView2 跳切后偶发让元素停留在 paused/ended 状态：尝试恢复播放。
      // 不等待 play()——数据不足时它的 promise 会悬挂；停顿由 tick 看门狗兜底。
      if(player.paused||player.ended)player.play().catch(()=>{});
    }
    const seamlessMeta=timelineMeta.get(item.clipId);for(const k of ['start','end']){$('editor-'+k).max=seamlessMeta?.duration||item.end;$('editor-'+k+'-range').max=seamlessMeta?.duration||item.end;$('editor-'+k).value=item[k];updateEditorRange(k);}
    timelineRender();if(typeof previewStart==='function')previewStart();
    return;
  }
  timelinePlaying=false;player.pause();
  stopEditorAudio();await timelineSource(item.clipId);
  player.muted=false;player.volume=Math.min(1,Number($('editor-voice-volume').value));await timelineSetCurrentTime(player,item.start+time-offset,item.end);
  const meta=timelineMeta.get(item.clipId);for(const k of ['start','end']){$('editor-'+k).max=meta?.duration||item.end;$('editor-'+k+'-range').max=meta?.duration||item.end;$('editor-'+k).value=item[k];updateEditorRange(k);}
  if(typeof previewPrepareAudio==='function')await previewPrepareAudio(item,time);
  timelineRender();timelinePlaying=play;if(play){try{await player.play();if(typeof previewStart==='function')previewStart();}catch(e){timelinePlaying=false;stopEditorAudio();throw e;}}
}
async function timelineAdd(clipId,range=null,kind='insert',at=null){
  if(!editorClipId){await openStudioEditor(clipId);return;}const meta=await timelineEnsureSource(clipId);timelineCheckpoint();
  const item={...(range?.clipId?range:{}),id:crypto.randomUUID(),clipId,start:range?.start??0,end:range?.end??meta.duration,kind};
  const index=at??(timelineItems.findIndex(x=>x.id===timelineSelection)+1||timelineItems.length);timelineItems.splice(index,0,item);await timelineSelect(item.id);toast('片段已加入时间线');
}
function timelineSplit(){
  const item=timelineCurrent();if(!item)return;const local=timelineTime-timelineOffset(item.id),duration=item.end-item.start;
  if(local<.05||local>duration-.05)return toast('把播放头移到片段内部再分割');
  timelineCheckpoint();const index=timelineItems.indexOf(item),cut=item.start+local;
  const right={...item,id:crypto.randomUUID(),start:timelineRound(cut)};item.end=timelineRound(cut);timelineItems.splice(index+1,0,right);timelineSelection=right.id;timelineRender();
}
function timelineRemove(){const item=timelineCurrent();if(!item)return;timelineCheckpoint();timelineItems=timelineItems.filter(x=>x.id!==item.id);timelineSelection=timelineItems[0]?.id||'';timelineTime=Math.min(timelineTime,timelineTotal());timelineRender();}
function timelineApplySentences(reuseRange=false){
  collectEditorSegments();if(!reuseRange||!timelineSentenceRange)timelineSentenceRange={start:Number($('editor-start').value),end:Number($('editor-end').value)};
  const {start,end}=timelineSentenceRange,ranges=[];let previous=-2;
  editorSegments.forEach((s,i)=>{if(!s.selected||s.end<=start||s.start>=end)return;const a=Math.max(start,s.start),b=Math.min(end,s.end),last=ranges.at(-1);if(last&&i===previous+1&&a-last.end<=.05)last.end=Math.max(last.end,b);else ranges.push({start:a,end:b});previous=i;});
  if(!ranges.length)return toast('当前范围内没有勾选的句子');timelineCheckpoint();
  timelineItems=[...timelineItems.filter(x=>x.kind==='hook'),...ranges.map(x=>({id:crypto.randomUUID(),clipId:editorClipId,...x,kind:'video'})),...timelineItems.filter(x=>x.kind==='insert')];timelineSentenceMode=true;timelineSelection=timelineItems[0]?.id||'';timelineTime=0;timelineRender();toast('时间线已按勾选句子剪切，未勾选内容不会出现在成片中');
}
/// <summary>
/// 只保留选中范围：主视频里与剪辑范围 [start,end] 相交的片段留下并按范围裁剪，范围外的片段直接删除；
/// 其它素材（钩子、插入片段、别的视频）不受影响。可用「撤销」随时恢复。
/// </summary>
function timelineKeepSelection(){
  if(!editorClipId)return toast('请先选择视频');
  const a=Number($('editor-start').value),b=Number($('editor-end').value);
  if(!Number.isFinite(a)||!Number.isFinite(b)||!(a>=0&&b>a))return toast('剪辑范围无效，请先设置开始与结束时间');
  const primary=timelineItems.filter(x=>x.clipId===editorClipId);
  if(!primary.length)return toast('时间线里没有主视频的片段');
  const kept=new Map();
  let dropped=0,clipped=0;
  for(const item of primary){
    if(item.end<=a||item.start>=b){dropped++;continue;}
    const start=Math.max(item.start,a),end=Math.min(item.end,b);
    if(end-start<=.01){dropped++;continue;}
    if(Math.abs(start-item.start)>.0005||Math.abs(end-item.end)>.0005)clipped++;
    kept.set(item.id,{...item,start,end});
  }
  if(!kept.size)return toast('选中范围里没有可保留的片段，请调整范围');
  const before=timelineTotal(),beforeCount=primary.length;
  // 保持原有顺序：主视频片段替换成裁剪后的版本，其它素材原样保留。
  const next=[];
  for(const item of timelineItems){
    if(item.clipId!==editorClipId){next.push(item);continue;}
    const replacement=kept.get(item.id);
    if(replacement)next.push(replacement);
  }
  timelineCheckpoint();
  timelineItems=next;
  timelineSelection=timelineItems[0]?.id||'';
  timelineTime=Math.min(timelineTime,timelineTotal());
  timelineRender();
  const saved=Math.max(0,Math.round((before-timelineTotal())*10)/10);
  toast(`只保留选中范围：删除 ${dropped} 段、裁剪 ${clipped} 段（${beforeCount} → ${kept.size} 段），共缩短 ${saved.toFixed(1)} 秒`);
}
function timelinePlan(){
  if(!timelineItems.length)throw new Error('时间线为空，请先加入片段');
  return {...(timelineSentenceMode&&timelineSentenceRange?timelineSentenceRange:{}),timeline:cloneTimeline(),sentenceMode:timelineSentenceMode,subtitleFont:$('timeline-font').value,subtitleSize:Number($('timeline-fontsize').value),subtitleColor:$('timeline-color').value,subtitlePosition:$('timeline-subpos').value,resolution:Number($('timeline-resolution').value),fps:Number($('timeline-fps').value),bitrateKbps:Number($('timeline-bitrate').value),duckMusic:$('advanced-duck')?.checked??true,subtitleOutline:Number($('advanced-outline')?.value??1.5),subtitleMargin:Number($('advanced-margin')?.value??18)};
}
async function timelineVariants(){
  if(!editorClipId)return;try{const list=await api('/studio/clips/'+encodeURIComponent(editorClipId)+'/variants');$('timeline-variants').innerHTML='<option value="">选择已保存方案</option>'+list.map(x=>'<option value="'+esc(x.id)+'">'+esc(x.name)+'</option>').join('');}catch{}
}
const panel=document.createElement('section');panel.className='timeline-panel';panel.innerHTML='<div class="section-title"><h2>剪辑时间线</h2><span class="muted" id="timeline-count"></span></div><div class="timeline-toolbar"><button id="timeline-play" type="button" class="quiet">▶ 播放时间线</button><button id="timeline-split" type="button" class="quiet">分割（Ctrl+B）</button><button id="timeline-delete" type="button" class="quiet">删除片段</button><button id="timeline-undo" type="button" class="quiet">撤销</button><button id="timeline-redo" type="button" class="quiet">重做</button><span id="timeline-time" class="mono"></span><label>缩放 <input id="timeline-zoom" type="range" min=".5" max="40" step=".5" value="5"></label></div><div class="timeline-shell"><div class="timeline-labels"><div>时间</div><div>V1 画面</div><div>A1 原音</div><div>C1 字幕</div><div>A2 配乐</div></div><div class="timeline-scroll" id="timeline-scroll"><div id="timeline-canvas"><div id="timeline-ruler"></div><div class="timeline-track video-track" id="timeline-video-track"></div><div class="timeline-track audio-track" id="timeline-audio-track"></div><div class="timeline-track caption-track" id="timeline-caption-track"></div><div class="timeline-track music-track" id="timeline-music-track"></div><div id="timeline-playhead"><i></i></div></div></div></div><div class="timeline-toolbar"><span class="muted" id="timeline-selected"></span><button id="timeline-apply-range" type="button" class="quiet">将上方范围应用到片段</button><button id="timeline-keep-selection" type="button" class="quiet" title="主视频只保留剪辑范围内的片段，范围外的片段直接删除（可撤销）">只保留选中范围</button><button id="timeline-duplicate" type="button" class="quiet">复制片段</button></div><div class="timeline-toolbar"><select id="timeline-source"></select><button id="timeline-add" type="button" class="quiet">加入素材</button><button id="timeline-import" type="button" class="quiet">导入文件…</button><button id="timeline-add-subtitle" type="button" class="quiet">加入字幕…</button><button id="timeline-sentences" type="button" class="quiet">按勾选句子剪切</button></div><p class="input-hint">拖动片段中央调整顺序；拖动左右边缘修改源视频范围。可直接把视频、字幕、音乐文件拖入窗口。点「只保留选中范围」会把主视频裁剪到上方剪辑范围，范围外的片段直接删除（用「撤销」可恢复）；勾选字幕后点「按勾选句子剪切」则只保留勾选的句子。</p><div class="timeline-options"><label>字幕字体<select id="timeline-font"><option value="Microsoft YaHei">微软雅黑</option><option value="SimHei">黑体</option><option value="SimSun">宋体</option><option value="Arial">Arial</option></select></label><label>字号<input id="timeline-fontsize" type="number" min="10" max="80" value="22"></label><label>颜色<input id="timeline-color" type="color" value="#ffffff"></label><label>位置<select id="timeline-subpos"><option value="bottom">底部</option><option value="middle">居中</option><option value="top">顶部</option></select></label><label>分辨率<select id="timeline-resolution"><option value="0">跟随源视频</option><option value="480">480P</option><option value="720" selected>720P</option><option value="1080">1080P</option><option value="2160">2160P</option></select></label><label>帧率<select id="timeline-fps"><option value="0">跟随源视频</option><option value="24">24</option><option value="25">25</option><option value="30" selected>30</option><option value="50">50</option><option value="60">60</option></select></label><label>码率<select id="timeline-bitrate"><option value="0">自动</option><option value="2000">2 Mbps</option><option value="4000">4 Mbps</option><option value="8000">8 Mbps</option><option value="16000">16 Mbps</option></select></label></div><div class="timeline-toolbar"><input id="timeline-variant-name" placeholder="方案名称"><button id="timeline-save-variant" type="button" class="quiet">保存为新方案</button><select id="timeline-variants"></select><button id="timeline-load-variant" type="button" class="quiet">载入方案</button><button id="timeline-package" type="button" class="quiet">导出素材包</button></div>';
document.querySelector('.editor-top').after(panel);
// 双缓冲：player 是“当前活动”的视频元素，可能在主元素与预载元素之间切换（见 editor-preview.js），
// 因此必须是可重新赋值的 var，而不是 const。
var player=$('editor-video'),monitor=document.createElement('div');monitor.className='editor-monitor';player.before(monitor);monitor.append(player);monitor.insertAdjacentHTML('beforeend','<div id="editor-caption-preview"></div>');
$('timeline-zoom').addEventListener('input',e=>{timelineZoom=Number(e.target.value);timelineRender()});
$('timeline-split').onclick=timelineSplit;$('timeline-delete').onclick=timelineRemove;$('timeline-undo').onclick=()=>timelineRestore(true);$('timeline-redo').onclick=()=>timelineRestore(false);
$('timeline-apply-range').onclick=()=>{const item=timelineCurrent();if(!item)return;const a=Number($('editor-start').value),b=Number($('editor-end').value),max=timelineMeta.get(item.clipId)?.duration||Infinity;if(!(a>=0&&b>a&&b<=max))return toast('片段范围无效');timelineCheckpoint();item.start=a;item.end=b;timelineRender()};
$('timeline-duplicate').onclick=()=>{const item=timelineCurrent();if(item)timelineAdd(item.clipId,item,item.kind)};
$('timeline-add').onclick=()=>timelineAdd($('timeline-source').value).catch(e=>toast(e.message));
$('timeline-keep-selection')?.addEventListener('click',()=>{try{timelineKeepSelection()}catch(e){toast(e.message)}});
$('timeline-sentences').onclick=()=>{try{timelineApplySentences()}catch(e){toast(e.message)}};
// 加入字幕：两种来源——本地字幕文件，或软件内其他视频转译好的字幕。加入后替换当前视频的字幕并重新载入编辑器。
const subtitleAddDialog=document.createElement('dialog');subtitleAddDialog.className='media-preview-dialog';subtitleAddDialog.innerHTML=`
<div class="section-title"><h2>加入字幕</h2><button type="button" class="quiet" id="subtitle-add-close">关闭</button></div>
<p class="input-hint">选择字幕来源。加入后会替换当前视频的字幕、更新字幕版本，时间线与逐句编辑会自动同步。</p>
<div class="studio-actions"><label class="inline-check"><input type="radio" name="subtitle-source" value="local" checked> 本地字幕文件（SRT/VTT/ASS/SSA）</label><label class="inline-check"><input type="radio" name="subtitle-source" value="builtin"> 软件内转译的字幕</label></div>
<div class="studio-actions"><input type="file" id="subtitle-local-file" accept=".srt,.vtt,.ass,.ssa" hidden><button type="button" class="quiet" id="subtitle-local-pick">选择字幕文件…</button><span class="muted" id="subtitle-local-name">未选择文件</span><select id="subtitle-source-clip" style="flex:1"></select></div>
<div class="studio-actions"><button type="button" class="quiet" id="subtitle-add-cancel">取消</button><button type="button" id="subtitle-add-confirm">加入字幕</button></div>`;
document.body.append(subtitleAddDialog);
$('timeline-add-subtitle').onclick=()=>{
  const choices=(snapshot?.clips||[]).filter(c=>c.srtPath&&c.id!==editorClipId);
  $('subtitle-source-clip').innerHTML='<option value="">选择已转译的视频</option>'+choices.map(c=>`<option value="${esc(c.id)}">${esc(timelineLabel(c.id))}${c.srtPath?' · 已有字幕':''}</option>`).join('');
  subtitleAddDialog.showModal();
};
$('subtitle-add-close').onclick=()=>subtitleAddDialog.close();
$('subtitle-add-cancel').onclick=()=>subtitleAddDialog.close();
$('subtitle-local-pick').onclick=()=>$('subtitle-local-file').click();
$('subtitle-local-file').onchange=e=>{$('subtitle-local-name').textContent=e.target.files[0]?e.target.files[0].name:'未选择文件';};
$('subtitle-add-confirm').onclick=async()=>{
  try{
    if(!editorClipId)throw new Error('请先载入视频');
    const mode=subtitleAddDialog.querySelector('input[name="subtitle-source"]:checked')?.value||'local';
    if(mode==='local'){
      const file=$('subtitle-local-file').files[0];
      if(!file)throw new Error('请先选择本地字幕文件');
      const body=new FormData();body.append('file',file);
      const response=await fetch(`/api/studio/clips/${encodeURIComponent(editorClipId)}/subtitle`,{method:'POST',body});
      if(!response.ok){const data=await response.json().catch(()=>({}));throw new Error(data.error||'字幕读取失败')}
    }else{
      const source=$('subtitle-source-clip').value;
      if(!source)throw new Error('请选择已转译的视频');
      await api(`/studio/clips/${encodeURIComponent(editorClipId)}/transcript-from`,'POST',{sourceClipId:source});
    }
    subtitleAddDialog.close();toast('字幕已加入，正在同步编辑器…');
    await openStudioEditor(editorClipId);
  }catch(error){toast(error.message)}
};
$('timeline-play').onclick=async()=>{try{if(timelinePlaying){timelinePlaying=false;player.pause();return;}await timelineSeek(timelineTime>=timelineTotal()-.1?0:timelineTime,true)}catch(e){toast(e.message)}};
$('timeline-save-variant').onclick=async()=>{try{const name=$('timeline-variant-name').value.trim()||'方案 '+new Date().toLocaleTimeString();await api('/studio/clips/'+encodeURIComponent(editorClipId)+'/variants','POST',{name,project:editorPlan()});timelineVariants();toast('新方案已保存')}catch(e){toast(e.message)}};
$('timeline-load-variant').onclick=async()=>{try{const id=$('timeline-variants').value;if(!id)return;const x=await api('/studio/variants/'+encodeURIComponent(id));editorData.project=x.project;await timelineLoadProject(editorData,editorClipId);toast('方案已载入')}catch(e){toast(e.message)}};
$('timeline-package').onclick=async()=>{try{$('editor-status').textContent='正在整理素材包…';await saveEditorSegments();const result=await api('/studio/clips/'+encodeURIComponent(editorClipId)+'/package','POST',editorPlan());const a=document.createElement('a');a.href='/api/studio/packages/'+encodeURIComponent(result.id)+'/file';a.download='LiveClipper-materials.zip';a.click();$('editor-status').textContent='素材包已生成'}catch(e){toast(e.message);$('editor-status').textContent=e.message}};
// 红色播放头支持按住拖动：按下标尺或播放头后，时间线实时跟随鼠标移动。
function timelineScrubTime(clientX){
  const box=$('timeline-canvas').getBoundingClientRect();
  return Math.max(0,Math.min((clientX-box.left)/timelineZoom,Math.max(0,timelineTotal()-.001)));
}
function timelineScrubStart(e){
  if(e.button!==0||!timelineItems.length)return;
  e.preventDefault();
  const resume=timelinePlaying;timelinePlaying=false;player.pause();
  const target=e.currentTarget,head=$('timeline-playhead');head.classList.add('is-dragging');
  try{target.setPointerCapture(e.pointerId)}catch{}
  const seek=(x,play=false)=>{
    timelineTime=timelineScrubTime(x);head.style.left=timelineTime*timelineZoom+'px';
    if(typeof previewQueueSeek==='function')previewQueueSeek(timelineTime,play);
    else timelineSeek(timelineTime,play).catch(error=>toast(error.message));
  };
  seek(e.clientX);
  const move=event=>{event.preventDefault();seek(event.clientX)};
  const finish=event=>{
    target.removeEventListener('pointermove',move);target.removeEventListener('pointerup',finish);target.removeEventListener('pointercancel',finish);
    head.classList.remove('is-dragging');
    if(resume&&event.type!=='pointercancel')seek(event.clientX??timelineTime*timelineZoom,true);
  };
  target.addEventListener('pointermove',move);target.addEventListener('pointerup',finish);target.addEventListener('pointercancel',finish);
}
$('timeline-ruler').addEventListener('pointerdown',timelineScrubStart);
$('timeline-playhead').addEventListener('pointerdown',timelineScrubStart);
// Playback, visual overlays and animated drag operations live in editor-preview.js.
document.addEventListener('keydown',e=>{if(!document.querySelector('#page-editor.active')||/INPUT|TEXTAREA|SELECT/.test(e.target.tagName))return;if(e.ctrlKey&&e.key.toLowerCase()==='b'){e.preventDefault();timelineSplit()}else if(e.key==='Delete'){e.preventDefault();timelineRemove()}else if(e.ctrlKey&&e.key.toLowerCase()==='z'){e.preventDefault();timelineRestore(!e.shiftKey)}});
document.addEventListener('click',async e=>{
  const hook=e.target.closest('[data-timeline-hook]');if(hook){try{collectEditorSegments();const s=editorSegments[Number(hook.dataset.timelineHook)];if(timelineItems.filter(x=>x.kind==='hook').length>=3)return toast('片头钩子最多 3 句');await timelineAdd(editorClipId,s,'hook',timelineItems.filter(x=>x.kind==='hook').length)}catch(x){toast(x.message)}}
  const event=e.target.closest('[data-add-event]');if(event){const item=editorData?.events.find(x=>x.id===event.dataset.addEvent);if(item)timelineAdd(editorClipId,item,'video').catch(x=>toast(x.message))}
});
if(window.chrome?.webview)window.chrome.webview.addEventListener('message',e=>{const m=e.data;if(m?.type==='files-picked'){const resolve=filePickRequests.get(m.requestId);filePickRequests.delete(m.requestId);resolve?.(m.paths||[])}});
async function pickStudioFiles(){if(!window.chrome?.webview){$('studio-file-input').click();return null;}return new Promise(resolve=>{const requestId=crypto.randomUUID();filePickRequests.set(requestId,resolve);window.chrome.webview.postMessage({type:'pick-files',requestId})})}
async function importPaths(paths){if(!paths?.length)return;const result=await api('/studio/import-paths','POST',{paths,outputDirectory:$('output-folder').value.trim()||null});await finishFileImport(result)}
async function finishFileImport(result){await refresh();await studioLoadLists();timelineAssetChoices();const ids=result.clipIds||[];if(ids.length){if(!editorClipId){await openStudioEditor(ids[0]);for(const id of ids.slice(1))await timelineAdd(id)}else for(const id of ids)await timelineAdd(id)}toast('导入完成：'+result.added+' 个新视频')}
async function importStudioFiles(files){
  if(!files?.length)return;const array=[...files],audio=array.filter(x=>/\.(wav|mp3|m4a|flac|ogg)$/i.test(x.name)),subtitles=array.filter(x=>/\.(srt|vtt|ass|ssa)$/i.test(x.name)),videos=array.filter(x=>/\.(mp4|mkv|mov|flv|webm|m4v|ts)$/i.test(x.name));
  $('studio-import-status').textContent='正在导入，请稍候…';
  try{
    for(const file of audio){const body=new FormData();body.append('file',file);const r=await fetch('/api/assets/import?kind=music&style='+encodeURIComponent('自定义'),{method:'POST',body});if(!r.ok)throw new Error((await r.json()).error||'音频导入失败');}
    if(videos.length){const body=new FormData();[...videos,...subtitles].forEach(x=>body.append('files',x));const r=await fetch('/api/studio/import-files'+($('output-folder').value?'?outputDirectory='+encodeURIComponent($('output-folder').value):''),{method:'POST',body});const result=await r.json();if(!r.ok)throw new Error(result.error||'文件导入失败');await finishFileImport(result)}
    else if(subtitles.length&&editorClipId){const body=new FormData();body.append('file',subtitles[0]);const r=await fetch('/api/studio/clips/'+encodeURIComponent(editorClipId)+'/subtitle',{method:'POST',body});if(!r.ok)throw new Error((await r.json()).error||'字幕导入失败');await openStudioEditor(editorClipId)}
    else if(!audio.length)throw new Error('请拖入视频、字幕或音频文件');
    await studioLoadLists();$('studio-import-status').textContent='导入完成';toast('文件导入完成');
  }catch(e){$('studio-import-status').textContent=e.message;toast(e.message)}
}
const drop=document.createElement('div');drop.className='studio-drop-zone';drop.innerHTML='<strong>拖入视频、字幕或音频文件</strong><span>支持多个文件；视频与同名或同日期字幕会自动匹配。</span><button id="studio-pick-files" class="quiet" type="button">选择文件…</button><input id="studio-file-input" type="file" multiple hidden accept=".mp4,.mkv,.mov,.flv,.webm,.m4v,.ts,.srt,.vtt,.ass,.ssa,.wav,.mp3,.m4a,.flac,.ogg"><span id="studio-import-status" class="muted"></span>';
$('import-form').before(drop);document.body.insertAdjacentHTML('beforeend','<div id="studio-drop-overlay" hidden><strong>松开鼠标导入文件</strong></div>');
$('studio-pick-files').onclick=async()=>{try{await importPaths(await pickStudioFiles())}catch(e){toast(e.message)}};$('timeline-import').onclick=$('studio-pick-files').onclick;
$('studio-file-input').onchange=e=>{importStudioFiles(e.target.files);e.target.value=''};
document.addEventListener('dragover',e=>{if([...e.dataTransfer.types].includes('Files')){e.preventDefault();$('studio-drop-overlay').hidden=false;}});
document.addEventListener('dragleave',e=>{if(!e.relatedTarget)$('studio-drop-overlay').hidden=true});
document.addEventListener('drop',e=>{if(e.dataTransfer.files.length){e.preventDefault();$('studio-drop-overlay').hidden=true;importStudioFiles(e.dataTransfer.files)}},true);
$('editor-music').addEventListener('change',timelineRender);



