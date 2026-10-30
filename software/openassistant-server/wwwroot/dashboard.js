"use strict";
const el=id=>document.getElementById(id);
const state={settings:null,models:null,devices:null,system:null,downloads:null,skills:[],voiceProfile:null,voiceUrl:null,key:"",loading:false};
const names={overview:"Обзор системы",settings:"Настройки ассистента",tests:"Проверка работы",devices:"Подключение устройства",integrations:"Интеграции",models:"Модели и загрузки",chat:"Диалог",media:"Музыка и радио",events:"События",logs:"Журнал сервера"};
function toast(msg,error=false){const box=el("toast");box.textContent=msg;box.className="toast show"+(error?" error":"");clearTimeout(toast.timer);toast.timer=setTimeout(()=>box.className="toast",5000)}
async function api(url,method="GET",data){const init={method,cache:"no-store"};if(data!==undefined){init.headers={"Content-Type":"application/json"};init.body=JSON.stringify(data)}const r=await fetch(url,init),v=await r.json().catch(()=>({}));if(!r.ok)throw Error(v.error||v.message||"HTTP "+r.status);return v}
function textNode(tag,text,className){const e=document.createElement(tag);e.textContent=String(text??"");if(className)e.className=className;return e}
function busy(btn,fn){return async()=>{btn.disabled=true;try{await fn()}catch(e){toast(e.message,true)}finally{btn.disabled=false}}}
function show(view){if(!(view in names))return;for(const node of document.querySelectorAll(".view"))node.classList.toggle("active",node.id===view);for(const node of document.querySelectorAll("#navigation [data-view]"))node.classList.toggle("active",node.dataset.view===view);el("pageName").textContent=names[view];history.replaceState(null,"","#"+view)}
for(const b of document.querySelectorAll("[data-view]"))b.onclick=()=>show(b.dataset.view);
function pct(a,b){return b>0?Math.min(100,Math.max(0,100*a/b)):0}
function setService(role){const status=state.models?.status||{},proc=status.running?.[role],health=status.api?.[role]?.state||"unreachable";const live=health==="ready";const running=proc?.running;const flag=el(role+"Flag");flag.textContent=live?"API готов":running?"Загрузка":health==="unknown"?"Нет проверки":"Недоступен";flag.className="state-tag "+(live?"good":running?"neutral":"bad");el(role+"Status").textContent=live?"Работает":running?"Запущен":"Остановлен";el(role+"Info").textContent=(proc?.pid?"PID "+proc.pid+" · ":"")+"HTTP: "+health}
function renderGpus(){const box=el("gpus");box.replaceChildren();const gpus=state.models?.status?.gpus||[];if(!gpus.length){box.append(textNode("div","Нет телеметрии NVIDIA. Проверьте драйвер и nvidia-smi.","empty"));return}for(const gpu of gpus){const card=textNode("div","","gpu-card");const head=textNode("div","","gpu-top");head.append(textNode("strong","GPU "+gpu.index+" · "+gpu.name),textNode("span",(gpu.temperature??"—")+" °C · "+(gpu.utilization??"—")+"%"));const bar=textNode("div","","gpu-bar"),fill=document.createElement("div");fill.style.width=pct(gpu.usedMiB,gpu.totalMiB)+"%";bar.append(fill);const foot=textNode("div","","gpu-bottom");foot.append(textNode("span",(gpu.usedMiB??"—")+"/"+(gpu.totalMiB??"—")+" MiB"),textNode("span",(gpu.powerWatts??"—")+" Вт"));card.append(head,bar,foot);box.append(card)}}
function renderDevices(){el("devicesCount").textContent=state.devices?.count??"—";el("onlineBadge").textContent=(state.devices?.count??0)+" онлайн";const box=el("connections");box.replaceChildren();const list=state.devices?.connections||[];if(!list.length){box.append(textNode("div","Подключённых устройств нет. После установки и настройки колонки она появится здесь.","empty"));return}
for(const entry of list){const row=textNode("div","","connection-entry");row.append(textNode("strong",entry.remote+" · "+entry.id),textNode("small","Подключение: "+new Date(entry.connectedAt).toLocaleString("ru-RU")+" · последний пакет: "+new Date(entry.lastPacketAt).toLocaleTimeString("ru-RU")));box.append(row)}}
function render(){for(const role of ["llm","stt","tts"])setService(role);renderGpus();renderDevices();el("updatedAt").textContent="Обновлено "+new Date().toLocaleTimeString("ru-RU");
const sys=state.system||{};el("cpuValue").textContent=sys.cpuThreads??"—";el("memoryValue").textContent=sys.serverMemoryMiB==null?"—":sys.serverMemoryMiB+" МиБ";el("storageValue").textContent=sys.diskFreeGiB==null?"—":sys.diskFreeGiB+" ГиБ";el("downloadsValue").textContent=(state.downloads?.jobs||[]).filter(j=>["queued","downloading"].includes(j.state)).length;
el("serverChip").textContent="Подключено";el("sideStatus").textContent="Сервер доступен"}
async function poll(){if(state.loading)return;state.loading=true;try{const [models,system,devices,downloads]=await Promise.all([api("/api/models"),api("/api/control/system"),api("/api/control/devices"),api("/api/models/downloads")]);Object.assign(state,{models,system,devices,downloads});render()}catch(e){el("serverChip").textContent="Нет соединения";el("sideStatus").textContent="Нет подключения";}finally{state.loading=false}}
function updateVoiceSamplingInputs(){
 const mode=el("voiceGenerationMode").value;
 el("voiceSeed").disabled=mode!=="seeded";
 for(const id of ["voiceTemperature","voiceTopP","voiceTopK"])el(id).disabled=mode==="greedy";
}
el("voiceGenerationMode").onchange=updateVoiceSamplingInputs;
function loadSettings(){
 const s=state.settings||{},p=state.voiceProfile||{};
 el("prompt").value=s.systemPrompt??"";
 el("temperature").value=s.llmTemperature??.5;
 el("maxTokens").value=s.llmMaxTokens??512;
 el("voice").value=p.speaker??s.ttsVoice??"Ryan";
 el("language").value=s.ttsLanguage??"Russian";
 el("voiceModeSetting").value=p.mode||"custom";
 el("voiceGenerationMode").value=p.generationMode||"seeded";
 el("voiceSeed").value=p.seed??42;
 el("voiceTemperature").value=p.temperature??0.75;
 el("voiceTopP").value=p.topP??0.9;
 el("voiceTopK").value=p.topK??40;
 el("voicePenalty").value=p.repetitionPenalty??1.05;
 el("voiceStyleSetting").value=p.style||"";
 el("voiceDescriptionSetting").value=p.description||"";
 updateVoiceSamplingInputs();
}
el("settingsForm").onsubmit=async event=>{
 event.preventDefault();const b=el("saveSettings");
 await busy(b,async()=>{
  const profile={...state.voiceProfile,
   mode:el("voiceModeSetting").value,speaker:el("voice").value.trim(),
   generationMode:el("voiceGenerationMode").value,seed:Number(el("voiceSeed").value),
   temperature:Number(el("voiceTemperature").value),topP:Number(el("voiceTopP").value),
   topK:Number(el("voiceTopK").value),repetitionPenalty:Number(el("voicePenalty").value),
   style:el("voiceStyleSetting").value.trim(),description:el("voiceDescriptionSetting").value.trim()};
  const savedProfile=await api("/api/setup/voice","PUT",profile);
  const settings={...state.settings,systemPrompt:el("prompt").value.trim(),
   llmTemperature:Number(el("temperature").value),llmMaxTokens:Number(el("maxTokens").value),
   ttsVoice:savedProfile.speaker,ttsLanguage:el("language").value.trim()};
  delete settings.hasDeviceToken;delete settings.hasHomeAssistantToken;
  await api("/api/settings","POST",settings);
  state.voiceProfile=savedProfile;state.settings=settings;
  el("settingsNote").textContent="Настройки сохранены.";
  toast("Настройки голоса и ассистента сохранены");
 })();
};
el("runLlm").onclick=busy(el("runLlm"),async()=>{el("llmAnswer").textContent="Генерация ответа…";el("llmBenchmark").textContent="Измерение…";const r=await api("/api/control/test/llm","POST",{prompt:el("testPrompt").value,model:null});el("llmAnswer").textContent=r.text||"(ответ пустой)";el("llmBenchmark").textContent="Первый фрагмент "+r.firstTokenMs+" мс · "+r.totalMs+" мс · "+r.tokensPerSecond+" ток/с"+(r.tokensExact?"":"*");toast("Тест завершён")});
el("runTts").onclick=busy(el("runTts"),async()=>{el("ttsBenchmark").textContent="Генерация WAV…";const response=await fetch("/api/control/test/tts",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({text:el("testPhrase").value,voice:null,model:null})});if(!response.ok){const x=await response.json().catch(()=>({}));throw Error(x.error||"HTTP "+response.status)}const wav=await response.blob();if(state.voiceUrl)URL.revokeObjectURL(state.voiceUrl);state.voiceUrl=URL.createObjectURL(wav);el("ttsPlayer").src=state.voiceUrl;el("downloadAudio").href=state.voiceUrl;el("downloadAudio").classList.remove("hidden");el("ttsBenchmark").textContent=response.headers.get("X-Synthesis-Ms")+" мс · RTF "+response.headers.get("X-RTF");await el("ttsPlayer").play().catch(()=>{});toast("Аудио готово")});
function deviceAddress(){let h=el("serverHost").value.trim().replace(/^(ws|wss|http|https):\/\//,"").replace(/\/.*$/,"").replace(/:\d+$/,"");el("wsAddress").textContent="ws://"+(h||"SERVER_IP")+":5055/ws"}
el("serverHost").oninput=deviceAddress;
async function copy(value){if(!navigator.clipboard?.writeText)throw Error("Копирование работает через HTTPS или localhost");await navigator.clipboard.writeText(value);toast("Скопировано")}
el("copyAddress").onclick=busy(el("copyAddress"),()=>copy(el("wsAddress").textContent));
el("copyKey").onclick=busy(el("copyKey"),async()=>{if(!state.key)throw Error("Сначала создайте новый ключ");await copy(state.key)});
el("createKey").onclick=busy(el("createKey"),async()=>{if(!confirm("Перевыпустить общий ключ сервера? Для новых подключений предыдущий ключ перестанет работать."))return;const r=await api("/api/control/device/key","POST");state.key=r.token;el("deviceKey").textContent=r.token;toast("Новый ключ создан")});
function renderSkills(){const box=el("skillsList");box.replaceChildren();for(const skill of state.skills){const label=textNode("label","","skill-option");const check=document.createElement("input");check.type="checkbox";check.dataset.skill=skill.name;check.checked=skill.enabled;const section=document.createElement("div");section.append(textNode("strong",skill.title),textNode("small",skill.description+(skill.sensitive?" · Действие требует разрешения":"")));label.append(check,section);box.append(label)}}
el("saveSkills").onclick=busy(el("saveSkills"),async()=>{const changes={};for(const input of document.querySelectorAll("#skillsList input"))changes[input.dataset.skill]=input.checked;state.skills=await api("/api/control/skills","PUT",changes);renderSkills();toast("Разрешения навыков обновлены")});
async function init(){const page=location.hash.slice(1);show(page in names?page:"overview");try{const [settings,skills,voiceProfile]=await Promise.all([api("/api/settings"),api("/api/control/skills"),api("/api/setup/voice")]);state.settings=settings;state.skills=skills;state.voiceProfile=voiceProfile;loadSettings();renderSkills()}catch(e){toast("Настройки: "+e.message,true)}await poll();await refreshConnections().catch(()=>{});await refreshModels().catch(()=>{})}
init();setInterval(poll,6000);setInterval(()=>{if(document.getElementById("logs").classList.contains("active"))refreshServerLogs().catch(()=>{});if(document.getElementById("models").classList.contains("active"))refreshModels().catch(()=>{})},5000);


/* Additional sections share the same layout and API. No nested pages or iframes. */
const modelState={files:[],selected:new Set(),revision:"main"};
function renderModelCards(){
 const box=el("modelCards");box.replaceChildren();
 for(const role of ["llm","stt","tts"]){
  const current=state.models?.status?.running?.[role];
  const card=textNode("article","","service");const head=textNode("div",role.toUpperCase(),"service-type");
  const model=state.models?.profiles?.[role];
  const modelName=(current?.modelPath||model?.modelPath||"").split("/").pop();
  card.append(head,textNode("div",current?.running?"Запущена":"Не запущена","service-value"),
    textNode("div",modelName||"Модель не выбрана","service-caption"));
  const actions=textNode("div","","model-card-actions");
  const edit=textNode("button","Параметры","button secondary");
  edit.onclick=()=>{el("profileRole").value=role;loadProfile();el("profileRole").scrollIntoView({behavior:"smooth",block:"center"})};
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
 renderModelCards();if(!document.activeElement?.closest(".model-editor"))loadProfile();
 const box=el("downloadsList");box.replaceChildren();
 for(const job of downloads.jobs||[]){
  const row=textNode("div","","download-row");row.append(textNode("strong",job.repo||job.id),
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
   select.onclick=()=>{el("profileRole").value="llm";loadProfile();el("profileModel").value=item.path;el("profileModel").scrollIntoView({behavior:"smooth",block:"center"});toast("Модель выбрана. Сохраните профиль перед запуском.")};
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
  }else actions.append(textNode("span",item.role==="tts"?"Кеш Hugging Face":"Кеш GigaAM","minor"));
  row.append(name,actions);files.append(row);
 }
 if(!files.children.length)files.textContent="Файлов пока нет";
}
el("profileRole").onchange=loadProfile;
el("profileSave").onclick=busy(el("profileSave"),async()=>{
 await api("/api/models/"+el("profileRole").value,"PUT",readProfile());await refreshModels();toast("Профиль сохранён")});
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
 await api("/api/models/downloads","POST",{repo:el("hfRepo").value.trim(),revision:el("hfRevision").value.trim()||"main",files:[...modelState.selected]});
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
 for(const item of (Array.isArray(results)?results:[]).slice(0,20)){
  const row=textNode("div","","search-row");const title=item.name||item.title||item.trackTitle||JSON.stringify(item).slice(0,100);
  row.append(textNode("span",title),textNode("button","Слушать","button secondary"));
  row.querySelector("button").onclick=()=>play(item).catch(e=>toast(e.message,true));box.append(row);
 }if(!box.children.length)box.textContent="Ничего не найдено";
}
el("searchRadio").onclick=busy(el("searchRadio"),async()=>{const r=await api("/api/radio/search?name="+encodeURIComponent(el("radioQuery").value.trim()));drawSearch(r,"radioResults",async x=>{const url=x.url||x.urlResolved;if(!url)throw Error("Нет адреса потока");el("mediaPlayer").src=url;await el("mediaPlayer").play().catch(()=>{})})});
el("searchMusic").onclick=busy(el("searchMusic"),async()=>{const r=await api("/api/yandex/search?query="+encodeURIComponent(el("musicQuery").value.trim()));drawSearch(r,"musicResults",async x=>{const id=x.id||x.trackId;if(!id)throw Error("Отсутствует ID трека");const r=await api("/api/yandex/tracks/"+encodeURIComponent(id)+"/playback");el("mediaPlayer").src=r.url;await el("mediaPlayer").play().catch(()=>{})})});
/* Chat directly via the device websocket */
let chatSocket=null,chatBuffer=[],chatSampleRate=24000;
function chatAppend(s){const box=el("chatHistory");box.textContent+=s;box.scrollTop=box.scrollHeight}
el("chatConnect").onclick=()=>{
 if(chatSocket?.readyState===1){chatSocket.close();return}
 const uri=(location.protocol==="https:"?"wss://":"ws://")+location.host+"/ws";
 chatSocket=new WebSocket(uri);chatSocket.binaryType="arraybuffer";el("chatStatus").textContent="Подключение";
 chatSocket.onopen=()=>{el("chatStatus").textContent="Подключено";el("chatConnect").textContent="Отключиться";el("chatSend").disabled=false;el("chatCancel").disabled=false};
 chatSocket.onclose=()=>{el("chatStatus").textContent="Отключено";el("chatConnect").textContent="Подключиться";el("chatSend").disabled=true;el("chatCancel").disabled=true};
 chatSocket.onerror=()=>el("chatStatus").textContent="Ошибка соединения";
 chatSocket.onmessage=event=>{
  if(event.data instanceof ArrayBuffer){chatBuffer.push(new Uint8Array(event.data));return}
  const msg=JSON.parse(event.data);
  if(msg.type==="transcript")chatAppend("\nВы: "+msg.text+"\n");
  else if(msg.type==="text")chatAppend(msg.delta||"");
  else if(msg.type==="audio_start"){chatBuffer=[];chatSampleRate=msg.sampleRate||24000}
  else if(msg.type==="audio_end"){
   const size=chatBuffer.reduce((n,x)=>n+x.length,0);const audio=new Uint8Array(size);let offset=0;
   for(const x of chatBuffer){audio.set(x,offset);offset+=x.length}
   const wav=new Uint8Array(44+audio.length),view=new DataView(wav.buffer);
   const write=(n,t)=>{for(let i=0;i<t.length;i++)wav[n+i]=t.charCodeAt(i)};
   write(0,"RIFF");view.setUint32(4,wav.length-8,true);write(8,"WAVE");write(12,"fmt ");
   view.setUint32(16,16,true);view.setUint16(20,1,true);view.setUint16(22,1,true);
   view.setUint32(24,chatSampleRate,true);view.setUint32(28,chatSampleRate*2,true);
   view.setUint16(32,2,true);view.setUint16(34,16,true);write(36,"data");view.setUint32(40,audio.length,true);wav.set(audio,44);
   if(el("chatAudio").dataset.url)URL.revokeObjectURL(el("chatAudio").dataset.url);
   const url=URL.createObjectURL(new Blob([wav],{type:"audio/wav"}));el("chatAudio").dataset.url=url;el("chatAudio").src=url;el("chatAudio").play().catch(()=>{});chatBuffer=[];
  }else if(msg.type==="error")chatAppend("\nОшибка: "+msg.message+"\n");
  else if(msg.type==="done")chatAppend("\n");
 };
};
function sendChat(){const s=el("chatInput").value.trim();if(!s||chatSocket?.readyState!==1)return;chatAppend("\nВы: "+s+"\nАссистент: ");chatSocket.send(JSON.stringify({type:"text",text:s}));el("chatInput").value=""}
el("chatSend").onclick=sendChat;el("chatInput").onkeydown=e=>{if(e.key==="Enter")sendChat()};el("chatCancel").onclick=()=>{if(chatSocket?.readyState===1)chatSocket.send(JSON.stringify({type:"cancel"}))};
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
