const $ = id => document.getElementById(id);
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const secs = n => { n = Math.max(0, Math.floor(Number(n)||0)); return `${String(Math.floor(n/3600)).padStart(2,'0')}:${String(Math.floor(n%3600/60)).padStart(2,'0')}:${String(n%60).padStart(2,'0')}`; };
const short = n => secs(n).slice(3);
const localTime = v => v ? new Date(v).toLocaleTimeString('zh-CN',{hour12:false}) : '—';
const stateText = {Offline:'离线',Connecting:'连接中',Recording:'录制中',Reconnecting:'重新连接',Finalizing:'整理中',Ended:'已结束',Collecting:'等待后录',Materializing:'生成中',Ready:'已完成',Failed:'失败'};
let snapshot = null;
let toastTimer;
let selectedDanmakuSessionId = '';
let selectedHighlightId = '';
let lastHighlightWheel = 0;
let resolvedUser = null;
let uidTimer;
let lastDanmakuRenderKey = '';
// 右侧「直播监控」面板跟随的房间：点击「正在监控」卡片切换；未选择时默认第一张卡。
let selectedRoomId = null;
// 预览页签：clip=高光回放（原有功能），live=所选房间的近实时直播画面。
let liveTab = 'clip';
let liveHls = null, liveHlsRoomId = 0, hlsLoading = null;

// 当前面板跟随的房间快照；没有监控时返回 null。
function currentRoom(){
  const rooms=snapshot?.status?.rooms||[];
  return rooms.find(r=>Number(r.roomId)===Number(selectedRoomId))||rooms[0]||null;
}
// 弹幕记录与热点词的作用域：下拉框手动选场次时优先，否则跟随右侧选中的直播间。
// 选中的直播间还没开始录制（没有 sessionId）时返回空串，避免串到别的直播间的数据。
function danmakuScopeSessionId(){return selectedDanmakuSessionId||currentRoom()?.sessionId||'';}
function danmakuScopeQuery(){
  const id=danmakuScopeSessionId(),hasRooms=(snapshot?.status?.rooms||[]).length>0;
  return id||hasRooms?`?sessionId=${encodeURIComponent(id)}`:'';
}

async function api(path, method='GET', body) {
  const response = await fetch('/api'+path, {method, headers: body ? {'Content-Type':'application/json'} : {}, body: body ? JSON.stringify(body) : undefined});
  if (!response.ok) {
    let payload; try {payload = await response.json();} catch {}
    throw new Error(payload?.error || payload?.detail || `请求失败 (${response.status})`);
  }
  return response.json();
}
function toast(message) {
  $('toast').textContent=message;$('toast').classList.add('visible');
  clearTimeout(toastTimer);toastTimer=setTimeout(()=>$('toast').classList.remove('visible'),3500);
}
function show(page) {
  document.querySelectorAll('.page').forEach(e=>e.classList.toggle('active',e.id==='page-'+page));
  document.querySelectorAll('.nav').forEach(e=>e.classList.toggle('active',e.dataset.page===page));
  $('top-title').textContent={live:'直播',highlights:'高光',recordings:'录播与素材',editor:'剪辑编辑器',audio:'音频库',jobs:'任务',settings:'设置'}[page];
}
document.querySelectorAll('[data-page]').forEach(e=>e.addEventListener('click',()=>show(e.dataset.page)));

