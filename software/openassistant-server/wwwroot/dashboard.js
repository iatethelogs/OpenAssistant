"use strict";
const el=id=>document.getElementById(id);
const state={settings:null,models:null,devices:null,system:null,downloads:null,skills:[],voiceProfile:null,voiceUrl:null,key:"",loading:false};
const names={overview:"Обзор системы",settings:"Настройки ассистента",tests:"Проверка работы",devices:"Подключение устройства",integrations:"Интеграции",tools:"Инструменты",models:"Модели и загрузки",chat:"Диалог",media:"Музыка и радио",events:"События",logs:"Журнал сервера"};
function toast(msg,error=false){const box=el("toast");box.textContent=msg;box.className="toast show"+(error?" error":"");clearTimeout(toast.timer);toast.timer=setTimeout(()=>box.className="toast",5000)}
async function api(url,method="GET",data){const init={method,cache:"no-store"};if(data!==undefined){init.headers={"Content-Type":"application/json"};init.body=JSON.stringify(data)}const r=await fetch(url,init),v=await r.json().catch(()=>({}));if(!r.ok)throw Error(v.error||v.message||"HTTP "+r.status);return v}
function numericDecimal(id,min,max){const raw=el(id).value.trim().replace(",",".");const n=Number(raw);if(!raw||!Number.isFinite(n)||n<=min||n>max)throw Error("Параметр "+id+": число больше "+min+" и не больше "+max);return n}
function textNode(tag,text,className){const e=document.createElement(tag);e.textContent=String(text??"");if(className)e.className=className;return e}
function busy(btn,fn){return async()=>{btn.disabled=true;try{await fn()}catch(e){toast(e.message,true)}finally{btn.disabled=false}}}
function show(view){if(!(view in names))return;const storage=view==="models";if(storage)view="settings";for(const node of document.querySelectorAll(".view"))node.classList.toggle("active",node.id===view);for(const node of document.querySelectorAll("#navigation [data-view]")){const selected=node.dataset.view===(storage?"models":view);node.classList.toggle("active",selected);if(selected)node.setAttribute("aria-current","page");else node.removeAttribute("aria-current")}el("pageName").textContent=storage?names.models:names[view];history.replaceState(null,"","#"+(storage?"models":view));if(storage)window.OpenAssistantModelUI?.select("storage");if(view==="chat")queueMicrotask(()=>connectChat(true));if(view==="tools")queueMicrotask(()=>refreshCustomTools().catch(e=>toast(e.message,true)));}
for(const b of document.querySelectorAll("[data-view]"))b.onclick=()=>show(b.dataset.view);
function pct(a,b){return b>0?Math.min(100,Math.max(0,100*a/b)):0}
function serviceState(role){
 const status=state.models?.status||{}, proc=status.running?.[role];
 const health=status.api?.[role]?.state||"unreachable";
 const ready=health==="ready", loading=!!proc?.running&&!ready;
 const failed=!!proc&&!proc.running&&!ready;
 const label=loading?"Загружается":ready?"Готова":failed?"Ошибка запуска":"Остановлена";
 const elapsed=proc?.loadingSeconds||0;
 const details=loading?(proc.loadingStage||"Загрузка модели")+" · "+elapsed+" с":
   ready?("HTTP API готов"+(proc?.pid?" · PID "+proc.pid:"")):
   failed?("Процесс завершён"+(proc.exitCode!=null?" · код "+proc.exitCode:"")):
   health==="unknown"?"HTTP ответил, но готовность не подтверждена":"API недоступен";
 return {proc,ready,loading,failed,label,details,health};
}
function setService(role){
 const svc=serviceState(role),flag=el(role+"Flag");
 flag.textContent=svc.ready?"API готов":svc.loading?"Загрузка":svc.failed?"Ошибка":"Недоступен";
 flag.className="state-tag "+(svc.ready?"good":svc.loading?"neutral":"bad");
 el(role+"Status").textContent=svc.label;
 el(role+"Info").textContent=svc.details;
 const track=el(role+"LoadTrack"),description=el(role+"LoadDescription");
 if(track){track.hidden=!(svc.loading||svc.ready);track.classList.toggle("indeterminate",svc.loading);
   track.setAttribute("aria-valuetext",svc.details);if(svc.ready)track.setAttribute("aria-valuenow","100");else track.removeAttribute("aria-valuenow");}
 if(description)description.textContent=svc.loading?svc.details:svc.ready?"Готова к запросам":"";
}
function renderGpus(){const box=el("gpus");box.replaceChildren();const gpus=state.models?.status?.gpus||[];if(!gpus.length){box.append(textNode("div","Нет телеметрии NVIDIA. Проверьте драйвер и nvidia-smi.","empty"));return}for(const gpu of gpus){const card=textNode("div","","gpu-card");const head=textNode("div","","gpu-top");head.append(textNode("strong","GPU "+gpu.index+" · "+gpu.name),textNode("span",(gpu.temperature??"—")+" °C · "+(gpu.utilization??"—")+"%"));const bar=textNode("div","","gpu-bar"),fill=document.createElement("div");fill.style.width=pct(gpu.usedMiB,gpu.totalMiB)+"%";bar.append(fill);const foot=textNode("div","","gpu-bottom");foot.append(textNode("span",(gpu.usedMiB??"—")+"/"+(gpu.totalMiB??"—")+" MiB"),textNode("span",(gpu.powerWatts??"—")+" Вт"));card.append(head,bar,foot);box.append(card)}}
function renderDevices(){el("devicesCount").textContent=state.devices?.count??"—";el("onlineBadge").textContent=(state.devices?.count??0)+" онлайн";const box=el("connections");box.replaceChildren();const list=state.devices?.connections||[];if(!list.length){box.append(textNode("div","Подключённых устройств нет. После установки и настройки колонки она появится здесь.","empty"));return}
for(const entry of list){const row=textNode("div","","connection-entry");row.append(textNode("strong",entry.remote+" · "+entry.id),textNode("small","Подключение: "+new Date(entry.connectedAt).toLocaleString("ru-RU")+" · последний пакет: "+new Date(entry.lastPacketAt).toLocaleTimeString("ru-RU")));box.append(row)}}
function render(){for(const role of ["llm","stt","tts"])setService(role);renderModelCards();renderGpus();renderDevices();window.OpenAssistantModelUI?.refreshStatuses?.();if(el("settings")?.classList.contains("active"))window.OpenAssistantModelUI?.renderIndex?.();el("updatedAt").textContent="Обновлено "+new Date().toLocaleTimeString("ru-RU");
const sys=state.system||{};el("cpuValue").textContent=sys.cpuThreads??"—";el("memoryValue").textContent=sys.serverMemoryMiB==null?"—":sys.serverMemoryMiB+" МиБ";el("storageValue").textContent=sys.diskFreeGiB==null?"—":sys.diskFreeGiB+" ГиБ";el("downloadsValue").textContent=(state.downloads?.jobs||[]).filter(j=>["queued","downloading"].includes(j.state)).length;
el("serverChip").textContent="Подключено";el("sideStatus").textContent="Сервер доступен"}
async function poll(){if(state.loading)return;state.loading=true;try{const [models,system,devices,downloads]=await Promise.all([api("/api/models"),api("/api/control/system"),api("/api/control/devices"),api("/api/models/downloads")]);Object.assign(state,{models,system,devices,downloads});render()}catch(e){el("serverChip").textContent="Нет соединения";el("sideStatus").textContent="Нет подключения";}finally{state.loading=false}}
function updateVoiceSamplingInputs(){
 const piper=el("ttsEngineChoice")?.value?.startsWith("piper-")===true;
 el("piperVoiceFields").hidden=!piper;
 el("qwenVoiceFields").hidden=piper;
 const mode=el("voiceGenerationMode").value;
 el("voiceGenerationMode").disabled=!!piper;
 el("voiceSeed").disabled=!!piper||mode!=="seeded";
 for(const id of ["voiceTemperature","voiceTopP","voiceTopK"])el(id).disabled=!!piper||mode==="greedy";
 el("voicePenalty").disabled=!!piper;
 for(const id of ["voice","voiceStyleSetting","voiceDescriptionSetting"])el(id).disabled=!!piper;
}
el("voiceGenerationMode").onchange=updateVoiceSamplingInputs;
function loadSettings(){
 const s=state.settings||{},p=state.voiceProfile||{};
 el("prompt").value=s.systemPrompt??"";
 el("temperature").value=s.llmTemperature??.5;
 el("maxTokens").value=s.llmMaxTokens??512;
 for(const [id,key,fallback] of [["topP","llmTopP",.9],["topK","llmTopK",40],["repeatPenalty","llmRepeatPenalty",1.1]])el(id).value=s[key]??fallback;
 for(const [id,key] of [["llmUrl","llmUrl"],["llmModel","llmModel"],["sttUrl","sttUrl"],["sttModel","sttModel"],["ttsUrl","ttsUrl"],["ttsModel","ttsModel"]])el(id).value=s[key]??"";
 el("voice").value=p.speaker??s.ttsVoice??"Ryan";
 el("language").value=s.ttsLanguage??"Russian";el("ttsSpeechMode").value=s.ttsSpeechMode??"quality";
 el("voiceModeSetting").value=p.mode||"custom";
 el("voiceGenerationMode").value=p.generationMode||"seeded";
 el("voiceSeed").value=p.seed??42;
 el("voiceTemperature").value=p.temperature??0.75;
 el("voiceTopP").value=p.topP??0.9;
 el("voiceTopK").value=p.topK??40;
 el("voicePenalty").value=p.repetitionPenalty??1.05;
 el("voiceStyleSetting").value=p.style||"";
 el("voiceDescriptionSetting").value=p.description||"";
 for(const [id,key,fallback] of [
   ["piperLengthScale","piperLengthScale",1],["piperNoiseScale","piperNoiseScale",0.667],
   ["piperNoiseWScale","piperNoiseWScale",0.8],["piperVolume","piperVolume",1]
 ])el(id).value=p[key]??fallback;
 el("piperNormalizeAudio").checked=p.piperNormalizeAudio??true;
 updateVoiceSamplingInputs();
}
el("settingsForm").onsubmit=async event=>{
 event.preventDefault();const b=el("saveSettings");
 await busy(b,async()=>{
  const piperValue=(id,min,max)=>{
   const value=Number(el(id).value.replace(",","."));
   if(!Number.isFinite(value)||value<min||value>max)throw Error("Параметр Piper "+id+": диапазон "+min+"–"+max);
   return value;
  };
  const profile={...state.voiceProfile,
   mode:el("voiceModeSetting").value,speaker:el("voice").value.trim(),
   generationMode:el("voiceGenerationMode").value,seed:Number(el("voiceSeed").value),
   temperature:Number(el("voiceTemperature").value),topP:numericDecimal("voiceTopP",0,1),
   topK:Number(el("voiceTopK").value),repetitionPenalty:Number(el("voicePenalty").value),
   style:el("voiceStyleSetting").value.trim(),description:el("voiceDescriptionSetting").value.trim(),
   piperLengthScale:piperValue("piperLengthScale",0.5,2.5),
   piperNoiseScale:piperValue("piperNoiseScale",0,2),
   piperNoiseWScale:piperValue("piperNoiseWScale",0,2),
   piperVolume:piperValue("piperVolume",0,2),
   piperNormalizeAudio:el("piperNormalizeAudio").checked};
  // Each settings tab can be saved independently. A broken/unconfigured TTS
  // runtime must not prevent an LLM or STT settings change.
  const activePane=document.querySelector("[data-settings-panel]:not([hidden])")?.dataset.settingsPanel;
  const savedProfile=activePane==="tts"
    ? await api("/api/setup/voice","PUT",profile)
    : (state.voiceProfile||profile);
  const settings={...state.settings,systemPrompt:el("prompt").value.trim(),
   llmTemperature:Number(el("temperature").value),llmMaxTokens:Number(el("maxTokens").value),
   llmTopP:numericDecimal("topP",0,1),llmTopK:Number(el("topK").value),llmRepeatPenalty:Number(el("repeatPenalty").value),
   llmUrl:el("llmUrl").value.trim(),llmModel:el("llmModel").value.trim(),
   sttUrl:el("sttUrl").value.trim(),sttModel:el("sttModel").value.trim(),
   ttsUrl:el("ttsUrl").value.trim(),ttsModel:el("ttsModel").value.trim(),
   ttsVoice:savedProfile.speaker,ttsLanguage:el("language").value.trim(),ttsSpeechMode:el("ttsSpeechMode").value};
  delete settings.hasDeviceToken;delete settings.hasHomeAssistantToken;
  await api("/api/settings","POST",settings);
  state.voiceProfile=savedProfile;state.settings=settings;
  el("settingsNote").textContent="Сохранено";
  toast("Настройки сохранены")
 })();
};
el("runLlm").onclick=busy(el("runLlm"),async()=>{el("llmAnswer").textContent="Генерация ответа…";el("llmBenchmark").textContent="Измерение…";const r=await api("/api/control/test/llm","POST",{prompt:el("testPrompt").value,model:null});el("llmAnswer").textContent=r.text||"(ответ пустой)";el("llmBenchmark").textContent="Первый фрагмент "+r.firstTokenMs+" мс · "+r.totalMs+" мс · "+r.tokensPerSecond+" ток/с"+(r.tokensExact?"":"*");toast("Тест завершён")});
el("runTts").onclick=busy(el("runTts"),async()=>{el("ttsBenchmark").textContent="Генерация WAV…";const response=await fetch("/api/control/test/tts",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({text:el("testPhrase").value,voice:null,model:null})});if(!response.ok){const x=await response.json().catch(()=>({}));throw Error(x.error||"HTTP "+response.status)}const wav=await response.blob();if(state.voiceUrl)URL.revokeObjectURL(state.voiceUrl);state.voiceUrl=URL.createObjectURL(wav);el("ttsPlayer").src=state.voiceUrl;el("downloadAudio").href=state.voiceUrl;el("downloadAudio").classList.remove("hidden");el("ttsBenchmark").textContent=response.headers.get("X-Synthesis-Ms")+" мс · RTF "+response.headers.get("X-RTF");await el("ttsPlayer").play().catch(()=>{});toast("Аудио готово")});

