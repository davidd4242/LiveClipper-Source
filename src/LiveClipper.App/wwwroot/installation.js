async function showInstallationEntry(){
  try{
    const info=await api('/installation');if(!info.installed)return;
    const section=document.createElement('div');section.className='settings-section';
    section.innerHTML=`<h2>软件安装</h2><p class="input-hint">安装位置：${esc(info.path)}</p><p class="input-hint">打开卸载界面，可选择保留或删除软件数据。</p><button class="quiet" type="button">卸载软件</button>`;
    section.querySelector('button').onclick=async()=>{try{await api('/installation/uninstall','POST',{});toast('已打开卸载界面');}catch(e){toast(e.message)}};
    $('settings-form').after(section);
  }catch{}
}
showInstallationEntry();
