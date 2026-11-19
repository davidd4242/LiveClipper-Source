export function MusicModelLicense({model}:{model:{id:string;license?:string|null}}) {
  const official = ['facebook/musicgen-small','facebook/musicgen-medium','facebook/musicgen-large'].includes(model.id);
  return <>{model.license ?? '许可未知'}{official && <> <a href="https://creativecommons.org/licenses/by-nc/4.0/" target="_blank" rel="noopener noreferrer">查看官方许可：CC BY-NC 4.0</a></>}</>;
}