async function refresh() {
  try {
    const sessionQuery=danmakuScopeQuery();
    const [status, highlights, clips, sessions, jobs, danmaku, hotwords, ffmpegDownload, videoExports] = await Promise.all([
      api('/status'),api('/highlights'),api('/clips'),api('/sessions'),api('/jobs'),api('/danmaku'+sessionQuery),api('/danmaku/hotwords'+sessionQuery),api('/ffmpeg/download'),api('/video-exports')
    ]);
    snapshot={status,highlights,clips,sessions,jobs,danmaku,hotwords,ffmpegDownload,videoExports};render();
    if(typeof renderEditorChoices==='function')renderEditorChoices();
    if(typeof timelineAssetChoices==='function')timelineAssetChoices();
  } catch (error) { $('top-status').textContent='连接中断'; $('sidebar-state').textContent='连接中断'; }
}
function render() {
  const {status:s,highlights,clips,sessions,jobs,danmaku,hotwords,ffmpegDownload,videoExports}=snapshot;
  const recording=s.session?.state==='Recording' || s.session?.state===2;
  $('sidebar-dot').className='status-dot'+(recording?' on':s.error?' warn':'');
  $('footer-dot').className='status-dot'+(recording?' on':s.error?' warn':'');
  $('sidebar-state').textContent=recording?'正在录制':s.monitoring?'监控中':'未连接';
  $('top-status').textContent=recording?`录制中 · ${secs((Date.now()-Date.parse(s.session.startedAt))/1000)}`:s.session?.state==='Reconnecting'?'正在重连':s.session?.state==='Connecting'?'连接中':s.monitoring?'等待开播':'等待连接';
  $('live-pill').textContent=recording?'● 正在录制':s.session?.state==='Reconnecting'?'重新连接':s.monitoring?'等待开播':'未监控';
  updateMarkButton();
  // 支持同时监控多个直播间：开始按钮一直可用，用于追加新的房间。
  const roomCount=(s.rooms||[]).length;
  $('start-button').hidden=false;
  $('start-button').textContent=roomCount?'添加直播间':'开始监控';
  $('stop-button').hidden=!roomCount;
  $('stop-button').textContent=roomCount>1?'全部停止':'停止监控';
  // 先确定面板跟随的房间再画监控列表，首次渲染就能带上选中态。
  const room=currentRoom();
  if(room)selectedRoomId=Number(room.roomId);
  renderRooms(s);
  $('streamer').textContent=room?.streamer || resolvedUser?.userName || '—';
  $('room-title').textContent=room?.title || resolvedUser?.title || '—';
  $('room-number').textContent=room?`# ${room.roomId}`:'—';
  $('record-state').textContent=room?.state?(stateText[room.state]||room.state):room?(room.isLive?'等待录制':'等待开播'):'等待中';
  $('record-time').textContent=room?.startedAt?secs((Date.now()-Date.parse(room.startedAt))/1000):'00:00:00';
  // 回放缓存按当前选中的直播间显示（bufferSeconds = 该场分片的末段结束 - 首段开始）；
  // 切换直播间时跟着切，没有监控时才回退到全局值。
  const bufferSeconds=room?Number(room.bufferSeconds||0):Number(s.bufferSeconds||0);
  $('buffer-time').textContent=s.bufferUnlimited?short(bufferSeconds):`${short(bufferSeconds)} / ${short(s.bufferLimitSeconds||1200)}`;
  $('highlight-count').textContent=String(room?.sessionId?highlights.filter(h=>h.sessionId===room.sessionId).length:highlights.length);
  $('message-rate').textContent=`${Number(room?.messageRate??s.messageRate??0).toFixed(1)} /s`;
  $('baseline').textContent=`${Number(room?.baseline??s.baseline??0).toFixed(1)} /s`;
  const heatScore=Number(room?.heatScore??s.heatScore??0);
  $('heat-value').textContent=`${Math.round(heatScore*100)}%`;
  $('heat-bar').style.width=`${Math.round(heatScore*100)}%`;
  const heatReason=$('heat-reason');if(heatReason)heatReason.textContent=room?.heatReason||s.heatReason||'';
  $('footer-record').textContent=recording?'录制中':s.monitoring?'监控中':'未录制';
  $('footer-buffer').textContent=`缓存 ${short(bufferSeconds)}`;
  $('footer-ffmpeg').textContent=s.ffmpegAvailable?'FFmpeg 就绪':'FFmpeg 未找到';
  $('footer-version').textContent=s.appVersion?`v${s.appVersion}`:'v—';
  const settingsVersion=$('settings-version');if(settingsVersion)settingsVersion.textContent=s.appVersion?`v${s.appVersion}`:'—';
  if(s.error) $('room-hint').textContent=s.error;
  else $('room-hint').textContent=s.danmakuError?`弹幕连接：${s.danmakuError}`:'开播后会自动开始录制；下播后自动结束。';
  renderHighlights(highlights,clips);
  renderHighlightPreview(highlights,clips);
  renderClips(clips,sessions,jobs,videoExports);
  renderJobs(jobs);
  renderDanmaku(danmaku,hotwords,sessions,s);
  renderFfmpegDownload(ffmpegDownload,s);
}
function renderRooms(status) {
  const list=$('room-list'),count=$('room-count');
  if(!list)return;
  const rooms=status.rooms||[];
  if(count)count.textContent=`${rooms.length} / ${status.maxRooms||8} 个直播间`;
  list.innerHTML=rooms.length?rooms.map(r=>{
    const state=r.state?stateText[r.state]||r.state:r.isLive?'等待录制':'等待开播';
    const recording=r.state==='Recording'||r.state===2;
    const problem=r.error||r.danmakuError||'';
    return `<div class="event-row room-tile${Number(r.roomId)===Number(selectedRoomId)?' selected':''}" data-room-select="${esc(r.roomId)}" title="点击在右侧预览这个直播间"><div><strong>${esc(r.title||('直播间 '+r.roomId))}</strong><small>#${esc(r.roomId)} · ${esc(r.streamer||'—')} · ${esc(state)} · 弹幕 ${Number(r.messageRate||0).toFixed(1)}/s · 热度 ${Math.round(Number(r.heatScore||0)*100)}%${problem?' · '+esc(problem):''}</small>${r.heatReason?`<small class="muted">${esc(r.heatReason)}</small>`:''}</div><div class="right"><div class="actions">${recording?`<button class="text-link" data-room-mark="${esc(r.roomId)}">保存高光</button>`:''}<button class="text-link" data-room-stop="${esc(r.roomId)}">停止</button></div></div></div>`;
  }).join(''):'<p class="empty">尚未监控任何直播间</p>';
}
function renderHighlights(highlights,clips) {
  const html=highlights.map(h=>{
    const clip=clips.find(c=>c.eventId===h.id);
    return `<div class="event-row"><div><strong>${h.source==='manual'?'手动标记':'弹幕高光'} · ${short(h.triggerTime)}</strong><small>${esc(h.id.slice(0,8))} &nbsp; ${esc(stateText[h.state]||h.state)} &nbsp; ${short(h.start)} → ${short(h.end)}</small></div><div class="right"><strong>${h.source==='danmaku'?Math.round(h.score*100)+'%':'★'}</strong>${clip?`<button class="text-link" data-clip="${esc(clip.id)}">预览视频 →</button>`:h.state==='Failed'?`<button class="text-link" data-retry="${esc(h.id)}">重试生成 →</button>`:''}</div></div>`;
  }).join('');
  $('highlight-list').innerHTML=html||'<p class="empty">尚无高光。录制中点击「保存高光」开始。</p>';
  // 直播页的快捷列表只显示当前选中房间的高光，和右侧监控面板保持一致。
  const roomSessionId=currentRoom()?.sessionId;
  const scoped=roomSessionId?highlights.filter(h=>h.sessionId===roomSessionId):highlights;
  $('live-highlights').innerHTML=scoped.map((h,i)=>`<button class="highlight-choice${h.id===selectedHighlightId?' selected':''}" data-select-highlight="${esc(h.id)}" type="button"><span><strong>${i+1}. ${h.source==='manual'?'手动标记':'弹幕高光'}</strong><small>${short(h.start)} → ${short(h.end)} · ${esc(stateText[h.state]||h.state)}</small></span><span class="muted mono">${clips.some(c=>c.eventId===h.id)?'▶':'…'}</span></button>`).join('')||'<p class="empty">尚无高光标记</p>';
}
function renderClips(clips,sessions,jobs,videoExports) {
  $('clip-list').innerHTML=clips.map(c=>{
    const running=jobs.some(j=>j.eventId===c.eventId&&j.type==='transcribe'&&['pending','running'].includes(j.status));
    // 显示名优先用服务端算好的规范名（高光=主播-直播日期-高光时刻[序号]；导入=原文件名），旧数据兜底。
    const title=c.title||((c.path||'').split(/[\\/]/).pop()||'').replace(/\.[^.]+$/,'')||`视频 ${c.eventId.slice(0,8)}`;
    const exports=videoExports.filter(x=>x.clipId===c.id).map(x=>x.status==='completed'?`<a href="/api/video-exports/${encodeURIComponent(x.id)}/video" download="${esc(x.name||`${title}.mp4`)}">${x.resolution?x.resolution+'p':'原始分辨率'} · ${x.fps?x.fps+'fps':'原始帧率'} · ${x.bitrateKbps?x.bitrateKbps+'k':'自动码率'} 下载</a>`:`<span class="muted">导出${x.status==='failed'?'失败':` ${Math.round(x.progress*100)}%`}</span>`).join('');
    return `<div class="clip-row"><div><strong>${esc(title)}</strong><small>${esc(localTime(c.createdAt))} · MP4 · ${c.srtPath?'转录已完成':running?'正在转录':'待转录'}</small>${exports?`<div class="clip-exports">${exports}</div>`:''}</div><div class="right"><div class="actions"><button class="text-link" data-clip="${esc(c.id)}">视频预览与导出</button><button class="text-link" data-edit="${esc(c.id)}">剪辑编辑</button>${c.srtPath?`<button class="text-link" data-transcript="${esc(c.id)}">文字预览与导出</button><button class="text-link" data-transcribe="${esc(c.eventId)}">重新转译</button>`:running?'<span class="muted">转录中…</span>':`<button class="text-link" data-transcribe="${esc(c.eventId)}">开始转录</button>`}<button class="text-link" data-clip-delete="${esc(c.id)}" title="删除这条切片的记录与素材文件">删除</button></div></div></div>`;
  }).join('')||'<p class="empty">尚无导出的切片</p>';
  $('session-list').innerHTML=sessions.map(x=>`<div class="session-row"><div><strong>直播间 ${esc(x.roomId)}</strong><small>${esc(new Date(x.startedAt).toLocaleString('zh-CN'))}</small></div><div class="right" style="display:flex;gap:12px;align-items:center"><small>${esc(stateText[x.state]||x.state)}</small><button class="text-link" data-session-delete="${esc(x.id)}" title="删除这场录制的分片缓存、弹幕记录与生成的高光切片">删除</button></div></div>`).join('')||'<p class="empty">尚无录制会话</p>';
}
function renderDanmaku(danmaku,hotwords,sessions,status) {
  const select=$('danmaku-session');
  const options='<option value="">当前或最近一场</option>'+sessions.map(x=>`<option value="${esc(x.id)}">${esc(new Date(x.startedAt).toLocaleString('zh-CN'))} · 房间 ${esc(x.roomId)}</option>`).join('');
  if(select.innerHTML!==options)select.innerHTML=options;
  select.value=selectedDanmakuSessionId;
  $('danmaku-count').textContent=`最近 ${status.danmakuRecordLimit||1000} 条`;
  const hotScope=$('hotword-scope');
  const scopedSession=sessions.find(x=>x.id===danmakuScopeSessionId());
  const scopeName=scopedSession?`房间 ${scopedSession.roomId} · `:'';
  if(hotScope)hotScope.textContent=hotwords.messageCount?`${scopeName}本场累计 ${hotwords.messageCount} 条弹幕`:'本场累计统计';
  $('hotword-list').innerHTML=hotwords.words.length?hotwords.words.map(x=>`<div class="hotword" title="${esc(x.userCount)} 位观众提及"><strong>${esc(x.text)}</strong><span>×${x.count}</span></div>`).join(''):`<p class="empty">${hotwords.messageCount?'需要多位观众重复提及才会形成热点词':'暂无弹幕热点词'}</p>`;
  const scopeId=danmakuScopeSessionId();
  const renderKey=[scopeId,status.danmakuRecordLimit,status.danmakuAuthorized,danmaku.length,danmaku[0]?.id,danmaku[danmaku.length-1]?.id].join('|');
  if(renderKey!==lastDanmakuRenderKey){
    const list=$('danmaku-list'),oldHeight=list.scrollHeight,oldTop=list.scrollTop,keepPosition=oldTop>4&&lastDanmakuRenderKey.startsWith(`${scopeId}|`);
    list.innerHTML=danmaku.length?danmaku.map(d=>`<div class="danmaku-row"><div class="danmaku-meta"><time class="mono">${esc(localTime(d.timestamp))}</time><strong>${esc(d.userName||'匿名观众')}</strong>${d.type==='danmaku'?'':`<span class="danmaku-type">${esc(d.type)}</span>`}<button type="button" class="quiet danmaku-delete" data-danmaku-delete="${esc(d.id)}" aria-label="删除这条弹幕记录">删除</button></div><span class="danmaku-body">${esc(d.text)}</span></div>`).join(''):`<p class="empty">${scopeId?'这场录制暂无弹幕':status.danmakuAuthorized?'已连接，暂无弹幕':'等待弹幕连接'}</p>`;
    if(keepPosition)list.scrollTop=oldTop+list.scrollHeight-oldHeight;
    lastDanmakuRenderKey=renderKey;
  }
}
function renderFfmpegDownload(download,status) {
  const busy=['downloading','verifying','installing'].includes(download.state);
  $('ffmpeg-download').disabled=busy;
  $('ffmpeg-download').textContent=busy?'正在下载 FFmpeg…':status.ffmpegAvailable?'重新下载 FFmpeg':'在软件内下载 FFmpeg';
  $('ffmpeg-download-status').textContent=({idle:status.ffmpegAvailable?'FFmpeg 已就绪':'未开始下载',downloading:`已下载 ${download.progress}%`,verifying:'正在校验下载文件',installing:'正在安装并检查',completed:'安装完成，可立即使用',failed:`下载失败：${download.error||'未知错误'}`})[download.state]||'未开始下载';
  $('ffmpeg-download-progress').hidden=!busy;
  $('ffmpeg-download-progress').value=download.progress;
  if(download.state==='completed'&&download.path&&document.activeElement!==$('ffmpeg-path'))$('ffmpeg-path').value=download.path;
}
function renderHighlightPreview(highlights,clips) {
  if(!highlights.some(h=>h.id===selectedHighlightId))selectedHighlightId=highlights[0]?.id||'';
  const index=highlights.findIndex(h=>h.id===selectedHighlightId);
  const current=index<0?null:highlights[index];
  const clip=current?clips.find(c=>c.eventId===current.id):null;
  const video=$('highlight-video');
  if(video.dataset.clip!==String(clip?.id||'')){
    video.pause();video.removeAttribute('src');video.load();video.dataset.clip=clip?.id||'';
    if(clip)video.src=`/api/clips/${encodeURIComponent(clip.id)}/video`;
  }
  video.hidden=!clip;$('preview-inner').hidden=!!clip;
  $('highlight-position').textContent=current?`${index+1} / ${highlights.length} · ${current.source==='manual'?'手动高光':'弹幕高光'}`:'暂无高光';
  $('highlight-prev').disabled=highlights.length<2;$('highlight-next').disabled=highlights.length<2;
  $('preview-state').textContent=current?clip?'高光视频已就绪':stateText[current.state]||current.state:'高光预览';
  $('preview-hint').textContent=current?clip?'':`高光 ${index+1} / ${highlights.length} · ${short(current.start)} → ${short(current.end)}`:'保存高光后可在此切换预览';
  document.querySelectorAll('.highlight-choice').forEach(x=>x.classList.toggle('selected',x.dataset.selectHighlight===selectedHighlightId));
}
function changeHighlight(delta) {
  const highlights=snapshot?.highlights||[];if(highlights.length<2)return;
  const index=Math.max(0,highlights.findIndex(h=>h.id===selectedHighlightId));
  selectedHighlightId=highlights[(index+delta+highlights.length)%highlights.length].id;
  renderHighlightPreview(highlights,snapshot.clips);
}
function updateTranscriptExport() {
  const id=$('transcript-dialog').dataset.clip;
  if(!id)return;
  const format=$('transcript-format').value;
  $('transcript-export').href=`/api/clips/${encodeURIComponent(id)}/transcript/export?format=${encodeURIComponent(format)}`;
  $('transcript-export').download=`transcript.${format}`;
}
async function openTranscript(id) {
  const dialog=$('transcript-dialog');dialog.dataset.clip='';
  $('transcript-text').textContent='正在读取转录文字…';$('transcript-meta').textContent='正在读取';
  $('transcript-export').setAttribute('aria-disabled','true');dialog.showModal();
  try {
    const preview=await api(`/clips/${encodeURIComponent(id)}/transcript`);
    const segments=preview.segments||[];
    $('transcript-text').textContent=preview.text||'语音识别完成，但没有识别出文字。';
    $('transcript-meta').textContent=`${segments.length} 段文字${preview.language?' · 语言 '+preview.language:''}`;
    dialog.dataset.clip=id;updateTranscriptExport();$('transcript-export').removeAttribute('aria-disabled');
  } catch(error) {$('transcript-text').textContent=error.message;$('transcript-meta').textContent='读取失败';}
}
function updateVideoExportButton() {
  const original=Number($('video-bitrate').value)===0&&Number($('video-resolution').value)===0&&Number($('video-fps').value)===0;
  $('video-export-button').textContent=original?'下载原始 MP4':'生成导出视频';
  $('video-export-status').textContent=original?'保持原始时可直接下载':'导出将在后台处理，完成后显示下载链接';
}
function openVideo(id) {
  const dialog=$('video-dialog');dialog.dataset.clip=id;
  const player=$('video-player');player.src=`/api/clips/${encodeURIComponent(id)}/video`;
  $('video-bitrate').value='0';$('video-resolution').value='0';$('video-fps').value='0';
  updateVideoExportButton();dialog.showModal();
}
function renderJobs(jobs) {
  // 运行中的转译显示实时耗时；转译任务都提供「重新转译」入口（会中断当前这次并从头再来）。
  $('job-list').innerHTML=jobs.map(j=>{
    const label=j.type==='transcribe'?'语音转写':j.type==='video-export'?'视频导出':j.type==='render'?'成片生成':esc(j.type);
    const statusText=({pending:'等待中',running:'运行中',completed:'已完成',failed:'失败',retried:'无进度，已自动重试',interrupted:'已中断'}[j.status]||j.status);
    const detail=j.error||j.message||statusText;
    const attempt=j.retryCount>0?`（第 ${j.retryCount+1} 次尝试）`:'';
    const elapsed=j.status==='running'&&j.startedAt?` <span class="mono" data-job-elapsed data-started="${esc(j.startedAt)}">已用时 ${secs((Date.now()-Date.parse(j.startedAt))/1000)}</span>`:'';
    const retry=j.type==='transcribe'?`<button class="text-link" type="button" data-job-retranscribe="${esc(j.eventId)}" title="中断这次转译并从头重新开始">重新转译</button>`:'';
    return `<div class="job-row"><div><strong>${label} · ${esc(j.eventId.slice(0,8))}</strong><small>${esc(detail)}${esc(attempt)}${elapsed}</small><div class="job-progress"><div style="width:${Math.max(0,Math.min(100,Math.round(j.progress*100)))}%"></div></div></div><div class="job-right"><span class="mono muted">${Math.round(j.progress*100)}%</span>${retry}</div></div>`;
  }).join('')||'<p class="empty">目前没有后台任务</p>';
}
function updateMarkButton(){
  const again=$('mark-again');
  const button=$('mark-button');
  const reset=()=>{button.disabled=true;button.innerHTML='<span class="ui-icon icon-highlights" aria-hidden="true"></span> 保存高光 <kbd>Ctrl F8</kbd>';if(again)again.hidden=true;};
  if(!snapshot){reset();return;}
  const room=currentRoom();
  if(!room){reset();return;}
  const recording=room.state==='Recording'||room.state===2;
  const pending=recording&&room.sessionId?snapshot.highlights.find(h=>h.sessionId===room.sessionId&&h.source==='manual'&&h.state==='Collecting'):null;
  button.disabled=!recording||!!pending;
  // 面板里的录制时长随选中房间每秒跳动。
  if(room.startedAt)$('record-time').textContent=secs((Date.now()-Date.parse(room.startedAt))/1000);
  if(!pending){button.innerHTML='<span class="ui-icon icon-highlights" aria-hidden="true"></span> 保存高光 <kbd>Ctrl F8</kbd>';if(again)again.hidden=true;return;}
  const remaining=Math.max(0,Math.ceil((Date.parse(room.startedAt)+pending.end*1000-Date.now())/1000));
  // 倒计时前带上主播名，一眼分清是哪个直播间在等后录。
  button.innerHTML=`<span class="ui-icon icon-highlights" aria-hidden="true"></span> ${esc(room.streamer||('直播间 '+room.roomId))} ${remaining?`后录倒计时 ${short(remaining)}`:'正在生成高光…'}`;
  // 后录等待期间按钮本体转为倒计时显示，旁边给出新的手动保存入口，可以直接再开一条。
  if(again){again.hidden=!recording;again.textContent=room.streamer?`再存一条 ${room.streamer} 的高光`:'再存一条高光';}
}
async function mark() {
  const room=currentRoom();if(!room)return;
  if(!(room.state==='Recording'||room.state===2))return;
  try{await api(`/mark?roomId=${encodeURIComponent(room.roomId)}`,'POST');toast(`已标记：保留前 ${snapshot?.status?.manualPreRollSeconds??300} 秒，后录 ${snapshot?.status?.manualPostRollSeconds??120} 秒后生成`);await refresh();}catch(e){toast(e.message)}
}
// ===== 直播画面近实时预览：播放本机正在录制的分片（约 4–8 秒延迟），不额外拉流、不依赖外部网站。 =====
function switchLiveTab(tab){
  liveTab=tab;
  const liveBtn=$('live-tab-live'),clipBtn=$('live-tab-clip');
  if(!liveBtn||!clipBtn)return;
  liveBtn.classList.toggle('active',tab==='live');
  clipBtn.classList.toggle('active',tab==='clip');
  liveBtn.setAttribute('aria-selected',String(tab==='live'));
  clipBtn.setAttribute('aria-selected',String(tab==='clip'));
  $('live-preview').hidden=tab!=='live';
  $('highlight-preview').hidden=tab!=='clip';
  const switcher=document.querySelector('.preview-switch');if(switcher)switcher.hidden=tab!=='clip';
  if(tab==='live')loadLivePreview();
  else{stopLivePreview();$('highlight-video')?.pause();}
}
function stopLivePreview(){if(liveHls){liveHls.destroy();liveHls=null;}liveHlsRoomId=0;}
function showLiveHint(state,note){
  const video=$('live-video');if(video)video.hidden=true;
  const inner=document.querySelector('#live-preview .preview-inner');if(inner)inner.hidden=false;
  if($('live-preview-state'))$('live-preview-state').textContent=state;
  if($('live-preview-note'))$('live-preview-note').textContent=note;
}
// hls.js 已随软件本地打包（wwwroot/lib），离线也能加载。
function ensureHls(){
  if(window.Hls)return Promise.resolve(window.Hls);
  if(hlsLoading)return hlsLoading;
  hlsLoading=new Promise(resolve=>{const script=document.createElement('script');script.src='/lib/hls.min.js';script.onload=()=>resolve(window.Hls||null);script.onerror=()=>resolve(null);document.head.append(script);});
  return hlsLoading;
}
async function loadLivePreview(){
  const room=currentRoom();
  if(!room||liveTab!=='live')return;
  const active=room.state==='Recording'||room.state==='Connecting'||room.state==='Reconnecting';
  if(!active){stopLivePreview();showLiveHint('暂无直播画面',room.isLive?'正在等待录制开始':'该直播间未开播或已结束');return;}
  const Hls=await ensureHls();
  if(liveTab!=='live')return;
  if(!Hls){showLiveHint('播放器组件缺失','请重新安装软件后重试');return;}
  const roomId=Number(room.roomId);
  if(liveHlsRoomId===roomId&&liveHls)return;
  stopLivePreview();liveHlsRoomId=roomId;
  const video=$('live-video');
  video.hidden=false;
  const inner=document.querySelector('#live-preview .preview-inner');if(inner)inner.hidden=true;
  liveHls=new Hls({liveSyncDurationCount:3,maxBufferLength:12,backBufferLength:30,manifestLoadingMaxRetry:2,manifestLoadingRetryDelay:2000});
  liveHls.on(Hls.Events.ERROR,(_,data)=>{
    if(!data?.fatal||!liveHls)return;
    if(data.type===Hls.ErrorTypes.NETWORK_ERROR)liveHls.startLoad();
    else if(data.type===Hls.ErrorTypes.MEDIA_ERROR)liveHls.recoverMediaError();
    else{stopLivePreview();showLiveHint('画面中断','正在等待新的录制分片…');setTimeout(()=>{if(liveTab==='live')loadLivePreview();},4000);}
  });
  liveHls.loadSource(`/api/rooms/${roomId}/live.m3u8`);
  liveHls.attachMedia(video);
  video.play().catch(()=>{});
}
async function resolveUid() {
  const value=$('bili-uid').value.trim();const uid=Number(value);
  if(!Number.isSafeInteger(uid)||uid<=0){$('uid-result').textContent='请输入有效的 B 站 UID';resolvedUser=null;return null;}
  $('uid-result').textContent='正在查询直播间…';
  try{
    const result=await api(`/resolve-uid/${uid}`);
    if($('bili-uid').value.trim()!==value)return null;
    resolvedUser=result;$('room-id').value=result.roomId;
    const already=(snapshot?.status?.rooms||[]).some(r=>Number(r.roomId)===Number(result.roomId));
    $('uid-result').textContent=`${result.userName} · UID ${result.uid} · 房间 ${result.roomId}${result.isLive?' · 正在直播':' · 尚未开播'}${already?' · 这个直播间已在监控中':''}`;
    if(snapshot)render();return result;
  }catch(error){resolvedUser=null;$('uid-result').textContent=error.message;return null;}
}
$('mark-button').addEventListener('click',mark);
// 后录倒计时期间出现的新入口：对当前选中的房间再手动保存一条高光。
$('mark-again')?.addEventListener('click',mark);
$('live-tab-live')?.addEventListener('click',()=>switchLiveTab('live'));
$('live-tab-clip')?.addEventListener('click',()=>switchLiveTab('clip'));
$('bili-uid').addEventListener('input',()=>{clearTimeout(uidTimer);resolvedUser=null;$('room-id').value='';const value=$('bili-uid').value.trim();$('uid-result').textContent=value?'等待识别…':'输入 UID 后自动查询主播和直播间房号；点「识别房间」会直接加入监控。';if(value)uidTimer=setTimeout(resolveUid,650);});
$('resolve-uid').addEventListener('click',async()=>{
  clearTimeout(uidTimer);const user=await resolveUid();if(!user)return;
  // 识别成功后直接加入监控列表，省一次「添加直播间」点击；已在监控时 resolveUid 已提示，不再重复添加。
  const already=(snapshot?.status?.rooms||[]).some(r=>Number(r.roomId)===Number(user.roomId));
  if(already){toast(`直播间 ${user.roomId} 已在监控列表中`);return;}
  $('start-button').click();
});
$('room-id').addEventListener('input',()=>{if(resolvedUser&&Number($('room-id').value)!==resolvedUser.roomId){resolvedUser=null;$('uid-result').textContent='已手动修改房号，将按填写的房号监控。';}});
$('start-button').addEventListener('click',async()=>{let roomId=Number($('room-id').value.trim());if(!Number.isSafeInteger(roomId)||roomId<=0){const user=await resolveUid();roomId=user?.roomId||0;}if(!Number.isSafeInteger(roomId)||roomId<=0){toast('请输入 UID 或有效的直播间房号');return;}try{await api('/start','POST',{roomId,uid:resolvedUser?.roomId===roomId?resolvedUser.uid:null});toast(`已开始监控直播间 ${roomId}`);await refresh();}catch(e){toast(e.message);if(e.message.includes('FFmpeg'))show('settings')}});
$('stop-button').addEventListener('click',async()=>{try{await api('/stop','POST');toast('已停止监控');await refresh();}catch(e){toast(e.message)}});
$('ffmpeg-download').addEventListener('click',async()=>{try{await api('/ffmpeg/download','POST');toast('已开始下载 FFmpeg');await refresh();}catch(e){toast(e.message)}});
$('danmaku-session').addEventListener('change',e=>{selectedDanmakuSessionId=e.target.value;refresh();});
$('danmaku-clear-all').addEventListener('click',async()=>{
  if(!snapshot?.sessions?.length)return toast('暂无可清理的弹幕');
  if(!confirm('清空所选录制会话的全部弹幕记录？已生成的高光素材文件不受影响。'))return;
  try{const response=await fetch('/api/danmaku'+danmakuScopeQuery(),{method:'DELETE'});if(!response.ok)throw new Error('清理失败');const result=await response.json();lastDanmakuRenderKey='';toast(`已删除 ${result.deleted} 条弹幕`);await refresh()}catch(error){toast(error.message)}
});
$('danmaku-clear-global').addEventListener('click',async()=>{
  if(!snapshot?.sessions?.length)return toast('暂无可清理的弹幕');
  if(!confirm('删除所有录制会话的弹幕记录？此操作无法撤销，已生成的高光素材文件不受影响。'))return;
  try{const response=await fetch('/api/danmaku/all-sessions',{method:'DELETE'});if(!response.ok)throw new Error('清理失败');const result=await response.json();lastDanmakuRenderKey='';toast(`已从所有场次删除 ${result.deleted} 条弹幕`);await refresh()}catch(error){toast(error.message)}
});
$('highlight-prev').addEventListener('click',()=>changeHighlight(-1));
$('highlight-next').addEventListener('click',()=>changeHighlight(1));
$('highlight-preview').addEventListener('wheel',e=>{if(!(snapshot?.highlights?.length>1))return;e.preventDefault();const now=Date.now();if(now-lastHighlightWheel<250)return;lastHighlightWheel=now;changeHighlight(e.deltaY>0?1:-1);},{passive:false});
$('close-transcript').addEventListener('click',()=>$('transcript-dialog').close());
$('transcript-format').addEventListener('change',updateTranscriptExport);
$('transcript-dialog').addEventListener('click',e=>{if(e.target===$('transcript-dialog'))e.target.close();});
$('close-video').addEventListener('click',()=>$('video-dialog').close());
$('video-dialog').addEventListener('close',()=>{$('video-player').pause();$('video-player').removeAttribute('src');});
['video-bitrate','video-resolution','video-fps'].forEach(id=>$(id).addEventListener('change',updateVideoExportButton));
$('video-export-button').addEventListener('click',async()=>{
  const id=$('video-dialog').dataset.clip;
  const options={bitrateKbps:Number($('video-bitrate').value),resolution:Number($('video-resolution').value),fps:Number($('video-fps').value)};
  if(options.bitrateKbps===0&&options.resolution===0&&options.fps===0){const clip=(snapshot?.clips||[]).find(x=>x.id===id);const title=clip?.title||((clip?.path||'').split(/[\\/]/).pop()||'').replace(/\.[^.]+$/,'');const link=document.createElement('a');link.href=`/api/clips/${encodeURIComponent(id)}/video`;link.download=title?`${title}.mp4`:'original.mp4';link.click();return;}
  try{await api(`/clips/${encodeURIComponent(id)}/export`,'POST',options);toast('视频导出任务已创建');$('video-dialog').close();await refresh();}catch(error){toast(error.message)}
});
document.addEventListener('keydown',e=>{if(e.ctrlKey&&e.key==='F8'){e.preventDefault();mark();}});
// 一键清空：录制会话、切片、本地导入记录与全部缓存；不碰用户自己的视频与导出成片。
$('clear-recordings')?.addEventListener('click',async()=>{
  if(!confirm('清空全部记录与缓存？将删除所有录制会话、切片、本地导入记录、转写与分片缓存；你的原始视频文件和已导出的成片不受影响。此操作无法恢复。'))return;
  const button=$('clear-recordings');button.disabled=true;
  try{const result=await api('/recordings/all','DELETE');toast(`已清理 ${result.sessions||0} 场录制与全部缓存`);await refresh();if(typeof studioLoadLists==='function')await studioLoadLists();}
  catch(error){toast(error.message)}
  finally{button.disabled=false}
});
document.addEventListener('click',async e=>{
  const message=e.target.closest('button[data-danmaku-delete]');if(message){
    if(!confirm('删除这条弹幕记录？'))return;
    try{const response=await fetch(`/api/danmaku/${encodeURIComponent(message.dataset.danmakuDelete)}`+danmakuScopeQuery(),{method:'DELETE'});if(!response.ok)throw new Error('删除失败');lastDanmakuRenderKey='';toast('弹幕已删除');await refresh()}catch(error){toast(error.message)}
    return;
  }
  const roomStop=e.target.closest('[data-room-stop]');if(roomStop){
    try{await api(`/stop?roomId=${encodeURIComponent(roomStop.dataset.roomStop)}`,'POST');toast('已停止该直播间');await refresh()}catch(error){toast(error.message)}
    return;
  }
  const roomMark=e.target.closest('[data-room-mark]');if(roomMark){
    try{await api(`/mark?roomId=${encodeURIComponent(roomMark.dataset.roomMark)}`,'POST');toast('已保存该直播间的高光');await refresh()}catch(error){toast(error.message)}
    return;
  }
  // 点击监控卡片（非按钮区域）：右侧直播监控面板切换到这个直播间，并显示它的直播画面。
  const tile=e.target.closest('[data-room-select]');
  if(tile&&!e.target.closest('button')){
    selectedRoomId=Number(tile.dataset.roomSelect);
    // 切换直播间后弹幕记录、热点词与回放缓存都跟着走：手动选过的场次在这里让位。
    selectedDanmakuSessionId='';lastDanmakuRenderKey='';
    render();
    switchLiveTab('live');
    refresh();
    return;
  }
  const choice=e.target.closest('[data-select-highlight]');if(choice){selectedHighlightId=choice.dataset.selectHighlight;renderHighlightPreview(snapshot.highlights,snapshot.clips);return;}
  // ===== 记录删除：切片 / 录制会话 / 本地导入。确认后才删，删完刷新两个库的列表。 =====
  const clipDelete=e.target.closest('[data-clip-delete]');
  if(clipDelete){
    if(!confirm('删除这条切片？会同时删除它的记录、转写与导出记录，以及软件目录里的素材文件。'))return;
    try{await api(`/clips/${encodeURIComponent(clipDelete.dataset.clipDelete)}`,'DELETE');toast('切片已删除');await refresh();if(typeof studioLoadLists==='function')await studioLoadLists();}catch(error){toast(error.message)}
    return;
  }
  const sessionDelete=e.target.closest('[data-session-delete]');
  if(sessionDelete){
    if(!confirm('删除这场录制？会连同它的分片缓存、弹幕记录和这场生成的高光切片一起删除，无法恢复。'))return;
    try{await api(`/sessions/${encodeURIComponent(sessionDelete.dataset.sessionDelete)}`,'DELETE');toast('录制记录已删除');await refresh();if(typeof studioLoadLists==='function')await studioLoadLists();}catch(error){toast(error.message)}
    return;
  }
  const localDelete=e.target.closest('[data-local-delete]');
  if(localDelete){
    if(!confirm('删除这条本地导入记录？转写、人声分离缓存与剪辑方案会一并清除；你的原始视频和已导出的成片不会被删除。'))return;
    try{await api(`/studio/videos/${encodeURIComponent(localDelete.dataset.localDelete)}`,'DELETE');toast('本地记录已删除');if(typeof studioLoadLists==='function')await studioLoadLists();await refresh();}catch(error){toast(error.message)}
    return;
  }
  const clip=e.target.closest('button[data-clip]');
  if(clip){openVideo(clip.dataset.clip);return;}
  const transcribe=e.target.closest('[data-transcribe]');if(transcribe){try{await api(`/transcribe/${encodeURIComponent(transcribe.dataset.transcribe)}/restart`,'POST');toast('转写任务已加入队列');await refresh()}catch(error){toast(error.message)}}
  const transcript=e.target.closest('[data-transcript]');if(transcript){await openTranscript(transcript.dataset.transcript);return;}
  const retry=e.target.closest('[data-retry]');if(retry){try{await api(`/highlights/${encodeURIComponent(retry.dataset.retry)}/retry`,'POST');toast('高光视频已生成');await refresh()}catch(error){toast(error.message)}}
  const jobRetry=e.target.closest('[data-job-retranscribe]');if(jobRetry){try{await api(`/transcribe/${encodeURIComponent(jobRetry.dataset.jobRetranscribe)}/restart`,'POST');toast('已中断并重新开始转译');await refresh()}catch(error){toast(error.message)}return;}
});
let cachedSettings;
async function loadSettings(){try{const x=await api('/settings');cachedSettings=x;$('room-id').value=x.roomId||'';$('bili-uid').value=x.bilibiliUid||'';$('storage-root').value=x.storageRoot;$('ffmpeg-path').value=x.ffmpegPath;$('python-path').value=x.pythonPath;$('whisper-model').value=x.whisperModel;$('auto-highlight').checked=x.autoHighlight;$('auto-transcribe').checked=x.autoTranscribe;$('highlight-pre-roll').value=x.highlightPreRollSeconds??200;$('highlight-post-roll').value=x.highlightPostRollSeconds??300;$('manual-pre-roll').value=x.manualPreRollSeconds??300;$('manual-post-roll').value=x.manualPostRollSeconds??120;$('danmaku-limit').value=x.danmakuRecordLimit??1000;$('ai-provider').value=x.aiProvider||'ollama';$('ai-endpoint').value=x.aiEndpoint||'https://api.openai.com/v1';$('ai-model').value=x.aiModel||'qwen2.5:1.5b';$('output-folder').value=x.localOutputRoot||'';$('auto-state').textContent=x.autoHighlight?'已开启':'已关闭';
  if($('bili-cookie'))$('bili-cookie').value=x.bilibiliCookie||'';
  // 后端只在新版本才带上这些字段；缺失时保留界面上的默认值，避免把用户设置写反。
  if($('auto-analyze')&&typeof x.autoAnalyze==='boolean')$('auto-analyze').checked=x.autoAnalyze;
  if($('auto-trim-silence')&&typeof x.autoTrimSilence==='boolean')$('auto-trim-silence').checked=x.autoTrimSilence;
  if($('cleanup-on-exit')&&typeof x.cleanupOnExit==='boolean')$('cleanup-on-exit').checked=x.cleanupOnExit;
  if($('auto-highlight-cooldown')&&Number(x.autoHighlightCooldownSeconds)>0)$('auto-highlight-cooldown').value=x.autoHighlightCooldownSeconds;
  if($('auto-highlight-baseline')&&Number(x.autoHighlightBaselineSeconds)>0)$('auto-highlight-baseline').value=x.autoHighlightBaselineSeconds;
  if($('auto-highlight-comparison')&&Number(x.autoHighlightComparisonSeconds)>0)$('auto-highlight-comparison').value=x.autoHighlightComparisonSeconds;
  if($('auto-highlight-ratio')&&Number(x.autoHighlightBaselineRatio)>0)$('auto-highlight-ratio').value=x.autoHighlightBaselineRatio;
  if($('auto-highlight-release-seconds')&&Number(x.autoHighlightReleaseSeconds)>0)$('auto-highlight-release-seconds').value=x.autoHighlightReleaseSeconds;
  if($('auto-highlight-rebase-seconds')&&Number(x.autoHighlightRebaseSeconds)>0)$('auto-highlight-rebase-seconds').value=x.autoHighlightRebaseSeconds;$('ai-provider').dispatchEvent(new Event('change'))}catch(e){toast(e.message)}}
