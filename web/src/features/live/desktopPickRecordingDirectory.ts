interface FolderHost {
 postMessage(message:unknown):void;
 addEventListener(type:'message',listener:(event:{data:unknown})=>void):void;
 removeEventListener(type:'message',listener:(event:{data:unknown})=>void):void;
}
export function pickRecordingDirectory(role: 'recording' | 'music-model' = 'recording'):Promise<string|null>{
 const host=(window as unknown as {chrome?:{webview?:FolderHost}}).chrome?.webview;
 if(!host)return Promise.reject(new Error('浏览文件夹仅在桌面版可用；也可在输入框填写目录。'));
 const requestId=`${role}-folder-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
 return new Promise((resolve,reject)=>{
  const finish=(action:()=>void)=>{host.removeEventListener('message',onMessage);action();};
  const onMessage=(event:{data:unknown})=>{
   const data=event.data as {type?:string;requestId?:string;path?:unknown;error?:string}|null;
   if(data?.type!=='folder-picked'||data.requestId!==requestId)return;
   if(data.error){finish(()=>reject(new Error(data.error)));return;}
   if(data.path===null||typeof data.path==='string'){finish(()=>resolve(data.path as string|null));return;}
   finish(()=>reject(new Error('文件夹选择器未返回有效目录。')));
  };
  host.addEventListener('message',onMessage);
  try{host.postMessage({type:'pick-folder',role,requestId});}catch(error){finish(()=>reject(error));}
 });
}
