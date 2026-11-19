export function catalogModelFromLink<T extends {id:string;engine:string}>(value:string,models:readonly T[]):T|undefined {
  const link=value.trim().replace(/\/$/,'');
  return models.find(model=>model.engine==='musicgen'&&model.id!=='local/musicgen-directory'&&link===`https://huggingface.co/${model.id}`);
}