$('settings-form').addEventListener('submit',async e=>{e.preventDefault();
  // 输入框留空时回到后端默认值，而不是把 0 传过去触发校验失败。
  const num=(id,fallback)=>{const v=Number($(id)?.value);return Number.isFinite(v)&&$(id)?.value.trim()!==''?v:fallback;};
  const body={roomId:Number($('room-id').value)||0,bilibiliUid:Number($('bili-uid').value)||0,storageRoot:$('storage-root').value.trim(),ffmpegPath:$('ffmpeg-path').value.trim()||'ffmpeg',pythonPath:$('python-path').value.trim()||'python',whisperModel:$('whisper-model').value,autoHighlight:$('auto-highlight').checked,autoTranscribe:$('auto-transcribe').checked,highlightPreRollSeconds:Number($('highlight-pre-roll').value),highlightPostRollSeconds:Number($('highlight-post-roll').value),manualPreRollSeconds:Number($('manual-pre-roll').value),manualPostRollSeconds:Number($('manual-post-roll').value),danmakuRecordLimit:Number($('danmaku-limit').value),aiProvider:$('ai-provider').value,aiEndpoint:$('ai-endpoint').value.trim(),aiModel:$('ai-model').value.trim(),localOutputRoot:$('output-folder').value.trim(),bilibiliCookie:$('bili-cookie')?.value.trim()||'',autoAnalyze:$('auto-analyze')?$('auto-analyze').checked:true,autoTrimSilence:$('auto-trim-silence')?$('auto-trim-silence').checked:true,cleanupOnExit:$('cleanup-on-exit')?$('cleanup-on-exit').checked:true,autoHighlightCooldownSeconds:num('auto-highlight-cooldown',60),autoHighlightBaselineSeconds:num('auto-highlight-baseline',600),autoHighlightComparisonSeconds:num('auto-highlight-comparison',120),autoHighlightBaselineRatio:num('auto-highlight-ratio',1.4),autoHighlightReleaseSeconds:num('auto-highlight-release-seconds',200),autoHighlightRebaseSeconds:num('auto-highlight-rebase-seconds',600),autoHighlightReleaseRatio:cachedSettings?.autoHighlightReleaseRatio,autoHighlightMaxExtensionSeconds:cachedSettings?.autoHighlightMaxExtensionSeconds};try{await api('/settings','POST',body);$('settings-result').textContent='已保存。高光时长和弹幕记录条数立即生效；手动修改路径需重启软件。';$('auto-state').textContent=body.autoHighlight?'已开启':'已关闭';toast('设置已保存');await refresh()}catch(error){toast(error.message)}});
