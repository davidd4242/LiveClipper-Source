// 全局漂浮方块工具：鼠标调整大小、持久化尺寸、最小化/恢复。
(function(){
  const STORAGE_KEY='liveclipper.panel-tools.v1';
  const targetSelector='.float-panel,.settings-section,.audio-card,.timeline-panel,.advanced-panel,.editor-side';
  let saved={};
  try{saved=JSON.parse(localStorage.getItem(STORAGE_KEY)||'{}')||{};}catch{saved={};}
  const clamp=(value,min,max)=>Math.min(max,Math.max(min,value));
  const sizeKey=(el,index)=>{
    if(el.dataset.panelKey)return 'key:'+el.dataset.panelKey;
    if(el.id)return 'id:'+el.id;
    const title=el.querySelector(':scope > .section-title h2,:scope > h2,:scope > h3')?.textContent.replace(/\s+/g,' ').trim();
    return 'text:'+location.pathname+':'+(title||el.className)+':'+index;
  };
  const persist=()=>{try{localStorage.setItem(STORAGE_KEY,JSON.stringify(saved));}catch{}};
  const maxWidth=()=>Math.max(280,window.innerWidth-36);
  const maxHeight=()=>Math.max(160,window.innerHeight-36);
  function applySaved(el,key){
    const item=saved[key];if(!item)return;
    if(item.width)el.style.width=clamp(item.width,240,maxWidth())+'px';
    if(item.height)el.style.height=clamp(item.height,120,maxHeight())+'px';
  }
  function addMinimize(el){
    if(el.querySelector(':scope > .panel-minimize-btn'))return;
    const header=el.querySelector(':scope > .section-title,:scope > h2,:scope > h3');if(!header)return;
    const button=document.createElement('button');
    button.type='button';button.className='panel-minimize-btn';button.textContent='−';
    button.title='最小化 / 恢复';button.setAttribute('aria-label','最小化或恢复面板');
    button.addEventListener('click',event=>{
      event.preventDefault();event.stopPropagation();
      const minimized=el.classList.toggle('is-minimized');
      button.textContent=minimized?'+':'−';
      button.title=minimized?'恢复面板':'最小化面板';
    });
    const actions=header.querySelector(':scope > .actions,:scope > .studio-actions,:scope > .transcript-actions');
    if(actions)header.insertBefore(button,actions);else header.append(button);
  }
  function addResize(el,key){
    if(el.querySelector(':scope > .panel-resize-handle'))return;
    const handle=document.createElement('button');
    handle.type='button';handle.className='panel-resize-handle';handle.title='拖动调整大小';handle.setAttribute('aria-label','拖动调整面板大小');
    handle.addEventListener('pointerdown',event=>{
      if(event.button!==0)return;
      event.preventDefault();event.stopPropagation();
      const rect=el.getBoundingClientRect();
      const start={x:event.clientX,y:event.clientY,width:rect.width,height:rect.height};
      el.classList.add('is-resizing');handle.setPointerCapture?.(event.pointerId);
      const move=moveEvent=>{
        const width=clamp(start.width+moveEvent.clientX-start.x,240,maxWidth());
        const height=clamp(start.height+moveEvent.clientY-start.y,120,maxHeight());
        el.style.width=Math.round(width)+'px';el.style.height=Math.round(height)+'px';
      };
      const finish=()=>{
        handle.removeEventListener('pointermove',move);handle.removeEventListener('pointerup',finish);handle.removeEventListener('pointercancel',finish);
        el.classList.remove('is-resizing');
        const rectNow=el.getBoundingClientRect();saved[key]={width:Math.round(rectNow.width),height:Math.round(rectNow.height)};persist();
      };
      handle.addEventListener('pointermove',move);handle.addEventListener('pointerup',finish);handle.addEventListener('pointercancel',finish);
    });
    el.append(handle);
  }
  function enhance(root=document){
    const panels=[];if(root.nodeType===1&&root.matches(targetSelector))panels.push(root);panels.push(...root.querySelectorAll(targetSelector));
    panels.forEach((el,index)=>{
      if(el.dataset.panelTools==='1')return;
      el.dataset.panelTools='1';el.classList.add('resizable-panel');
      const key=sizeKey(el,index);el.dataset.panelSizeKey=key;
      addMinimize(el);addResize(el,key);applySaved(el,key);
    });
  }
  function clampAll(){document.querySelectorAll('.resizable-panel[data-panel-size-key]').forEach(el=>{
    const rect=el.getBoundingClientRect();
    if(rect.width>maxWidth())el.style.width=maxWidth()+'px';
    if(rect.height>maxHeight())el.style.height=maxHeight()+'px';
  });}
  window.__refreshPanelTools=enhance;
  enhance();
  window.addEventListener('resize',clampAll);
  new MutationObserver(records=>{for(const record of records)for(const node of record.addedNodes)if(node.nodeType===1)enhance(node);}).observe(document.body,{childList:true,subtree:true});
})();
