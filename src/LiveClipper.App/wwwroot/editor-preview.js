// The monitor follows the composed timeline, including its audio and editable overlays.
const previewStage=document.createElement('div');previewStage.id='preview-stage';
monitor.prepend(previewStage);previewStage.append(player,$('editor-caption-preview'));player.controls=false;
previewStage.insertAdjacentHTML('beforeend',`<div id="preview-sticker" hidden tabindex="0" role="group" aria-label="表情包：拖动移动，方向键微调"><img draggable="false" alt="表情包"><button type="button" class="sticker-handle sticker-rotate" data-sticker-action="rotate" aria-label="旋转表情包">↻</button><button type="button" class="sticker-handle sticker-resize" data-sticker-action="resize" aria-label="缩放表情包">↘</button></div>`);
monitor.insertAdjacentHTML('beforeend',`<div class="preview-transport"><button type="button" class="quiet" id="preview-play" aria-label="播放编辑后的时间线">▶</button><span id="preview-time" class="mono">00:00 / 00:00</span><input type="range" id="preview-progress" min="0" max="1" step=".01" value="0" aria-label="成片预览进度"><button type="button" class="quiet" id="preview-mute" aria-label="预览静音">🔊</button><button type="button" class="quiet" id="preview-fullscreen" aria-label="全屏预览">⛶</button><label class="preview-toggle"><input type="checkbox" id="preview-captions" checked> 预览字幕</label></div><div class="preview-hint" id="editor-preview-hint">字幕、配乐与表情包随时间线同步预览</div>`);
const previewSticker=$('preview-sticker'),previewImage=previewSticker.querySelector('img'),previewEffects=new Map();
// 双缓冲：第二个视频元素在后台预载下一片段，跳切边界直接切换（零 seek 等待），消除剪气口后的卡顿。
// CSS 的 #preview-stage video 会给它与主播放器相同的铺满样式，两个元素完全重叠。
const playerB=document.createElement('video');playerB.preload='auto';playerB.playsInline=true;playerB.controls=false;playerB.dataset.source='';playerB.style.display='none';
player.after(playerB);
function previewStandby(){return player===playerB?$('editor-video'):playerB;}
/// <summary>把主元素与 standby 一起暂停（导航离开、停止试听等场景）。</summary>
function previewPauseAll(){for(const el of [$('editor-video'),playerB])el.pause();timelinePlaying=false;}
function previewActivate(el){
  if(el===player)return;
  player.pause();player.style.display='none';el.style.display='';player=el;
  previewShape();previewRefresh();
}
let previewArmIndex=-1,previewArmBusy=false;
function previewResetStandby(){previewArmIndex=-1;previewArmBusy=false;playerB.pause();playerB.dataset.source='';playerB.removeAttribute('src');playerB.load();}
function previewResetPlayback(){
  const main=$('editor-video');
  if(player!==main)previewActivate(main);else player.pause();
  previewResetStandby();
}
async function previewArmStandby(nextIndex){
  if(previewArmBusy)return;previewArmBusy=true;
  try{
    const item=timelineItems[nextIndex];if(!item)return;
    const other=previewStandby();if(!other)return;
    previewArmIndex=nextIndex;
    if(other.dataset.source!==item.clipId||other.readyState<1){
      other.dataset.source=item.clipId;
      other.src='/api/clips/'+encodeURIComponent(item.clipId)+'/video';
      // 带超时：服务器无响应或事件丢失时不能让 previewArmBusy 永久卡在 true。
      await new Promise((resolve,reject)=>{
        const timer=setTimeout(()=>reject(new Error('预载超时')),10000);
        other.addEventListener('loadedmetadata',()=>{clearTimeout(timer);resolve();},{once:true});
        other.addEventListener('error',()=>{clearTimeout(timer);reject(new Error('预载失败'));},{once:true});
      });
    }
    // 等 seek 真正完成（3 秒兜底）再标记就位：边界时 standby 必须已停在新段起点并带数据，
    // 否则边界处才发现没就绪，只能回退到对活动元素的慢 seek（稀疏关键帧下会明显冻结）。
    const target=item.start+.001;
    if(Math.abs(other.currentTime-target)>.01){
      const settled=new Promise(resolve=>{const done=()=>{other.removeEventListener('seeked',done);resolve();};other.addEventListener('seeked',done);setTimeout(done,3000);});
      other.currentTime=target;
      await settled;
    }
  }catch{previewArmIndex=-1;}
  finally{previewArmBusy=false;}
}
async function previewBufferedJump(item,sourceTime){
  const other=previewStandby();if(!other||other===player)return false;
  const target=item.start+sourceTime;
  // 等待期间主画面继续播放而不是先暂停：宁可短暂播进气口静音区，也不能把画面冻住
  // （此前先暂停再等，等待本身就造成可见冻结，超时后又走慢 seek 二次冻结）。
  const deadline=Date.now()+1500;
  while(Date.now()<deadline){
    if(other.dataset.source!==item.clipId){previewArmIndex=-1;return false;}
    if(!other.seeking&&other.readyState>=3&&Math.abs(other.currentTime-target)<=.06){
      previewActivate(other);
      // 不等待 play()：数据不足时它的 promise 可能长时间悬挂并卡死过渡状态。
      player.play().catch(()=>{});
      previewArmIndex=-1;
      return true;
    }
    // 播入被剪区域过深仍未就绪时才停住画面，避免把下一段的有效内容播出来。
    if(player.currentTime>item.end+1.2&&!player.paused)player.pause();
    await new Promise(r=>setTimeout(r,50));
  }
  previewArmIndex=-1;
  return false;
}
let previewFrame=0,previewTransition=false,previewMuted=false,previewStickerSelected=false,previewAudioContext=null,previewAnalyser=null,previewVoiceAnalyser=null,previewDuck=1,previewScrubbing=false,previewScrubPlay=false,previewSeekQueue=null,previewSeekBusy=false;
const previewClamp=(n,min,max)=>Math.max(min,Math.min(max,n));
const previewAssetUrl=id=>'/api/assets/'+encodeURIComponent(id)+'/file';
function previewProjectLoaded(){previewStopAudio();previewEffects.forEach(x=>x.audio.remove());previewEffects.clear();previewStickerSelected=false;previewShape();previewRefresh();}
function previewShape(){const meta=timelineMeta.get(timelineItems[0]?.clipId),w=meta?.width||player.videoWidth||16,h=meta?.height||player.videoHeight||9;previewStage.style.aspectRatio=w+' / '+h;monitor.style.maxWidth=w<h?(390*w/h)+'px':'';}
function previewItem(){return timelineCurrent()||timelineItems[timelineIndex];}
function previewRefresh(){
  const item=previewItem(),caption=$('editor-caption-preview'),total=timelineTotal(),h=previewStage.clientHeight||300;
  const local=item?previewClamp(timelineTime-timelineOffset(item.id),0,item.end-item.start):0;
  $('preview-time').textContent=short(timelineTime)+' / '+short(total);$('preview-progress').max=String(Math.max(.01,total));if(!previewScrubbing)$('preview-progress').value=String(timelineTime);
  $('preview-play').textContent=timelinePlaying?'❚❚':'▶';$('timeline-play').textContent=timelinePlaying?'❚❚ 暂停时间线':'▶ 播放时间线';
  $('timeline-playhead').style.left=timelineTime*timelineZoom+'px';$('timeline-time').textContent=secs(timelineTime)+' / '+secs(total);
  const segments=item?.clipId===editorClipId?editorSegments:timelineMeta.get(item?.clipId)?.transcript?.segments||[],sourceTime=(item?.start||0)+local;
  const active=segments.filter(s=>sourceTime>=s.start&&sourceTime<s.end&&(item?.kind==='hook'||item?.clipId!==editorClipId||s.selected!==false));
  // 预览字幕与“烧进成片”解耦：勾选预览字幕就能在播放器里直接看到字幕效果。
  caption.textContent=($('preview-captions')?.checked??true)?active.map(s=>s.text).join('\n'):'';
  const size=Number($('timeline-fontsize').value)*h/288,margin=Number($('advanced-margin').value)*h/288;
  caption.style.fontFamily='"'+$('timeline-font').value+'",sans-serif';caption.style.fontSize=size+'px';caption.style.color=$('timeline-color').value;
  const outline=Number($('advanced-outline').value)*h/288;caption.style.webkitTextStroke=outline+'px #000';caption.style.paintOrder='stroke fill';caption.style.textShadow=outline?'0 1px 2px #000':'none';
  const position=$('timeline-subpos').value;caption.style.top=position==='top'?margin+'px':position==='middle'?'50%':'auto';caption.style.bottom=position==='bottom'?margin+'px':'auto';caption.style.transform=position==='middle'?'translateY(-50%)':'none';
  const fraction=item?local/(item.end-item.start):0,zoom=item?.motion==='push'?1+Math.max(.12,(item.zoom||1)-1)*fraction:item?.zoom||1;
  const shake=item?.motion==='shake'?h*.009:0;player.style.transform=`translate(${Math.sin(local*35)*shake}px,${Math.cos(local*29)*shake}px) scale(${zoom})`;
  player.style.filter=({warm:'sepia(.18) saturate(1.15)',cool:'sepia(.12) hue-rotate(165deg)',mono:'grayscale(1)',vivid:'saturate(1.3) contrast(1.06)'})[item?.filter]||'none';
  const sticker=audioAssets.find(x=>x.id===item?.stickerAssetId&&x.kind==='sticker');previewSticker.hidden=!sticker;
  if(sticker){const src=previewAssetUrl(sticker.id);if(previewImage.getAttribute('src')!==src)previewImage.src=src;previewSticker.style.left=(item.stickerX??.72)*100+'%';previewSticker.style.top=(item.stickerY??.12)*100+'%';previewSticker.style.width=(item.stickerWidth??.22)*100+'%';previewSticker.style.transform='rotate('+(item.stickerRotation||0)+'deg)';}
  previewSticker.classList.toggle('is-selected',previewStickerSelected&&!previewSticker.hidden);
  $('editor-preview-hint').textContent=sticker?'点击表情包：拖动移动 · 右下角缩放 · 上方手柄旋转 · 方向键微调':'字幕、配乐与表情包随时间线同步预览';
}
function previewAudioLoad(audio,url){
  if(audio.getAttribute('src')!==url){audio.src=url;audio.load();}if(audio.readyState>=1)return Promise.resolve(true);
  return new Promise(resolve=>{let timer;const finish=ok=>{clearTimeout(timer);audio.removeEventListener('loadedmetadata',ready);audio.removeEventListener('error',failed);resolve(ok);},ready=()=>finish(true),failed=()=>finish(false);audio.addEventListener('loadedmetadata',ready);audio.addEventListener('error',failed);timer=setTimeout(failed,8000);});
}
function previewEffectEvents(){const events=[];const global=$('editor-effect').value;if(global)events.push({key:'global:'+global,id:global,at:Number($('editor-effect-at').value)||0});let offset=0;for(const item of timelineItems){if(item.soundAssetId)events.push({key:item.id+':'+item.soundAssetId,id:item.soundAssetId,at:offset});offset+=item.end-item.start;}return events;}
function previewGetEffect(event){let entry=previewEffects.get(event.key);if(!entry){const audio=document.createElement('audio');audio.hidden=true;audio.preload='auto';monitor.append(audio);entry={audio,at:event.at,failed:false};previewEffects.set(event.key,entry);entry.ready=previewAudioLoad(audio,previewAssetUrl(event.id)).then(ok=>{entry.failed=!ok;return ok;});}entry.at=event.at;return entry;}
async function previewPrepareAudio(item,time){
  const loads=[],music=$('editor-music-preview'),vocals=$('editor-vocals-preview'),musicId=$('editor-music').value;
  if(musicId){music.loop=true;loads.push(previewAudioLoad(music,previewAssetUrl(musicId)));}else music.pause();
  const stems=timelineMeta.get(item.clipId)?.stems;
  if($('editor-voice-mode').value==='vocals'&&item.kind!=='insert'&&stems)loads.push(previewAudioLoad(vocals,'/api/studio/clips/'+encodeURIComponent(item.clipId)+'/stems/vocals'));else vocals.pause();
  for(const event of previewEffectEvents())if(event.at<=time+3)loads.push(previewGetEffect(event).ready);
  await Promise.all(loads);previewSyncAudio(false);previewRefresh();
}
function previewStopAudio(){for(const id of ['editor-music-preview','editor-vocals-preview','editor-effect-preview'])$(id).pause();previewEffects.forEach(x=>x.audio.pause());}
function previewAudioAt(audio,time,playing,volume,loop=false){
  audio.volume=previewMuted?0:previewClamp(volume,0,1);if(audio.readyState<1||!Number.isFinite(audio.duration)||audio.duration<=0)return;
  let target=loop?((time%audio.duration)+audio.duration)%audio.duration:time;
  if(target<0||target>=audio.duration){audio.pause();return;}
  if(Math.abs(audio.currentTime-target)>.14)audio.currentTime=target;
  if(playing&&audio.paused&&!audio.dataset.playFailed)audio.play().catch(()=>{audio.dataset.playFailed='1';toast('音频无法播放，请检查对应素材');});else if(!playing)audio.pause();
}
const previewElementAnalysers=new Map();
function previewEnsureAnalyser(audio){
  if(!previewAudioContext||previewElementAnalysers.has(audio))return;
  const source=previewAudioContext.createMediaElementSource(audio),analyser=previewAudioContext.createAnalyser();analyser.fftSize=256;
  source.connect(analyser);analyser.connect(previewAudioContext.destination);
  previewElementAnalysers.set(audio,analyser);
}
function previewInitAudioGraph(){
  if(previewAudioContext){previewAudioContext.resume().catch(()=>{});previewEnsureAnalyser(player);return;}
  try{
    previewAudioContext=new AudioContext();
    previewEnsureAnalyser($('editor-video'));previewEnsureAnalyser(playerB);
    const voiceSource=previewAudioContext.createMediaElementSource($('editor-vocals-preview')),voiceAnalyser=previewAudioContext.createAnalyser();voiceAnalyser.fftSize=256;
    voiceSource.connect(voiceAnalyser);voiceAnalyser.connect(previewAudioContext.destination);previewVoiceAnalyser=voiceAnalyser;
    previewAudioContext.resume().catch(()=>{});
  }catch{previewAudioContext=null;}
}
function previewSyncAudio(playing=timelinePlaying&&!player.paused){
  const item=previewItem();if(!item)return;const stems=timelineMeta.get(item.clipId)?.stems,voiceMode=$('editor-voice-mode').value==='vocals'&&item.kind!=='insert'&&!!stems;
  player.muted=previewMuted||voiceMode;player.volume=previewClamp(Number($('editor-voice-volume').value),0,1);
  const vocals=$('editor-vocals-preview');if(voiceMode)previewAudioAt(vocals,item.start+timelineTime-timelineOffset(item.id),playing,Number($('editor-voice-volume').value));else vocals.pause();
  let duck=1;const analyser=voiceMode?previewVoiceAnalyser:previewElementAnalysers.get(player);
  if($('advanced-duck').checked&&analyser&&playing){const samples=new Float32Array(analyser.fftSize);analyser.getFloatTimeDomainData(samples);const rms=Math.sqrt(samples.reduce((n,x)=>n+x*x,0)/samples.length);duck=rms>.015?Math.max(.25,.015/rms):1;}
  previewDuck+=(duck-previewDuck)*(duck<previewDuck?.3:.06);
  const fade=previewClamp(timelineTotal()-timelineTime,0,1);if($('editor-music').value)previewAudioAt($('editor-music-preview'),timelineTime,playing,Number($('editor-music-volume').value)*previewDuck*fade,true);
  const events=previewEffectEvents(),keys=new Set(events.map(x=>x.key));for(const event of events){if(event.at>timelineTime+3)continue;const entry=previewGetEffect(event);if(!entry.failed)previewAudioAt(entry.audio,timelineTime-event.at,playing,Number($('editor-effect-volume').value));}
  previewEffects.forEach((x,key)=>{if(!keys.has(key))x.audio.pause();});
}
function previewStart(){previewInitAudioGraph();previewEffects.forEach(x=>delete x.audio.dataset.playFailed);previewSyncAudio(true);cancelAnimationFrame(previewFrame);previewFrame=requestAnimationFrame(previewTick);previewRefresh();}
async function previewNext(){
  if(previewTransition||!timelinePlaying)return;previewTransition=true;const next=timelineIndex+1;
  try{if(next<timelineItems.length)await timelineSeek(timelineOffset(timelineItems[next].id),true);else{timelinePlaying=false;player.pause();timelineTime=timelineTotal();previewStopAudio();previewRefresh();}}catch(e){timelinePlaying=false;player.pause();toast(e.message);}finally{previewTransition=false;}
}
let previewLastFrameTime=-1,previewLastAdvance=0;
function previewTick(){
  // 画面推进时间戳始终跟踪（含过渡期间），作为全局卡死检测的基准。
  if(timelinePlaying&&player.currentTime!==previewLastFrameTime){previewLastFrameTime=player.currentTime;previewLastAdvance=Date.now();}
  if(timelinePlaying&&!timelineLoading&&!previewTransition){const item=timelineItems[timelineIndex];if(item){
    // 跳切后 WebView2 偶发把元素留在暂停态而时间线仍标记为播放中：主动恢复，避免画面卡死。
    if(player.paused&&!player.ended&&!player.seeking)player.play().catch(()=>{});
    // 看门狗：画面 2 秒没有前进（卡死在边界帧）或 seek 悬挂 3 秒（seeked 事件丢失）时直接原地救活。
    // 不走 timelineSeek 全流程——那会再次进入边界等待/慢 seek，造成二次冻结。
    if(!player.paused&&!player.ended&&((!player.seeking&&Date.now()-previewLastAdvance>2000)||(player.seeking&&Date.now()-previewLastAdvance>3000))){
      previewLastAdvance=Date.now();
      const cur=timelineItems[timelineIndex];
      if(cur){
        const offset=timelineOffset(cur.id);
        const next=Math.min(cur.start+Math.min(timelineTime+.05-offset,cur.end-cur.start-.001),cur.end-.001);
        if(Number.isFinite(next)&&next>player.currentTime)player.currentTime=next;
        player.play().catch(()=>{});
      }
    }
    // 双缓冲预载：剩余不足 2.5 秒（超短片段等于一进段就预载）且下一段同源时，
    // 提前把 standby 元素定位到下一段起点——录播关键帧稀疏，seek+缓冲需要尽量多的提前量。
    const next=timelineItems[timelineIndex+1];
    if(next&&next.clipId===item.clipId&&!player.seeking){
      const remain=item.end-player.currentTime;
      if(remain<2.5&&remain>0&&previewArmIndex!==timelineIndex+1)previewArmStandby(timelineIndex+1);
    }else if(previewArmIndex===timelineIndex+1)previewArmIndex=-1;
    timelineTime=timelineOffset(item.id)+previewClamp(player.currentTime-item.start,0,item.end-item.start);previewRefresh();previewSyncAudio();if(player.currentTime>=item.end-.025||player.ended)previewNext();}}
  // 过渡卡死兜底：previewNext/跳切的等待悬挂超过 3 秒时强制解锁过渡状态，交回主循环与看门狗。
  // 不要求 timelinePlaying——悬挂期间它可能已被其他事件处理器置 false，那种情况下同样需要解锁，
  // 否则界面会永久冻结（录屏中卡在 00:15 的形态）。
  if(previewTransition&&Date.now()-previewLastAdvance>3000)previewTransition=false;
  if(timelinePlaying||previewTransition)previewFrame=requestAnimationFrame(previewTick);
}
for(const element of [$('editor-video'),playerB]){
  element.addEventListener('pause',()=>{if(!player.paused||timelineLoading||previewTransition)return;if(player.ended&&timelinePlaying){previewNext();return;}timelinePlaying=false;previewStopAudio();previewRefresh();});
  element.addEventListener('ended',()=>{if(timelinePlaying)previewNext();});
  element.addEventListener('play',()=>{if(timelinePlaying)previewStart();});
  element.addEventListener('seeked',()=>{if(!timelineLoading){previewRefresh();previewSyncAudio();}});
  element.addEventListener('loadedmetadata',()=>{previewShape();previewRefresh();});
}
new ResizeObserver(previewRefresh).observe(previewStage);
$('preview-play').onclick=()=>{previewInitAudioGraph();$('timeline-play').click();};
$('preview-mute').onclick=()=>{previewMuted=!previewMuted;$('preview-mute').textContent=previewMuted?'🔇':'🔊';$('preview-mute').setAttribute('aria-pressed',String(previewMuted));previewSyncAudio();};
$('preview-fullscreen').onclick=()=>{if(document.fullscreenElement)document.exitFullscreen();else monitor.requestFullscreen().catch(e=>toast(e.message));};
async function previewQueueSeek(time,play=false){previewSeekQueue={time,play};if(previewSeekBusy)return;previewSeekBusy=true;try{while(previewSeekQueue){const next=previewSeekQueue;previewSeekQueue=null;await timelineSeek(next.time,next.play);}}catch(e){toast(e.message);}finally{previewSeekBusy=false;}}
$('preview-progress').onpointerdown=()=>{previewScrubbing=true;previewScrubPlay=timelinePlaying;player.pause();};
$('preview-progress').oninput=e=>previewQueueSeek(Number(e.target.value),false);
$('preview-progress').onchange=e=>{previewScrubbing=false;previewQueueSeek(Number(e.target.value),previewScrubPlay);};
// pointercancel 不会总派发到 range 输入框上，指针离开窗口时 previewScrubbing 会一直是 true，
// 进度条从此不再跟随播放。这里用一个全局兜底：松开鼠标/指针离开后再收尾。
// 收尾放在 change 事件之后，保证「松手后继续播放」的行为不受影响。
const previewScrubEnd=()=>{if(!previewScrubbing)return;previewScrubbing=false;previewScrubPlay=false;previewRefresh();};
window.addEventListener('pointerup',()=>setTimeout(previewScrubEnd,0));
window.addEventListener('blur',previewScrubEnd);
previewStage.addEventListener('pointerdown',e=>{if(!e.target.closest('#preview-sticker')){previewStickerSelected=false;previewRefresh();}});
previewSticker.addEventListener('pointerdown',e=>{
  if(e.button!==0)return;const item=previewItem();if(!item?.stickerAssetId)return;e.preventDefault();player.pause();previewStickerSelected=true;previewSticker.focus();previewRefresh();
  const action=e.target.closest('[data-sticker-action]')?.dataset.stickerAction||'move',rect=previewStage.getBoundingClientRect(),initial={x:item.stickerX??.72,y:item.stickerY??.12,width:item.stickerWidth??.22,rotation:item.stickerRotation||0},ratio=previewImage.naturalHeight/previewImage.naturalWidth||1;
  const cx=rect.left+(initial.x+initial.width/2)*rect.width,cy=rect.top+initial.y*rect.height+initial.width*rect.width*ratio/2,angle=Math.atan2(e.clientY-cy,e.clientX-cx),distance=Math.hypot(e.clientX-cx,e.clientY-cy),startX=e.clientX,startY=e.clientY;let changed=false;
  const constrain=()=>{item.stickerX=previewClamp(item.stickerX,0,Math.max(0,1-item.stickerWidth));item.stickerY=previewClamp(item.stickerY,0,Math.max(0,1-item.stickerWidth*rect.width*ratio/rect.height));};
  previewSticker.setPointerCapture(e.pointerId);
  const move=event=>{if(!changed&&Math.hypot(event.clientX-startX,event.clientY-startY)<3)return;if(!changed){timelineCheckpoint();changed=true;}
    if(action==='move'){item.stickerX=initial.x+(event.clientX-startX)/rect.width;item.stickerY=initial.y+(event.clientY-startY)/rect.height;item.stickerWidth=initial.width;constrain();}
    else if(action==='resize'){item.stickerWidth=previewClamp(initial.width*Math.hypot(event.clientX-cx,event.clientY-cy)/Math.max(1,distance),.05,Math.min(1,rect.height/(rect.width*ratio)));item.stickerX=(cx-rect.left)/rect.width-item.stickerWidth/2;item.stickerY=(cy-rect.top)/rect.height-item.stickerWidth*rect.width*ratio/(2*rect.height);constrain();}
    else{const degree=initial.rotation+(Math.atan2(event.clientY-cy,event.clientX-cx)-angle)*180/Math.PI;item.stickerRotation=Math.round(((degree+540)%360)-180);}
    previewRefresh();advancedEffectSelection();};
  const finish=event=>{previewSticker.removeEventListener('pointermove',move);previewSticker.removeEventListener('pointerup',finish);previewSticker.removeEventListener('pointercancel',finish);if(event.type==='pointercancel'&&changed){Object.assign(item,{stickerX:initial.x,stickerY:initial.y,stickerWidth:initial.width,stickerRotation:initial.rotation});timelineHistory.pop();}timelineRender();};
  previewSticker.addEventListener('pointermove',move);previewSticker.addEventListener('pointerup',finish);previewSticker.addEventListener('pointercancel',finish);
});
previewSticker.addEventListener('keydown',e=>{const item=previewItem();if(!item)return;if(!['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','Delete','Backspace','Escape'].includes(e.key))return;e.preventDefault();e.stopPropagation();if(e.key==='Escape'){previewStickerSelected=false;previewRefresh();return;}timelineCheckpoint();if(e.key==='Delete'||e.key==='Backspace')item.stickerAssetId=null;else{const delta=e.shiftKey?10:1;item.stickerX=previewClamp((item.stickerX??.72)+(e.key==='ArrowLeft'?-delta:e.key==='ArrowRight'?delta:0)/previewStage.clientWidth,0,1);item.stickerY=previewClamp((item.stickerY??.12)+(e.key==='ArrowUp'?-delta:e.key==='ArrowDown'?delta:0)/previewStage.clientHeight,0,1);}timelineRender();});
document.addEventListener('input',e=>{if(e.target.closest('#editor-segments')){try{collectEditorSegments();timelineRender();editorMarkDirty();}catch{}}else if(e.target.matches('#editor-burn,#preview-captions,#timeline-font,#timeline-fontsize,#timeline-color,#timeline-subpos,#advanced-outline,#advanced-margin,#advanced-duck,#editor-voice-volume,#editor-music-volume,#editor-effect-volume')){previewRefresh();previewSyncAudio();if(e.target.id!=='preview-captions')editorMarkDirty();}});
for(const id of ['editor-music','editor-effect','editor-effect-at','editor-voice-mode'])$(id).addEventListener('change',()=>{if(previewItem())previewPrepareAudio(previewItem(),timelineTime).then(()=>{previewSyncAudio();timelineRender();}).catch(e=>toast(e.message));});
const addSubtitle=document.createElement('button');addSubtitle.type='button';addSubtitle.className='quiet';addSubtitle.id='editor-add-subtitle';addSubtitle.textContent='新增字幕';$('editor-save-subtitles').before(addSubtitle);
addSubtitle.onclick=()=>{const item=previewItem();if(!item)return toast('先载入视频');if(item.clipId!==editorClipId)return toast('请先选择主视频的片段，再新增字幕');try{collectEditorSegments();}catch(e){return toast(e.message);}const start=timelineRound(item.start+previewClamp(timelineTime-timelineOffset(item.id),0,item.end-item.start-.1)),end=timelineRound(Math.min(start+2,timelineMeta.get(item.clipId)?.duration||item.end));if(end<=start)return;editorSegments.push({start,end,text:'输入字幕',selected:true});$('editor-burn').checked=true;renderEditorSegments();timelineRender();const input=document.querySelector('[data-segment-text="'+(editorSegments.length-1)+'"]');input.focus();input.select();};
async function previewEditorRange(){const item=previewItem();if(!item)return;await timelineSeek(timelineOffset(item.id),true);editorListenUntil=item.end;}
async function previewAuditionSegment(index){collectEditorSegments();const sentence=editorSegments[index];if(!sentence)return;const item=timelineItems.find(x=>x.clipId===editorClipId&&sentence.start<x.end&&sentence.end>x.start);if(!item)return toast('这句字幕不在时间线内，请先加入对应片段');await timelineSeek(timelineOffset(item.id)+Math.max(0,sentence.start-item.start),true);editorListenUntil=Math.min(item.end,sentence.end);}
// Keep a floating drag image and animate the linked tracks into the candidate slot.
const timelineDragTrack=$('timeline-video-track');
timelineDragTrack.addEventListener('pointerdown',e=>{
  const node=e.target.closest('[data-timeline-item]');if(!node||e.button!==0)return;e.preventDefault();const item=timelineItems.find(x=>x.id===node.dataset.timelineItem);if(!item)return;player.pause();
  const original=cloneTimeline(),future=timelineFuture.slice(),startX=e.clientX,startY=e.clientY,trim=e.target.dataset.trim,duration=timelineMeta.get(item.clipId)?.duration||item.end;let changed=false,ghost=null,slot=null,targetIndex=timelineItems.indexOf(item),lastPointer=e,scrollFrame=0;
  timelineSelection=item.id;document.querySelectorAll('[data-timeline-item]').forEach(x=>x.classList.toggle('selected',x.dataset.timelineItem===item.id));advancedEffectSelection();
  timelineDragTrack.setPointerCapture(e.pointerId);
  const shifted=()=>document.querySelectorAll('[data-timeline-item],[data-timeline-parent]');
  const paint=()=>{if(!ghost)return;const rect=$('timeline-canvas').getBoundingClientRect(),at=previewClamp((lastPointer.clientX-rect.left)/timelineZoom,0,timelineTotal()),rest=timelineItems.filter(x=>x.id!==item.id);let offset=0;targetIndex=rest.length;for(let i=0;i<rest.length;i++){const length=rest[i].end-rest[i].start;if(at<offset+length/2){targetIndex=i;break;}offset+=length;}const order=rest.slice();order.splice(targetIndex,0,item);let cursor=0;const positions=new Map();for(const x of order){positions.set(x.id,cursor);cursor+=x.end-x.start;}shifted().forEach(x=>{const id=x.dataset.timelineItem||x.dataset.timelineParent;x.style.transform='translateX('+((positions.get(id)-timelineOffset(id))*timelineZoom)+'px)';x.classList.toggle('drag-origin',id===item.id);});slot.style.left=positions.get(item.id)*timelineZoom+'px';slot.style.width=(item.end-item.start)*timelineZoom+'px';ghost.style.left=(lastPointer.clientX-35)+'px';ghost.style.top=(lastPointer.clientY-20)+'px';};
  const autoScroll=()=>{if(!ghost)return;const scroll=$('timeline-scroll'),rect=scroll.getBoundingClientRect(),edge=45;let delta=0;if(lastPointer.clientX<rect.left+edge)delta=-Math.min(16,(rect.left+edge-lastPointer.clientX)/3);else if(lastPointer.clientX>rect.right-edge)delta=Math.min(16,(lastPointer.clientX-rect.right+edge)/3);if(delta){scroll.scrollLeft+=delta;paint();}scrollFrame=requestAnimationFrame(autoScroll);};
  const move=event=>{lastPointer=event;if(!changed&&Math.hypot(event.clientX-startX,event.clientY-startY)<4)return;if(!changed){timelineCheckpoint();changed=true;panel.classList.add('timeline-is-dragging');if(!trim){ghost=node.cloneNode(true);ghost.removeAttribute('data-timeline-item');ghost.className+=' timeline-drag-ghost';ghost.style.width=previewClamp(node.clientWidth,95,320)+'px';document.body.append(ghost);slot=document.createElement('div');slot.className='timeline-drop-slot';timelineDragTrack.append(slot);autoScroll();}}
    if(trim){const old=original.find(x=>x.id===item.id),delta=(event.clientX-startX)/timelineZoom;if(trim==='start')item.start=timelineRound(previewClamp(old.start+delta,0,item.end-.05));else item.end=timelineRound(previewClamp(old.end+delta,item.start+.05,duration));timelineTime=timelineOffset(item.id);timelineRender();}else paint();};
  const finish=event=>{
    cancelAnimationFrame(scrollFrame);timelineDragTrack.removeEventListener('pointermove',move);timelineDragTrack.removeEventListener('pointerup',finish);timelineDragTrack.removeEventListener('pointercancel',finish);panel.classList.remove('timeline-is-dragging');
    if(event.type==='pointercancel'&&changed){timelineItems=original;timelineHistory.pop();timelineFuture=future;}else if(changed&&!trim){timelineItems=timelineItems.filter(x=>x.id!==item.id);timelineItems.splice(targetIndex,0,item);}
    slot?.remove();timelineRender();
    if(ghost){const destination=document.querySelector('.timeline-clip[data-timeline-item="'+item.id+'"]')?.getBoundingClientRect();if(destination&&!matchMedia('(prefers-reduced-motion: reduce)').matches){const animation=ghost.animate([{left:ghost.style.left,top:ghost.style.top,opacity:.95},{left:destination.left+'px',top:destination.top+'px',opacity:0}],{duration:180,easing:'ease-out'});animation.onfinish=()=>ghost.remove();}else ghost.remove();}
    timelineSelect(item.id,true).catch(error=>toast(error.message));
  };
  timelineDragTrack.addEventListener('pointermove',move);timelineDragTrack.addEventListener('pointerup',finish);timelineDragTrack.addEventListener('pointercancel',finish);
});
previewRefresh();
// 编辑器仪表盘布局（按用户示意图）：三栏两行，所有面板平铺填满窗口（无整页滚动）。
// 左栏（最宽）= 大预览 + 剪辑时间线（「当前视频」选择行放在时间线卡片顶部）；
// 中栏 = 字幕逐句编辑 + 字幕事件；右栏 = 声音与成片（含已生成成片，整卡滚轮滚动）+ 剪辑建议与精剪效果。
// 面板内部列表（逐句/事件/特效）自己滚动，面板本身不滚。
(function buildEditorDashboard(){
  const workspace=$('editor-workspace');if(!workspace||workspace.dataset.dashboard)return;
  workspace.dataset.dashboard='1';
  const title=name=>[...workspace.querySelectorAll(':scope > .section-title')].find(x=>x.textContent.includes(name));
  const segTitle=title('字幕逐句编辑'),evtTitle=title('字幕事件'),audioTitle=title('声音与成片'),renderTitle=title('已生成成片');
  const advanced=workspace.querySelector(':scope > .advanced-panel');
  const editorTop=workspace.querySelector(':scope > .editor-top');
  const timeline=workspace.querySelector(':scope > .timeline-panel');
  const cell=area=>{
    const d=document.createElement('div');d.className='dash-cell';d.style.gridArea=area;d.dataset.panelKey='editor-'+area;
    d.classList.add('float-panel');return d;
  };
  // 收集「某标题之后、下一个标题/面板之前」的所有节点。之前的写法从第一个子元素就会命中
  // break（editor-top 在 DOM 顺序里位于 from 之前），导致所有内容都没进卡片、网格被散节点挤塌。
  const between=(from,until)=>{
    const out=[];let on=false;
    for(const node of [...workspace.children]){
      if(node===from){on=true;continue;}
      if(!on)continue;
      if(until&&node===until)break;
      if(node.classList&&(node.classList.contains('section-title')||node.classList.contains('advanced-panel')||node.classList.contains('editor-top')||node.classList.contains('timeline-panel')))break;
      out.push(node);
    }
    return out;
  };
  const preview=cell('preview'),tl=cell('timeline'),segments=cell('segments'),events=cell('events');
  preview.classList.add('cell-preview');
  if(editorTop)preview.append(editorTop);
  // 「当前视频」选择行从顶部外壳移进左栏时间线卡片（示意图：预览下方、时间线上方）。
  const shell=workspace.closest('.studio-panel');
  const folder=shell?.querySelector(':scope > .folder-row');
  if(timeline){
    if(folder)tl.append(folder);
    tl.append(timeline);
    // 时间线卡片里只保留标题、主工具栏和轨道画布；次要行（应用范围、素材、
    // 字幕选项、导出方案）折叠进展开块，避免把多轨轨道区挤成一条黑缝。
    const toolbars=[...timeline.querySelectorAll(':scope > .timeline-toolbar')];
    const hint=timeline.querySelector(':scope > .input-hint');
    const options=timeline.querySelector(':scope > .timeline-options');
    if(toolbars.length>1||hint||options){
      const extra=document.createElement('details');
      extra.className='timeline-extra';
      const summary=document.createElement('summary');
      summary.textContent='片段范围、素材与导出选项';
      extra.append(summary);
      if(toolbars[1])extra.append(toolbars[1]);
      if(toolbars[2])extra.append(toolbars[2]);
      if(hint)extra.append(hint);
      if(options)extra.append(options);
      if(toolbars[3])extra.append(toolbars[3]);
      timeline.append(extra);
    }
  }else if(folder&&shell)shell.append(folder);
  if(segTitle)segments.append(segTitle,...between(segTitle,evtTitle));
  if(evtTitle)events.append(evtTitle,...between(evtTitle,audioTitle));
  // 右栏上卡：声音与成片 + 已生成成片放进同一张卡；内容超出卡片高度时整卡滚轮滚动。
  let audioCell=null;
  if(audioTitle){
    audioCell=cell('audio');audioCell.classList.add('cell-audio');
    audioCell.append(audioTitle,...between(audioTitle,renderTitle));
    if(renderTitle)audioCell.append(renderTitle,...between(renderTitle,null));
  }
  // 右栏下卡：剪辑建议与精剪效果独立成卡，内容自行滚动。
  let effectsCell=null;
  if(advanced){advanced.style.display='';effectsCell=cell('effects');effectsCell.append(advanced);}
  // append 顺序即窄窗口（<1150px）纵向堆叠的顺序。
  const cells=[preview,tl,segments,events];
  if(audioCell)cells.push(audioCell);
  if(effectsCell)cells.push(effectsCell);
  workspace.append(...cells);
  // 高度按实际视口计算：必须设“确定高度”，否则网格的 fr 行会退化为按内容解析、被撑到数千像素。
  // 下限取两行最小高度之和（约 660px）：小窗口宁可轻微页面滚动，也不允许面板重叠或列表被挤没。
  // 窄窗口（<1150px）改用纵向堆叠 + 整页滚动，不再钉死高度。
  const fitDashboard=()=>{
    if(window.matchMedia('(max-width:1149px)').matches){workspace.style.height='';return;}
    const top=workspace.getBoundingClientRect().top+(window.scrollY||0);
    if(top>0)workspace.style.height=Math.max(660,window.innerHeight-top-26)+'px';
  };
  window.__fitDashboard=fitDashboard;
  window.addEventListener('resize',fitDashboard);
  fitDashboard();
  // 字体与布局稳定后再校准一次，防止首次渲染时量到 0 高。
  requestAnimationFrame(()=>requestAnimationFrame(fitDashboard));
})();