// ===== Cookie 自动识别：在软件内扫码登录 =====
function setCookieStatus(text,warn){
  const el=$('cookie-status');
  if(!el)return;
  el.textContent=text;
  el.classList.toggle('warn',warn===true);
}
function detectCookies(){ setCookieStatus('公开源码版未启用浏览器登录检测。请使用软件内登录。',true); }
function loginCookieInApp(){
  if(!window.chrome?.webview){toast('请在 LiveClipper 桌面程序里使用扫码登录');return;}
  setCookieStatus('已打开 B 站登录窗口，请用手机 App 扫码…');
  window.chrome.webview.postMessage({type:'bilibili-login',requestId:crypto.randomUUID()});
}
if(window.chrome?.webview){
  window.chrome.webview.addEventListener('message',e=>{
    const message=e.data;if(message?.type!=='cookie-imported')return;
    if(message.ok){
      if($('bili-cookie')&&message.cookie)$('bili-cookie').value=message.cookie;
      setCookieStatus(message.detail||'登录成功，Cookie 已加密保存',false);
      toast(message.detail||'登录成功，Cookie 已加密保存');
      refresh();
    }
    else setCookieStatus(message.detail||'登录未完成',true);
  });
}
$('cookie-detect')?.addEventListener('click',detectCookies);
$('cookie-login')?.addEventListener('click',loginCookieInApp);
if($('danmaku-resolve-names'))$('danmaku-resolve-names').addEventListener('click',async()=>{
  const button=$('danmaku-resolve-names'),status=$('danmaku-resolve-status');
  button.disabled=true;if(status)status.textContent='正在补全用户名…';
  try{const result=await api('/danmaku/resolve-names?max=300','POST');if(status)status.textContent=result.queued?`已提交 ${result.queued} 位观众，稍后会自动刷新显示`:'没有需要补全的用户名';lastDanmakuRenderKey='';await refresh()}
  catch(error){if(status)status.textContent=error.message;toast(error.message)}
  finally{button.disabled=false}
});
// 任务页运行中转译的耗时每秒刷新一次，不必等 refresh 轮询。
setInterval(()=>{document.querySelectorAll('[data-job-elapsed]').forEach(el=>{const started=Date.parse(el.dataset.started||'');if(started)el.textContent=`已用时 ${secs((Date.now()-started)/1000)}`;});},1000);
loadSettings();refresh();setInterval(refresh,3000);setInterval(updateMarkButton,1000);
