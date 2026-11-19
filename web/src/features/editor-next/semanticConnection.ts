import type {AiSettings} from '../../api/settings';
import {adviceConnection} from '../editor/aiAdvice';
/** Ollama ignores the editable compatible endpoint: disclose the server's actual configured listener. */
export function semanticConnection(settings:AiSettings){
 const connection=adviceConnection(settings);
 if(connection?.provider==='ollama')return {...connection,endpoint:settings.ollamaEndpoint.trim()};
 return connection;
}
export function isLocalOllamaEndpoint(endpoint:string){
 try{const url=new URL(endpoint);return ['http:','https:'].includes(url.protocol)&&['localhost','127.0.0.1','[::1]'].includes(url.hostname)&&!url.username&&!url.password;}catch{return false;}
}