el("discoverClients").onclick=busy(el("discoverClients"),async()=>{
 const container=el("discoveredClients");container.textContent="Идёт UDP-поиск симуляторов…";
 const discovered=await api("/api/discovery/clients","POST");
 container.replaceChildren();
 if(!discovered.length){
  container.append(textNode("p","Устройства не найдены. Проверьте, что симулятор запущен, разрешён UDP 5057 и оба компьютера в одной подсети.","muted"));
  return;
 }
 for(const found of discovered){
  const row=textNode("div","","connection-entry");
  row.append(textNode("strong",found.name+" · "+found.address),
   textNode("small",found.connected?"WebSocket уже подключён":"Ожидает подключения"));
  container.append(row);
 }
 toast("Найдено устройств: "+discovered.length);
});
function deviceAddress(){let h=el("serverHost").value.trim().replace(/^(ws|wss|http|https):\/\//,"").replace(/\/.*$/,"").replace(/:\d+$/,"");el("wsAddress").textContent="ws://"+(h||"SERVER_IP")+":5055/ws"}
el("serverHost").oninput=deviceAddress;
async function copy(value){if(!navigator.clipboard?.writeText)throw Error("Копирование работает через HTTPS или localhost");await navigator.clipboard.writeText(value);toast("Скопировано")}
el("copyAddress").onclick=busy(el("copyAddress"),()=>copy(el("wsAddress").textContent));
el("copyKey").onclick=busy(el("copyKey"),async()=>{if(!state.key)throw Error("Ключ ещё не загружен");await copy(state.key)});
async function loadDeviceKey(){
 const controller=new AbortController();
 const timeout=setTimeout(()=>controller.abort(),7000);
 try{
  const response=await fetch("/api/control/device/key",{cache:"no-store",signal:controller.signal});
  if(!response.ok)throw Error("HTTP "+response.status);
  const key=await response.json();
  if(!key.token)throw Error("Сервер вернул пустой ключ");
  state.key=key.token;
  el("deviceKey").textContent=key.token;
 }finally{clearTimeout(timeout)}
}
el("createKey").onclick=busy(el("createKey"),async()=>{
 if(!confirm("Перевыпустить ключ устройства? Старый перестанет работать при следующем подключении."))return;
 const r=await api("/api/control/device/key","POST");
 state.key=r.token;
 el("deviceKey").textContent=r.token;
 toast("Ключ перевыпущен и сохранён на сервере");
});

const emptyToolSchema=()=>({type:"object",properties:{},required:[],additionalProperties:false});
let customTools=[],editingTool=null;
function resetToolEditor(){
 editingTool=null;el("toolName").disabled=false;el("toolName").value="";el("toolTitle").value="";
 el("toolDescription").value="";el("toolSchema").value=JSON.stringify(emptyToolSchema(),null,2);
 el("toolEnabled").checked=false;el("toolDelete").disabled=true;el("toolMode").textContent="Новый инструмент";
 el("toolSaveStatus").textContent="";
}
function editTool(item){
 editingTool=item.name;el("toolName").value=item.name;el("toolName").disabled=true;
 el("toolTitle").value=item.title;el("toolDescription").value=item.description;
 el("toolSchema").value=JSON.stringify(item.parameters,null,2);
 el("toolEnabled").checked=!!item.enabled;el("toolDelete").disabled=false;
 el("toolMode").textContent=item.implemented?"Обработчик подключён":"Описание без обработчика";
 el("toolSaveStatus").textContent="";
}
function renderCustomTools(){
 const box=el("customToolsList");box.replaceChildren();
 if(!customTools.length){box.append(textNode("p","Инструментов пока нет. Создайте первое описание.","muted"));return}
 for(const item of customTools){
  const button=textNode("button","","tool-item");button.type="button";
  button.classList.toggle("selected",item.name===editingTool);
  button.append(textNode("strong",item.title),textNode("code",item.name),
   textNode("small",item.implemented?"Обработчик подключён":item.enabled?"Схема активна · нет обработчика":"Схема отключена"));
  button.onclick=()=>{editTool(item);renderCustomTools()};box.append(button);
 }
}
async function refreshCustomTools(){
 customTools=await api("/api/tools");renderCustomTools();
 if(editingTool){const previous=customTools.find(x=>x.name===editingTool);if(previous)editTool(previous);else resetToolEditor()}
}
el("toolNew").onclick=()=>{resetToolEditor();renderCustomTools();el("toolName").focus()};
el("customToolForm").onsubmit=async event=>{
 event.preventDefault();const btn=el("toolSave");
 await busy(btn,async()=>{
  let schema;
  try{schema=JSON.parse(el("toolSchema").value)}
  catch(e){throw Error("Ошибка JSON Schema: "+e.message)}
  const name=el("toolName").value.trim();
  const entry={name,title:el("toolTitle").value.trim(),description:el("toolDescription").value.trim(),
   parameters:schema,enabled:el("toolEnabled").checked};
  await api("/api/tools/"+encodeURIComponent(name),"PUT",entry);
  editingTool=name;await refreshCustomTools();renderCustomTools();
  el("toolSaveStatus").textContent="Сохранено";toast("Описание инструмента сохранено");
 })
};
el("toolDelete").onclick=busy(el("toolDelete"),async()=>{
 if(!editingTool||!confirm("Удалить инструмент "+editingTool+"?"))return;
 await api("/api/tools/"+encodeURIComponent(editingTool),"DELETE");
 resetToolEditor();await refreshCustomTools();toast("Инструмент удалён");
});
resetToolEditor();
function renderSkills(){const box=el("skillsList");box.replaceChildren();for(const skill of state.skills){const label=textNode("label","","skill-option");const check=document.createElement("input");check.type="checkbox";check.dataset.skill=skill.name;check.checked=skill.enabled;const section=document.createElement("div");section.append(textNode("strong",skill.title),textNode("small",skill.description+(skill.sensitive?" · Действие требует разрешения":"")));label.append(check,section);box.append(label)}}
el("saveSkills").onclick=busy(el("saveSkills"),async()=>{const changes={};for(const input of document.querySelectorAll("#skillsList input"))changes[input.dataset.skill]=input.checked;state.skills=await api("/api/control/skills","PUT",changes);renderSkills();toast("Разрешения навыков обновлены")});
async function init(){const page=location.hash.slice(1);show(page in names?page:"overview");loadDeviceKey().catch(e=>{el("deviceKey").textContent="Не удалось загрузить ключ: "+e.message});try{const [settings,skills,voiceProfile]=await Promise.all([api("/api/settings"),api("/api/control/skills"),api("/api/setup/voice")]);state.settings=settings;state.skills=skills;state.voiceProfile=voiceProfile;loadSettings();renderSkills()}catch(e){toast("Настройки: "+e.message,true)}await poll();await refreshConnections().catch(()=>{});await refreshModels().catch(()=>{});await initTtsChoices().catch(e=>toast("Каталог TTS: "+e.message,true));
 if(!el("serverHost").value && location.hostname && location.hostname!=="localhost" && location.hostname!=="127.0.0.1"){
  el("serverHost").value=location.hostname;deviceAddress();
 }
}
init();setInterval(poll,6000);setInterval(()=>{if(document.getElementById("logs").classList.contains("active"))refreshServerLogs().catch(()=>{});if(document.getElementById("settings")?.classList.contains("active")&&!document.getElementById("settings-storage")?.hidden)refreshModels().catch(()=>{})},5000);


// Engine setup is managed by the same backend as the first-run wizard.
let ttsInstallPolling=false;
async function pollTtsInstallation(){
 if(ttsInstallPolling)return;
 ttsInstallPolling=true;
 const indicator=el("ttsEngineInstallStatus"),button=el("ttsEngineInstall");
 button.disabled=true;
 try{
  while(true){
   const status=await api("/api/setup/status");
   const job=status.jobs?.find(x=>x.role==="tts");
   if(!job||job.status!=="running"){
    if(job)indicator.textContent=job.status==="ready"?"Модель запущена: "+job.name:"Ошибка: "+(job.error||job.description);
    await refreshModels();
    if(job?.status==="ready"){
     const current=await api("/api/settings");
     state.settings=current;
     el("ttsModel").value=current.ttsModel??"";
     state.voiceProfile=await api("/api/setup/voice");
     el("voiceModeSetting").value=state.voiceProfile.mode||"custom";
    }
    break;
   }
   indicator.textContent=job.description||"Загрузка модели";
   await new Promise(resolve=>setTimeout(resolve,1800));
  }
 }catch(error){indicator.textContent=error.message;}
 finally{ttsInstallPolling=false;button.disabled=false;}
}
async function initTtsChoices(){
 const catalog=await api("/api/setup/catalog"),picker=el("ttsEngineChoice");
 const tts=catalog.filter(model=>model.role==="tts");
 picker.replaceChildren(...tts.map(model=>new Option(model.name,model.id)));
 const profile=state.models?.profiles?.tts;
 const configured=profile?.environment?.OPENASSISTANT_MODEL;
 const engine=profile?.environment?.OPENASSISTANT_TTS_ENGINE;
 const selected=tts.find(model=>model.id.startsWith("piper-")
   ? model.file===configured
   : model.repo===configured&&model.id.startsWith("qwen-tts-fast-")===(engine==="faster"));
 picker.value=selected?.id||"qwen-tts-fast-06";
 picker.onchange=updateVoiceSamplingInputs;
 updateVoiceSamplingInputs();
 el("ttsEngineInstall").onclick=async()=>{
  if(ttsInstallPolling)return;
  const button=el("ttsEngineInstall");
  button.disabled=true;
  try{
   const modelId=picker.value,piper=modelId.startsWith("piper-");
   const job=await api("/api/setup/tts","POST",{modelId,gpuUuid:piper?"cpu":null});
   el("ttsEngineInstallStatus").textContent=job.description||"Подготовка модели";
   await pollTtsInstallation();
  }catch(error){toast(error.message,true);}
  finally{button.disabled=false;}
 };
 const status=await api("/api/setup/status");
 if(status.jobs?.some(x=>x.role==="tts"&&x.status==="running"))void pollTtsInstallation();
}

/* Additional sections share the same layout and API. No nested pages or iframes. */
const modelState={files:[],selected:new Set(),revision:"main"};
function renderModelCards(){
 const box=el("modelCards");box.replaceChildren();
 for(const role of ["llm","stt","tts"]){
  const current=state.models?.status?.running?.[role];
  const card=textNode("article","","service");const head=textNode("div",role.toUpperCase(),"service-type");
  const model=state.models?.profiles?.[role];
  const modelName=current?.displayName||model?.displayName||
    (role==="stt"&&model?.environment?.OPENASSISTANT_MODEL==="v3_e2e_rnnt"?"GigaAM v3 · RNNT":null)||
    (model?.environment?.OPENASSISTANT_MODEL||"").split("/").pop()||
    (current?.modelPath||model?.modelPath||"").split(/[\\/]/).pop();
  const svc=serviceState(role);
  card.append(head,textNode("div",svc.label,"service-value"),
    textNode("div",modelName||"Модель не выбрана","service-caption"));
  if(svc.loading||svc.ready||svc.failed){
   const progress=textNode("div","","model-readiness");progress.setAttribute("role","status");
   progress.append(textNode("div",svc.details,"model-readiness-detail"));
   if(svc.loading||svc.ready){
    const track=textNode("div","","model-readiness-track"+(svc.loading?" indeterminate":""));
    track.setAttribute("role","progressbar");track.setAttribute("aria-label","Готовность "+role.toUpperCase());
    if(svc.ready)track.setAttribute("aria-valuenow","100");
    track.append(textNode("span","","model-readiness-fill"));
    progress.append(track);
   }
   card.append(progress);
  }
  const actions=textNode("div","","model-card-actions");
  const edit=textNode("button","Параметры","button secondary");
  edit.onclick=()=>{show("settings");window.OpenAssistantModelUI?.select(role);window.OpenAssistantModelUI?.mountRole(role);el("profileModel").scrollIntoView({behavior:"smooth",block:"center"})};
  actions.append(edit);
  if(current?.running){
    const stop=textNode("button","Остановить","button secondary");
    stop.onclick=busy(stop,async()=>{await api("/api/models/"+role+"/stop","POST");await refreshModels();toast("Процесс остановлен")});
    actions.append(stop);
  }else if(model?.modelPath){
    const start=textNode("button","Запустить","button primary");
    start.onclick=busy(start,async()=>{await api("/api/models/"+role+"/start","POST");await refreshModels()});
    actions.append(start);
  }
  card.append(actions);box.append(card);
 }
}
function loadProfile(){
 const role=el("profileRole").value,p=state.models?.profiles?.[role]||{};
 el("profileExecutable").value=p.executable||"";
 el("profileModel").value=p.modelPath||"";
 el("profileCwd").value=p.workingDirectory||"";
 el("profileHealth").value=p.healthUrl||"";
 el("profileArgs").value=(p.arguments||[]).join("\n");
 const list=el("profileGpus");list.replaceChildren();
 for(const gpu of state.models?.status?.gpus||[]){
  const row=document.createElement("label");row.className="gpu-choice";
  const check=document.createElement("input");check.type="checkbox";check.value=gpu.uuid;check.checked=(p.gpuUuids||[]).includes(gpu.uuid);
  row.append(check,textNode("span","GPU "+gpu.index+" · "+gpu.name));list.append(row);
 }
 const log=state.models?.status?.running?.[role]?.logs||[];
 el("profileLogs").textContent=log.slice(-40).join("\n")||"Нет сообщений процесса";
}
function readProfile(){
 const role=el("profileRole").value,prior=state.models?.profiles?.[role]||{};
 return {...prior,executable:el("profileExecutable").value.trim(),
  modelPath:el("profileModel").value.trim(),workingDirectory:el("profileCwd").value.trim(),
  healthUrl:el("profileHealth").value.trim(),
  arguments:el("profileArgs").value.split("\n").map(x=>x.trim()).filter(Boolean),
  gpuUuids:[...el("profileGpus").querySelectorAll("input:checked")].map(x=>x.value),
  gpus:[]};
}
async function refreshModels(){
 const [models,downloads]=await Promise.all([api("/api/models"),api("/api/models/downloads")]);
 const was=JSON.stringify(state.downloads?.jobs);
 state.models=models;state.downloads=downloads;
 renderModelCards();
  if(!window.OpenAssistantModelUI?.hasUnsavedChanges?.() &&
     !document.activeElement?.closest(".model-editor"))loadProfile();
 const box=el("downloadsList");box.replaceChildren();
 for(const job of downloads.jobs||[]){
  const row=textNode("div","","download-row");row.append(textNode("strong",(job.role?job.role.toUpperCase()+" · ":"")+(job.repo||job.id)),
   textNode("span",(job.state||"")+(job.error?" · "+job.error:""),"minor"));
  const percent=job.total?Math.round(job.bytes/job.total*100):0;
  const bar=textNode("div","","progress-line");const fill=document.createElement("span");fill.style.width=Math.min(percent,100)+"%";bar.append(fill);row.append(bar);
  if(job.state==="downloading"){const stop=textNode("button","Отменить","button secondary");stop.onclick=busy(stop,async()=>{await api("/api/models/downloads/"+job.id+"/cancel","POST");await refreshModels()});row.append(stop)}
  box.append(row);
 }
 if(!box.children.length)box.textContent="Загрузок нет";
 const files=el("installedModels");files.replaceChildren();
 for(const item of (downloads.installed||[]).slice(0,450)){
  const row=textNode("div","","installed-row");
  const name=textNode("span",(item.role||"").toUpperCase()+" · "+item.name,"installed-name");name.title=item.path;
  const actions=textNode("div","","installed-actions");
  if(item.role==="llm"){
   const select=textNode("button","Выбрать","button secondary");
   select.onclick=()=>{show("settings");window.OpenAssistantModelUI?.select("llm");window.OpenAssistantModelUI?.mountRole("llm");el("profileModel").value=item.path;el("profileModel").scrollIntoView({behavior:"smooth",block:"center"});toast("Сохраните профиль для запуска.")};
   actions.append(select);
   if(item.removable){
    const del=textNode("button","Удалить веса","button danger");
    del.onclick=busy(del,async()=>{
     if(!confirm("Удалить файл весов "+item.name+"? Это действие нельзя отменить."))return;
     await api("/api/models/weights/delete","POST",{path:item.path});
     await refreshModels();toast("Веса удалены")
    });
    actions.append(del);
   }
  }else actions.append(textNode("span",item.source==="manual-download"?"Файлы в каталоге":item.role==="tts"?"Кеш Hugging Face":"Кеш GigaAM","minor"));
  row.append(name,actions);files.append(row);
 }
 if(!files.children.length)files.textContent="Файлов пока нет";window.OpenAssistantModelUI?.renderIndex();
}
el("profileRole").onchange=loadProfile;
el("profileSave").onclick=busy(el("profileSave"),async()=>{
 const role=el("profileRole").value;await api("/api/models/"+role,"PUT",readProfile());
  window.OpenAssistantModelUI?.saved?.(role);await refreshModels();toast("Профиль сохранён")});
for(const action of ["Start","Restart","Stop"]){const b=el("profile"+action);b.onclick=busy(b,async()=>{await api("/api/models/"+el("profileRole").value+"/"+action.toLowerCase(),"POST");await refreshModels();toast("Команда выполнена")})}
el("inspectHub").onclick=busy(el("inspectHub"),async()=>{
 const repo=el("hfRepo").value.trim(),rev=el("hfRevision").value.trim()||"main";
 const r=await api("/api/models/hub?repo="+encodeURIComponent(repo)+"&revision="+encodeURIComponent(rev));
 modelState.files=r.files||[];modelState.selected=new Set();
 const box=el("hubFiles");box.replaceChildren();
 for(const f of modelState.files){const label=document.createElement("label");label.className="file-choice";const c=document.createElement("input");c.type="checkbox";c.value=f.path;
 c.onchange=()=>{c.checked?modelState.selected.add(f.path):modelState.selected.delete(f.path);el("downloadHint").textContent=modelState.selected.size+" файлов выбрано"};
 label.append(c,textNode("span",f.path+" · "+(f.size/1048576).toFixed(1)+" МиБ"));box.append(label)}
 if(!modelState.files.length)box.textContent="Файлов нет";
});
el("downloadSelected").onclick=busy(el("downloadSelected"),async()=>{
 if(!modelState.selected.size)throw Error("Выберите файлы");
 await api("/api/models/downloads","POST",{repo:el("hfRepo").value.trim(),
    revision:el("hfRevision").value.trim()||"main",files:[...modelState.selected],
    role:window.OpenAssistantModelUI?.downloadRole?.()||"llm"});
 await refreshModels();toast("Загрузка началась")});
el("saveHfToken").onclick=busy(el("saveHfToken"),async()=>{
 await api("/api/models/hf-token","POST",{token:el("hfToken").value.trim()});el("hfToken").value="";toast("Токен сохранён")});
/* Connections */
let oauthSession=null,oauthEpoch=0,oauthTimer=null;
function clearOauth(){oauthEpoch++;oauthSession=null;if(oauthTimer)clearTimeout(oauthTimer);oauthTimer=null;el("ymPair").classList.add("hidden")}
async function refreshConnections(){
 const [status,settings]=await Promise.all([api("/api/integrations"),api("/api/settings")]);
 el("ymState").textContent=status.yandexMusic?.connected?"Подключено · "+(status.yandexMusic.account||"аккаунт"):"Не подключено";
 el("haState").textContent=status.homeAssistant?.connected?"Подключено":"Не подключено";
 if(document.activeElement!==el("haUrl"))el("haUrl").value=settings.homeAssistantUrl||"";
}
el("ymStart").onclick=busy(el("ymStart"),async()=>{
 clearOauth();
 const result=await api("/api/integrations/yandex/device/start","POST");
 const epoch=++oauthEpoch;oauthSession=result.session;
 el("ymPair").classList.remove("hidden");el("ymCode").textContent=result.userCode;el("ymLink").href=result.verificationUrl;
 const step=async()=>{if(epoch!==oauthEpoch||!oauthSession)return;
  try{
   const r=await api("/api/integrations/yandex/device/poll","POST",{session:oauthSession});
   if(epoch!==oauthEpoch)return;
   if(r.state==="connected"){clearOauth();await refreshConnections();toast("Музыка подключена");return}
   if(["expired","error"].includes(r.state)){el("ymProgress").textContent=r.message||"Авторизация завершилась";return}
   el("ymProgress").textContent=r.message||"Ожидаем подтверждения";
   oauthTimer=setTimeout(step,Math.max(5,Number(r.interval)||5)*1000);
  }catch(e){el("ymProgress").textContent=e.message;oauthTimer=setTimeout(step,10000)}
 };
 oauthTimer=setTimeout(step,Math.max(5,Number(result.interval)||5)*1000);
});
el("ymCancel").onclick=busy(el("ymCancel"),async()=>{clearOauth();await api("/api/integrations/yandex/device/cancel","POST")});
el("ymSaveToken").onclick=busy(el("ymSaveToken"),async()=>{await api("/api/integrations/yandex/token","POST",{token:el("ymToken").value.trim()});el("ymToken").value="";clearOauth();await refreshConnections()});
el("ymDisconnect").onclick=busy(el("ymDisconnect"),async()=>{clearOauth();await api("/api/integrations/yandex","DELETE");await refreshConnections()});
el("haConnect").onclick=busy(el("haConnect"),async()=>{await api("/api/integrations/homeassistant","POST",{url:el("haUrl").value.trim(),token:el("haToken").value.trim()});el("haToken").value="";await refreshConnections()});
el("haDisconnect").onclick=busy(el("haDisconnect"),async()=>{await api("/api/integrations/homeassistant","DELETE");await refreshConnections()});
/* Server logs and events */
async function refreshServerLogs(){
 const data=await api("/api/setup/logs?take=700"),level=el("logLevel").value;
 el("serverLogs").textContent=data.filter(x=>!level||x.level===level).map(x=>new Date(x.time).toLocaleTimeString("ru-RU")+" ["+x.level+"] "+x.category+" · "+x.message).join("\n")||"Сообщений нет";
}
el("refreshLogs").onclick=busy(el("refreshLogs"),refreshServerLogs);
el("logLevel").onchange=()=>refreshServerLogs().catch(e=>toast(e.message,true));
async function refreshEvents(){
 const rows=await api("/api/events");
 el("eventsOutput").textContent=JSON.stringify(rows,null,2);
}
el("refreshEvents").onclick=busy(el("refreshEvents"),refreshEvents);
/* Radio and music */
function drawSearch(results,target,play){
 const box=el(target);box.replaceChildren();
 for(const [index,item] of (Array.isArray(results)?results:[]).slice(0,25).entries()){
  const row=textNode("div","","search-row");
  const title=item.name||item.title||item.trackTitle||JSON.stringify(item).slice(0,100);
  row.append(textNode("span",title),textNode("button","Слушать","button secondary"));
  row.querySelector("button").onclick=()=>play(item,index).catch(e=>toast(e.message,true));
  box.append(row);
 }
 if(!box.children.length)box.textContent="Ничего не найдено";
}
const browserPlayer=el("mediaPlayer");
let browserQueue=[],browserIndex=-1,browserSource="",browserEpoch=0;
function renderBrowserQueue(){
 const list=el("musicQueue");list.replaceChildren();
 for(const [i,track] of browserQueue.slice(0,40).entries()){
  const row=textNode("div","","search-row");
  const title=(track.artists?.join(", ")||"")+(track.artists?.length?" — ":"")+(track.title||"Трек");
  row.append(textNode("span",(i===browserIndex?"▶ ":"")+title),textNode("button","Слушать","button secondary"));
  row.querySelector("button").onclick=()=>playBrowserTrack(i).catch(e=>toast(e.message,true));
  list.append(row);
 }
 el("musicQueueStatus").textContent=browserQueue.length?
  "Трек "+(browserIndex+1)+" из "+browserQueue.length+" · автопереход включён":"Очередь пуста";
}
async function playBrowserTrack(index){
 if(index<0||index>=browserQueue.length)return;
 const epoch=++browserEpoch,track=browserQueue[index];
 browserSource="yandex";browserIndex=index;renderBrowserQueue();
 const title=(track.artists?.join(", ")||"")+(track.artists?.length?" — ":"")+(track.title||"Трек");
 el("musicCurrent").textContent=title+" · получение аудиоссылки";
 const info=await api("/api/yandex/tracks/"+encodeURIComponent(track.id)+"/playback");
 if(epoch!==browserEpoch)return;
 browserPlayer.src=info.url;el("musicCurrent").textContent=title;
 try{await browserPlayer.play()}
 catch(error){el("musicQueueStatus").textContent="Не удалось воспроизвести: "+error.message;throw error}
}
async function browserNext(){
 if(browserSource!=="yandex")return;
 if(browserIndex+1>=browserQueue.length&&browserQueue.length){
  try{
   const recommendations=await api("/api/yandex/tracks/"+encodeURIComponent(browserQueue.at(-1).id)+"/similar");
   const known=new Set(browserQueue.map(x=>x.id));
   browserQueue.push(...recommendations.filter(x=>!known.has(x.id)&&known.add(x.id)).slice(0,30));
  }catch(error){toast("Продолжение музыки: "+error.message,true)}
 }
 if(browserIndex+1>=browserQueue.length){
  el("musicQueueStatus").textContent="Очередь закончилась";return;
 }
 await playBrowserTrack(browserIndex+1);
}
browserPlayer.addEventListener("ended",()=>{void browserNext().catch(e=>toast(e.message,true))});
el("musicPrev").onclick=busy(el("musicPrev"),async()=>{if(browserSource==="yandex")await playBrowserTrack(Math.max(0,browserIndex-1))});
el("musicNext").onclick=busy(el("musicNext"),browserNext);
el("musicPause").onclick=busy(el("musicPause"),async()=>{if(!browserPlayer.src)return;if(browserPlayer.paused)await browserPlayer.play();else browserPlayer.pause()});
el("musicStop").onclick=()=>{
 browserEpoch++;browserSource="";browserPlayer.pause();browserPlayer.removeAttribute("src");
 browserPlayer.load();browserIndex=-1;browserQueue=[];el("musicCurrent").textContent="Трек не выбран";renderBrowserQueue();
};
el("searchRadio").onclick=busy(el("searchRadio"),async()=>{
 const r=await api("/api/radio/search?name="+encodeURIComponent(el("radioQuery").value.trim()));
 drawSearch(r,"radioResults",async x=>{
  const url=x.url||x.urlResolved;if(!url)throw Error("Нет адреса потока");
  browserEpoch++;browserSource="radio";browserQueue=[];browserIndex=-1;
  renderBrowserQueue();el("musicCurrent").textContent=x.name||"Радио";
  browserPlayer.src=url;await browserPlayer.play();
 });
});
el("searchMusic").onclick=busy(el("searchMusic"),async()=>{
 const r=await api("/api/yandex/search?query="+encodeURIComponent(el("musicQuery").value.trim()));
 drawSearch(r,"musicResults",async(_,index)=>{browserQueue=r;await playBrowserTrack(index)});
});
el("loadYmPlaylists").onclick=busy(el("loadYmPlaylists"),async()=>{
 const entries=await api("/api/yandex/playlists"),picker=el("ymPlaylistSelect");
 picker.replaceChildren(new Option("Выберите плейлист",""),
   ...entries.map(p=>new Option(p.title+" · "+p.trackCount+" треков",p.kind)));
 if(entries.length===0)toast("Плейлисты не найдены",true);
});
el("playYmPlaylist").onclick=busy(el("playYmPlaylist"),async()=>{
 const kind=el("ymPlaylistSelect").value;
 if(!/^\d{1,12}$/.test(kind))throw Error("Выберите плейлист");
 const tracks=await api("/api/yandex/playlists/"+kind+"/tracks");
 if(!tracks.length)throw Error("Плейлист пуст");
 browserQueue=tracks;await playBrowserTrack(0);
});
el("playYmLiked").onclick=busy(el("playYmLiked"),async()=>{
 const tracks=await api("/api/yandex/liked");
 if(!tracks.length)throw Error("Понравившихся треков пока нет");
 browserQueue=tracks;await playBrowserTrack(0);
});
/* Chat directly via the device websocket */
let chatSocket=null,chatBuffer=[],chatSampleRate=24000;
function chatAppend(s){const box=el("chatHistory");box.textContent+=s;box.scrollTop=box.scrollHeight}
function activeProfile(){return el("chatProfile").value.trim()||"Общий"}
async function refreshMemoryCount(){
 const profile=activeProfile();
 const r=await api("/api/memory/"+encodeURIComponent(profile));
 el("chatMemoryCount").textContent=r.exchanges+" разговоров сохранено";
}
el("chatProfile").value=localStorage.getItem("openassistant-chat-profile")||"Общий";
el("chatProfileSet").onclick=()=>{
 const name=activeProfile();if(!/^[\p{L}\p{N} _.-]{1,48}$/u.test(name)){toast("Некорректное имя профиля",true);return}
 localStorage.setItem("openassistant-chat-profile",name);
 el("chatHistory").textContent="";
 if(chatSocket?.readyState===1)chatSocket.send(JSON.stringify({type:"profile",name}));
 refreshMemoryCount().catch(e=>toast(e.message,true));
};
el("chatMemoryClear").onclick=busy(el("chatMemoryClear"),async()=>{
 if(!confirm("Удалить сохранённые разговоры профиля «"+activeProfile()+"»?"))return;
 await api("/api/memory/"+encodeURIComponent(activeProfile()),"DELETE");
 el("chatHistory").textContent="";
 await refreshMemoryCount();
 toast("История профиля удалена");
});
refreshMemoryCount().catch(()=>{});
function connectChat(auto=false){
 if(chatSocket?.readyState===WebSocket.OPEN){if(!auto)chatSocket.close();return}
 if(chatSocket?.readyState===WebSocket.CONNECTING)return;
 const host=location.hostname.toLowerCase();
 const local=host==="localhost"||host==="127.0.0.1"||host==="::1"||host==="[::1]";
 const key=el("chatDeviceToken").value.trim();
 if(!local&&!key){el("chatStatus").textContent="Укажите ключ из «Устройства» или используйте SSH-туннель";return}
 // LAN connections require the device key; HTTPS/WSS is recommended for
 // untrusted networks. Do not silently disable chat on a trusted local LAN.
 if(!local&&location.protocol!=="https:")
   el("chatStatus").textContent="Подключение без шифрования · Только доверенная сеть";
 const uri=(location.protocol==="https:"?"wss://":"ws://")+location.host+"/ws";
 let socket;
 try{socket=new WebSocket(uri,key?["openassistant","oa.auth."+key]:["openassistant"])}
 catch(e){el("chatStatus").textContent=e.message;return}
 chatSocket=socket;socket.binaryType="arraybuffer";el("chatStatus").textContent="Подключение…";
 socket.onopen=()=>{
   if(socket!==chatSocket)return;
   socket.send(JSON.stringify({type:"profile",name:activeProfile()}));
   el("chatStatus").textContent="Подключено";
   el("chatConnect").textContent="Отключиться";
   el("chatSend").disabled=false;el("chatCancel").disabled=false;
   el("chatInput").focus();
 };
 socket.onclose=event=>{
   if(socket!==chatSocket)return;
   const detail=event.code===1006?" · Проверьте адрес, токен и доступность сервера":
     event.code===1008?" · Доступ запрещён":"";
   el("chatStatus").textContent="Отключено"+detail;
   el("chatConnect").textContent="Подключиться";
   el("chatSend").disabled=true;el("chatCancel").disabled=true;
 };
 socket.onerror=()=>{if(socket===chatSocket)el("chatStatus").textContent="Ошибка соединения. Проверьте ключ и адрес."};
 socket.onmessage=event=>{
   if(socket!==chatSocket)return;
   if(event.data instanceof ArrayBuffer){chatBuffer.push(new Uint8Array(event.data));return}
   let msg;try{msg=JSON.parse(event.data)}catch{return}
   if(msg.type==="transcript")chatAppend("\nВы: "+msg.text+"\nАссистент: ");
   else if(msg.type==="profile")el("chatStatus").textContent="Подключено · "+msg.name;
   else if(msg.type==="text"){chatAppend(msg.delta||"");el("chatStatus").textContent="Ответ ассистента"}
   else if(msg.type==="audio_start"){chatBuffer=[];chatSampleRate=msg.sampleRate||24000}
   else if(msg.type==="audio_end"){
     const size=chatBuffer.reduce((n,x)=>n+x.length,0),pcm=new Uint8Array(size);let offset=0;
     for(const x of chatBuffer){pcm.set(x,offset);offset+=x.length}
     const wav=new Uint8Array(44+pcm.length),view=new DataView(wav.buffer);
     const write=(n,t)=>{for(let i=0;i<t.length;i++)wav[n+i]=t.charCodeAt(i)};
     write(0,"RIFF");view.setUint32(4,wav.length-8,true);write(8,"WAVE");write(12,"fmt ");
     view.setUint32(16,16,true);view.setUint16(20,1,true);view.setUint16(22,1,true);
     view.setUint32(24,chatSampleRate,true);view.setUint32(28,chatSampleRate*2,true);
     view.setUint16(32,2,true);view.setUint16(34,16,true);write(36,"data");view.setUint32(40,size,true);wav.set(pcm,44);
     const player=el("chatAudio");
     if(player.dataset.url)URL.revokeObjectURL(player.dataset.url);
     player.dataset.url=URL.createObjectURL(new Blob([wav],{type:"audio/wav"}));
     player.src=player.dataset.url;player.hidden=!el("chatAudioEnabled").checked;
     if(!player.hidden)player.play().catch(()=>{});
     chatBuffer=[];
   }else if(msg.type==="error"){
     chatAppend("\nОшибка: "+(msg.message||"Ошибка обработки")+"\n");
     el("chatSend").disabled=false;el("chatStatus").textContent="Подключено";
   }else if(msg.type==="done"||msg.type==="cancelled"){
     chatAppend("\n");el("chatSend").disabled=false;
     el("chatStatus").textContent="Подключено";
     refreshMemoryCount().catch(()=>{});
   }
 };
}
el("chatConnect").onclick=()=>connectChat(false);
el("chatAudioEnabled").onchange=()=>{el("chatAudio").hidden=!el("chatAudioEnabled").checked};
function sendChat(){
 const s=el("chatInput").value.trim();if(!s)return;
 if(chatSocket?.readyState!==WebSocket.OPEN){
   el("chatStatus").textContent="Нет соединения. Нажмите «Подключиться»";
   return;
 }
 chatSocket.send(JSON.stringify({type:"text",text:s,audio:el("chatAudioEnabled").checked}));
 el("chatSend").disabled=true;el("chatStatus").textContent="Генерация ответа…";
 el("chatInput").value="";
}
el("chatSend").onclick=sendChat;el("chatInput").onkeydown=e=>{if(e.key==="Enter")sendChat()};el("chatCancel").onclick=()=>{if(chatSocket?.readyState===1)chatSocket.send(JSON.stringify({type:"cancel"}))};
el("assistantDefaults").onclick=busy(el("assistantDefaults"),async()=>{
 const p=await api("/api/control/assistant-defaults");
 el("prompt").value=p.systemPrompt;el("temperature").value=p.llmTemperature;el("maxTokens").value=p.llmMaxTokens;el("topP").value=p.llmTopP;el("topK").value=p.llmTopK;el("repeatPenalty").value=p.llmRepeatPenalty;
 toast("Параметры выставлены. Нажмите «Сохранить настройки».");
});
el("haRefreshEntities").onclick=busy(el("haRefreshEntities"),async()=>{
 const list=await api("/api/integrations/homeassistant/entities");
 const box=el("haEntities");box.replaceChildren();
 for(const item of list.slice(0,80)){
  const row=textNode("div","","ha-entity");
  row.append(textNode("span",item.name||item.id),textNode("small",(item.state||"—")+(item.unit?" "+item.unit:"")));
  box.append(row);
 }
 if(!list.length)box.textContent="Устройств нет";
});
/* Control navigation event hooks */
for(const btn of document.querySelectorAll('[data-view="models"],[data-view="integrations"],[data-view="logs"],[data-view="events"]')){
 btn.addEventListener("click",()=>{const v=btn.dataset.view;if(v==="models")refreshModels().catch(e=>toast(e.message,true));if(v==="integrations")refreshConnections().catch(e=>toast(e.message,true));if(v==="logs")refreshServerLogs().catch(e=>toast(e.message,true));if(v==="events")refreshEvents().catch(e=>toast(e.message,true))})
}

/* Microphone test uses the same audio conversion as initial setup. */
let recordedPcm=null, recordedUrl=null;
function setRecordingProgress(seconds){
 const pct=Math.min(100,Math.round(seconds*20));
 el("recordMeterDashboard").classList.add("active");
 el("recordTimeDashboard").textContent=seconds.toFixed(1).replace(".",",")+" / 5,0 с";
 el("recordBarDashboard").style.width=pct+"%";
 el("recordTrackDashboard").setAttribute("aria-valuenow",String(pct));
}
el("recordAudio").onclick=busy(el("recordAudio"),async()=>{
 recordedPcm=null;el("recognizeAudio").disabled=true;
 el("recognitionOutput").textContent="Запись…";el("recognitionSignal").textContent="";
 el("recognitionMetrics").textContent="";setRecordingProgress(0);
 try{
  const data=await OpenAssistantAudioTest.record({onProgress:info=>setRecordingProgress(info.elapsed)});
  recordedPcm=data.pcm;
  if(recordedUrl)URL.revokeObjectURL(recordedUrl);
  recordedUrl=URL.createObjectURL(data.wav);el("recordedAudio").src=recordedUrl;
  el("recognitionOutput").textContent="Запись завершена. Нажмите «Распознать запись».";
  el("recognitionSignal").textContent="RMS: "+data.rmsPercent.toFixed(2)+"% · Пик: "+data.peakPercent.toFixed(1)+"% · "+data.duration.toFixed(2)+" с";
  if(data.signalStatus==="silent")el("recognitionSignal").textContent+=". Почти тишина: проверьте микрофон.";
  el("recognizeAudio").disabled=false;
 }finally{el("recordMeterDashboard").classList.remove("active")}
});
el("recognizeAudio").onclick=busy(el("recognizeAudio"),async()=>{
 if(!recordedPcm)throw Error("Сначала запишите фразу");
 el("recognitionOutput").textContent="Распознавание…";
 const data=await OpenAssistantAudioTest.transcribe(recordedPcm);
 const shown=OpenAssistantAudioTest.description(data);
 el("recognitionOutput").textContent=shown.text;
 el("recognitionSignal").textContent=shown.quality;
 el("recognitionMetrics").textContent=shown.metrics;
});
