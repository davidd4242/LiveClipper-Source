// 本地音乐创建窗口：内置合成器无需下载，MusicGen 模型由用户明确同意后按需下载。
let musicStatus=null,musicJobId='',musicPollTimer=null,musicDownloadTimer=null;
const musicBytes=bytes=>bytes<=0?'无需下载':bytes>=1e9?`${(bytes/1e9).toFixed(1)} GB`:`${Math.round(bytes/1e6)} MB`;
function musicSetStatus(text,error=false){const el=$('music-generate-status');if(!el)return;el.textContent=text||'';el.style.color=error?'var(--danger)':'';}
function musicProgress(progress,detail,hide=false){
  const box=$('music-progress');if(!box)return;box.hidden=hide;box.querySelector('i').style.width=Math.max(0,Math.min(100,progress))+'%';$('music-progress-text').textContent=detail||'';
}
function musicDurationOptions(modelId){
  const ai=modelId!=='liveclipper-builtin';
  const options=ai?[10,20,30]:[15,30,60,120];
  const current=Number($('music-duration').value)||options[0];
  $('music-duration').innerHTML=options.map(value=>`<option value="${value}">${value} 秒</option>`).join('');
  $('music-duration').value=options.includes(current)?current:options[options.length-1];
}
function renderMusicStatus(){
  if(!musicStatus)return;
  const select=$('music-model'),current=select.value||musicStatus.defaultModelId;
  select.innerHTML=musicStatus.models.map(model=>`<option value="${esc(model.id)}">${esc(model.name)}${model.installed?'（已就绪）':''}</option>`).join('');
  select.value=musicStatus.models.some(x=>x.id===current)?current:musicStatus.defaultModelId;
  const model=musicStatus.models.find(x=>x.id===select.value)||musicStatus.models[0];
  $('music-model-meta').textContent=`${model.description}${model.approxBytes?` · 约 ${musicBytes(model.approxBytes)}`:''} · ${model.license}`;
  $('music-allow-download').checked=!!musicStatus.allowDownload;
  const downloading=musicStatus.download?.state==='preparing'||musicStatus.download?.state==='downloading';
$('music-download').disabled=model.builtIn||model.installed||downloading;
$('music-download').textContent=downloading?'正在下载…':model.builtIn?'无需下载':model.installed?'模型已就绪':'下载并安装模型';
$('music-download-status').textContent=model.builtIn?'内置合成器无需下载模型':(musicStatus.download?.detail||'');
  if(!model.builtIn&&musicStatus.download?.error)$('music-download-status').textContent=musicStatus.download.error;
  musicDurationOptions(model.id);
}
async function loadMusicStatus(){
  try{musicStatus=await api('/music/status');renderMusicStatus();}
  catch(error){musicSetStatus(error.message,true);}
}
function pollMusicDownload(){
  if(musicDownloadTimer)return;
  musicDownloadTimer=setInterval(async()=>{
    try{
      musicStatus=await api('/music/status');renderMusicStatus();
      const state=musicStatus.download?.state;
      if(state==='preparing'||state==='downloading')return;
      clearInterval(musicDownloadTimer);musicDownloadTimer=null;
      if(state==='completed'){toast('本地音乐模型已下载完成');musicSetStatus('模型已准备好，可以开始生成音乐。');}
      else if(state==='failed')musicSetStatus(musicStatus.download?.error||'模型下载失败',true);
    }catch{}
  },1000);
}
function stopMusicPolling(){if(musicPollTimer){clearInterval(musicPollTimer);musicPollTimer=null;}}
async function pollMusicJob(){
  if(!musicJobId)return;stopMusicPolling();
  const poll=async()=>{
    try{
      const job=await api(`/music/jobs/${encodeURIComponent(musicJobId)}`);
      musicProgress(job.progress,job.detail);
      if(job.status==='completed'){
        stopMusicPolling();$('music-cancel').hidden=true;$('music-generate').disabled=false;
        musicSetStatus('生成完成，已自动加入背景音乐库。');
        if(job.assetId){$('music-preview').hidden=false;$('music-preview-audio').src=`/api/assets/${encodeURIComponent(job.assetId)}/file`;$('music-preview-title').textContent='生成结果 · 已加入背景音乐库';}
        await studioLoadLists();toast('音乐已生成并加入素材库');
      }else if(job.status==='failed'||job.status==='cancelled'){
        stopMusicPolling();$('music-cancel').hidden=true;$('music-generate').disabled=false;
        musicSetStatus(job.error||job.detail,true);musicProgress(job.progress,job.detail);
      }
    }catch(error){stopMusicPolling();musicSetStatus(error.message,true);$('music-generate').disabled=false;$('music-cancel').hidden=true;}
  };
  await poll();if(musicJobId&&!musicPollTimer)musicPollTimer=setInterval(poll,1000);
}
function openMusicDialog(){$('music-dialog').showModal();$('music-preview').hidden=true;musicProgress(0,'',true);musicSetStatus('');loadMusicStatus();}
document.addEventListener('DOMContentLoaded',()=>{
  const open=$('open-music');if(open)open.addEventListener('click',openMusicDialog);
  const close=$('close-music');if(close)close.addEventListener('click',()=>$('music-dialog').close());
$('music-model').addEventListener('change',async()=>{renderMusicStatus();try{musicStatus=await api('/music/preferences','POST',{allowDownload:$('music-allow-download').checked,modelId:$('music-model').value});}catch(error){musicSetStatus(error.message,true);}});
$('music-allow-download').addEventListener('change',async()=>{musicSetStatus('正在保存下载设置…');try{musicStatus=await api('/music/preferences','POST',{allowDownload:$('music-allow-download').checked,modelId:$('music-model').value});musicSetStatus('下载设置已保存。');}catch(error){musicSetStatus(error.message,true);}});
  $('music-download').addEventListener('click',async()=>{
    const modelId=$('music-model').value,allow=$('music-allow-download').checked;
    if(!allow){musicSetStatus('请先勾选允许下载本地音乐模型。',true);return;}
    try{
      const state=await api('/music/models/download','POST',{modelId,allowDownload:true});
      musicSetStatus('模型下载已开始，可以保持窗口打开查看进度。');musicProgress(state.progress||1,state.detail);pollMusicDownload();
    }catch(error){musicSetStatus(error.message,true);}
  });
  $('music-form').addEventListener('submit',async event=>{
    event.preventDefault();
    const body={modelId:$('music-model').value,genre:$('music-genre').value.trim(),style:$('music-style').value.trim(),content:$('music-content').value.trim(),theme:$('music-theme').value.trim(),durationSeconds:Number($('music-duration').value),allowDownload:$('music-allow-download').checked};
    if(!body.genre||!body.style||!body.content||!body.theme){musicSetStatus('请填写曲风、风格、内容和主题。',true);return;}
    $('music-generate').disabled=true;$('music-cancel').hidden=false;$('music-preview').hidden=true;musicProgress(1,'正在创建任务…');
    try{
      const job=await api('/music/generate','POST',body);musicJobId=job.id;musicSetStatus('音乐生成中，请保持软件运行。');await pollMusicJob();
    }catch(error){$('music-generate').disabled=false;$('music-cancel').hidden=true;musicSetStatus(error.message,true);musicProgress(0,'',true);}
  });
  $('music-cancel').addEventListener('click',async()=>{
    if(!musicJobId)return;try{await api(`/music/jobs/${encodeURIComponent(musicJobId)}/cancel`,'POST');musicSetStatus('正在取消…');}catch(error){musicSetStatus(error.message,true);}
  });
  $('music-use').addEventListener('click',()=>{$('music-dialog').close();show('audio');toast('已加入背景音乐库，可在剪辑编辑器中选择');});
});
