"use strict";
const el=id=>document.getElementById(id);
const state={catalog:[],selected:{},jobs:{},step:"llm",pcm:null,voiceUrl:null,recordUrl:null,key:"",starting:{},shown:{},last:{},installed:[],voiceProfile:null};
const captions={llm:"Интеллект",stt:"Слух",tts:"Голос"};
const steps=["llm","stt","tts","music","complete"];
function showError(message){alert(message)}
async function api(url,method="GET",data){const init={method,cache:"no-store"};if(data!==undefined){init.headers={"Content-Type":"application/json"};init.body=JSON.stringify(data)}
 const r=await fetch(url,init);const body=await r.json().catch(()=>({}));if(!r.ok)throw Error(body.error||body.message||"HTTP "+r.status);return body}
function show(step){
 if(!steps.includes(step))return;
 const outgoing=document.querySelector(".section.active"),incoming=document.getElementById(step);
 if(outgoing&&outgoing!==incoming){
   outgoing.classList.remove("active");
   if(!window.matchMedia("(prefers-reduced-motion: reduce)").matches){
     outgoing.classList.add("leaving");
     setTimeout(()=>outgoing.classList.remove("leaving"),360);
   }
 }
 incoming.classList.add("active");
 state.step=step;
 for(const elem of document.querySelectorAll(".step")){const ix=steps.indexOf(elem.dataset.step),now=steps.indexOf(step);elem.classList.toggle("current",ix===now);elem.classList.toggle("done",ix<now)}
 history.replaceState(null,"","#"+step);window.scrollTo({top:0,behavior:"smooth"})}
for(const btn of document.querySelectorAll("[data-next]"))btn.onclick=()=>show(btn.dataset.next);
for(const btn of document.querySelectorAll(".step[data-step]"))btn.onclick=()=>show(btn.dataset.step);
function modelCard(m){const b=document.createElement("button");b.type="button";b.className="model";b.dataset.model=m.id;
 const type=document.createElement("span");type.className="type";type.textContent=m.role==="llm"?"Языковая модель":m.role==="stt"?"Распознавание речи":"Синтез речи";
 const name=document.createElement("strong");name.textContent=m.name;const desc=document.createElement("small");desc.textContent=m.summary;
 const foot=document.createElement("span");foot.className="foot";
 foot.textContent=m.quality;
 b.append(type,name,desc,foot);
 const installed=state.installed.filter(item=>item.role===m.role&&(
   (m.role==="stt"&&item.repo==="salute-developers/GigaAM") ||
   (m.role==="tts"&&item.repo===m.repo) ||
   (m.role==="llm"&&item.path?.includes("/"+m.repo+"/"))));
 if(installed.length){
   const cached=installed.some(item=>item.source!=="manual-download");
   const saved=document.createElement("span");saved.className="tag";
   saved.textContent=cached?"Уже скачана":"Файлы на диске";
   saved.title=cached?"Модель найдена в рабочем кеше движка":"Файлы скачаны вручную; движок может потребовать собственный кеш";
   b.append(saved);
 }
 if(m.recommended){const label=document.createElement("span");label.className="tag";label.textContent="Рекомендуем";b.append(label)}
 b.onclick=()=>{state.selected[m.role]=m.id;renderCards(m.role);updateVoiceMode()};return b}
function renderCards(role){const box=el(role+"Models");box.replaceChildren();for(const m of state.catalog.filter(x=>x.role===role)){const b=modelCard(m);if(state.selected[role]===m.id)b.classList.add("selected");box.append(b)}}
async function refreshInstalled(){
 try{const data=await api("/api/models/downloads");state.installed=data.installed||[];for(const role of ["llm","stt","tts"])renderCards(role)}catch{}
}
function updateButtons(){for(const role of ["llm","stt","tts"]){const b=el("start"+role.toUpperCase().slice(0,1)+role.slice(1));if(!b)continue;const status=state.jobs[role]?.status;b.disabled=status==="running";b.textContent=status==="ready"?"Установить другую модель":status==="running"?"Установка выполняется…":"Установить и проверить"}}
async function start(role){if(state.starting[role]||state.jobs[role]?.status==="running")return;const modelId=state.selected[role];if(!modelId)throw Error("Сначала выберите модель");
 state.starting[role]=true;
 try{const input={modelId};
 if(role==="llm"){input.gpuUuid=el("gpuSelect").value||null;input.contextSize=Number(el("contextSelect").value);input.temperature=Number(el("temperatureSelect").value);input.modelUrl=el("customModelUrl").value.trim()||null;}
 else input.gpuUuid=el(role+"Gpu").value||null;const result=await api("/api/setup/"+role,"POST",input);state.jobs[role]=result;state.shown[role]=false;el("result"+role.charAt(0).toUpperCase()+role.slice(1)).classList.remove("visible");updateRole(role,result);updateButtons()}
 catch(e){showError("Не удалось начать установку: "+e.message)}finally{state.starting[role]=false}}
for(const role of ["llm","stt","tts"]){const button=el("start"+role.charAt(0).toUpperCase()+role.slice(1));button.onclick=()=>start(role)}
const titles={prepare:"Проверка системы",download:"Загрузка модели",install:"Установка зависимостей",runtime:"Подготовка движка",launch:"Запуск модели",loading:"Загрузка весов",test:"Проверка работы",ready:"Установка завершена",failed:"Установка прервана",cancelled:"Установка отменена",fallback:"Переключение на CPU",starting:"Подготовка"};
const stageSets={
 llm:[["prepare","Проверка системы"],["download","Загрузка модели"],["runtime","Подготовка llama.cpp"],["launch","Запуск сервиса"],["loading","Загрузка весов"],["test","Проверка ответа"]],
 stt:[["prepare","Проверка системы"],["install","Зависимости Python"],["launch","Запуск сервиса"],["loading","Загрузка модели"],["test","Проверка API"]],
 tts:[["prepare","Проверка системы"],["install","Зависимости Python"],["launch","Запуск сервиса"],["loading","Загрузка модели"],["test","Синтез тестовой фразы"]]
};
function renderMilestones(role,job){
 const cap=role[0].toUpperCase()+role.slice(1),box=el("steps"+cap);
 const steps=stageSets[role],complete=new Set(job.completedSteps||[]);
 const current=job.step==="fallback"?"loading":job.step;
 const old=box.dataset.state||"",next=job.status+"|"+job.step+"|"+[...complete].join(",");
 if(old===next)return;box.dataset.state=next;box.replaceChildren();
 for(const [id,title] of steps){
   const done=complete.has(id)||job.status==="ready";
   const active=!done&&current===id;
   const failed=active&&job.status==="failed";
   const item=document.createElement("div");item.className="install-step"+(done?" complete":failed?" failed":active?" active":"");
   const symbol=document.createElement("span");symbol.className="step-state";symbol.textContent=done?"✓":failed?"×":active?"•":"";
   const name=document.createElement("span");name.textContent=title;
   item.append(symbol,name);box.append(item);
 }
}
const stageNums={starting:0,prepare:2,download:18,install:18,runtime:45,launch:63,loading:78,test:94,ready:100};
function fmt(n){if(!n)return"0 МБ";if(n>1073741824)return(n/1073741824).toFixed(2)+" ГБ";return(n/1048576).toFixed(0)+" МБ"}
function updateRole(role,job){
 const cap=role[0].toUpperCase()+role.slice(1);
 el("progress"+cap).classList.add("visible");
 el("title"+cap).textContent=titles[job.step]||"Настраиваем";
 el("desc"+cap).textContent=job.error||job.description||"Подготовка";
 const steps=stageSets[role],done=new Set(job.completedSteps||[]);
 let progress=job.status==="ready"?100:Math.round(100*steps.filter(x=>done.has(x[0])).length/steps.length);
 if(job.step==="download"&&job.total>0)
   progress=Math.min(99,progress+Math.round(100*(job.bytes/job.total)/steps.length));
 el("bar"+cap).style.width=Math.max(1,Math.min(progress,100))+"%";
 renderMilestones(role,job);
 el("bytes"+cap).textContent=job.total?Math.min(100,Math.floor(100*job.bytes/job.total))+"%":"Выполняется";
 if(role==="llm"){
  const hasFile=job.total>0;
  const transferred=hasFile?Math.min(100,Math.max(0,Math.floor(100*job.bytes/job.total))):0;
  el("transferPanelLlm").classList.toggle("visible",hasFile);
  el("transferPctLlm").textContent=hasFile?transferred+"%":"—";
  el("transferBarLlm").style.width=transferred+"%";
  el("transferTrackLlm").setAttribute("aria-valuenow",String(transferred));
 }
 el("stage"+cap).textContent=job.status==="ready"?"ГОТОВО":job.status==="failed"?"ОШИБКА":titles[job.step]||"Выполняется";
 const log=el("logs"+cap),nearBottom=log.scrollHeight-log.scrollTop-log.clientHeight<70;
 const content=(job.logs||[]).slice(-125).join("\n");
 if(log.textContent!==content){log.textContent=content;if(nearBottom)log.scrollTop=log.scrollHeight}
 const ring=el("ring"+cap);
 ring.classList.toggle("ready",job.status==="ready");
 ring.classList.toggle("failed",job.status==="failed");
 el("progress"+cap).classList.toggle("error",job.status==="failed");
 if(job.status==="failed"){el("desc"+cap).textContent=job.error||"Проверьте технические подробности";updateButtons()}
 if(job.status==="ready"&&!state.shown[role]){state.shown[role]=true;el("result"+cap).classList.add("visible");if(role==="llm")llmResult(job);if(role==="stt")el("sttRecognized").textContent="Модель готова. Запишите тестовую фразу.";if(role==="tts")el("ttsMetrics").textContent="Первичная проверка завершена. Выберите голос и прослушайте результат.";}
 if(job.status==="ready")el("result"+cap).classList.add("visible");
 if(job.status==="ready"&&!state.last[role+"cache"]){state.last[role+"cache"]=true;void refreshInstalled()}
 if(job.status!=="ready")state.last[role+"cache"]=false;
}
function llmResult(job){let data={};try{data=JSON.parse(job.testResult||"{}")}catch{}
 el("answerLlm").textContent=data.text||"Модель успешно ответила на тестовый запрос";
 const metrics=el("metricsLlm");metrics.replaceChildren();
 const dataItems=[["Первый фрагмент",data.firstTokenMs!=null?data.firstTokenMs+" мс":"—"],["Весь ответ",data.totalMs!=null?data.totalMs+" мс":"—"],[data.tokensExact?"Скорость":"Оценка скорости",data.tokensPerSecond!=null?data.tokensPerSecond+" ток/с":"—"]];
 for(const [label,value] of dataItems){const x=document.createElement("span");x.className="pill";x.textContent=label+" · "+value;metrics.append(x)}
}
async function poll(){try{
 const result=await api("/api/setup/status");
 el("dot").classList.add("on");el("serverState").textContent="Сервер на связи";
 for(const job of result.jobs||[]){state.jobs[job.role]=job;updateRole(job.role,job)}
 if(state.step==="llm"&&state.jobs.llm?.status==="ready")sampleGpu();
 updateButtons();
 }catch(e){el("dot").classList.remove("on");el("serverState").textContent="Связь с сервером потеряна"}}
async function sampleGpu(){
 try{
  const result=await api("/api/models"),list=result.status?.gpus||[];
  const selected=el("gpuSelect").value;
  const active=selected?list.find(g=>g.uuid===selected):list.slice().sort((a,b)=>(b.usedMiB||0)-(a.usedMiB||0))[0];
  if(!active)return;
  const usage=active.utilization??0,used=active.usedMiB??0,total=active.totalMiB??0;
  el("gpuBenchmark").textContent="GPU "+active.index+" · загрузка "+usage+"% · память "+used+"/"+total+" МиБ · "+(active.temperature??"—")+" °C";
  el("gpuBenchmarkBar").style.width=Math.min(100,100*used/Math.max(total,1))+"%";
 }catch{}
}
el("testLlm").onclick=async()=>{
 const b=el("testLlm");b.disabled=true;el("manualLlm").classList.remove("hidden");el("manualLlm").textContent="Проверяем ответ…";
 const timer=setInterval(sampleGpu,800);
 try{
  await sampleGpu();
  const r=await api("/api/control/test/llm","POST",{prompt:el("testPrompt").value,model:"local"});
  el("manualLlm").textContent=r.text+"\n\nПервый фрагмент: "+r.firstTokenMs+" мс · Время: "+r.totalMs+" мс · "+(r.tokensExact?"Скорость: ":"Оценка: ")+r.tokensPerSecond+" ток/с";
 }catch(e){el("manualLlm").textContent=e.message}
 finally{clearInterval(timer);await sampleGpu();b.disabled=false}
};
function updateVoiceMode(){
 const choice=state.selected.tts||"qwen-tts-fast-06";
 const design=choice.includes("design");
 const style=choice.includes("17")&&!design;
 const fast=choice.includes("-fast-");
 el("designedVoice").classList.toggle("hidden",!design);
 el("presetVoice").classList.toggle("hidden",design);
 el("customStyle").classList.toggle("hidden",!style);
 el("ttsChoiceHelp").textContent=design
   ? "Голос создаётся по описанию. Можно менять тембр, характер и подачу; новый запуск может дать другой результат."
   :style
   ? "Выбирается готовый тембр. Можно отдельно изменить манеру речи и интонации."
   :"Выбирается один из готовых голосов. Произвольное описание тембра не поддерживается.";
 el("ttsChoiceHelp").dataset.engine=fast?"faster":"official";
}
function updateRecordingUi(which, elapsed) {
 const progress = Math.min(100, Math.round(elapsed * 20));
 el("recordMeter"+which).classList.add("active");
 el("recordTime"+which).textContent=elapsed.toFixed(1).replace(".",",")+" / 5,0 с";
 el("recordBar"+which).style.width=progress+"%";
 el("recordTrack"+which).setAttribute("aria-valuenow",String(progress));
}
el("recordStt").onclick=async()=>{
 const btn=el("recordStt");btn.disabled=true;el("transcribeStt").disabled=true;state.pcm=null;
 el("sttSignal").textContent="";el("sttMetrics").textContent="";
 updateRecordingUi("Stt",0);el("sttRecognized").textContent="Запись…";
 try{
  const data=await OpenAssistantAudioTest.record({onProgress:info=>updateRecordingUi("Stt",info.elapsed)});
  state.pcm=data.pcm;
  if(state.recordUrl)URL.revokeObjectURL(state.recordUrl);
  state.recordUrl=URL.createObjectURL(data.wav);
  el("recordPlayer").src=state.recordUrl;
  el("sttRecognized").textContent="Запись готова. Нажмите «Распознать».";
  el("sttSignal").textContent="RMS: "+data.rmsPercent.toFixed(2)+"% · Пик: "+data.peakPercent.toFixed(1)+"% · "+
    data.duration.toFixed(2)+" с.";
  if(data.signalStatus==="silent")el("sttSignal").textContent+=" Микрофон почти не передаёт звук.";
  el("transcribeStt").disabled=false;
 }catch(e){el("sttRecognized").textContent="Ошибка записи: "+e.message}
 finally{btn.disabled=false;el("recordMeterStt").classList.remove("active")}
};
el("transcribeStt").onclick=async()=>{
 const btn=el("transcribeStt");btn.disabled=true;el("sttRecognized").textContent="Распознавание…";
 try{
  const result=await OpenAssistantAudioTest.transcribe(state.pcm);
  const shown=OpenAssistantAudioTest.description(result);
  el("sttRecognized").textContent=shown.text;
  el("sttSignal").textContent=shown.quality;
  el("sttMetrics").textContent=shown.metrics;
 }catch(e){el("sttRecognized").textContent="Ошибка: "+e.message}
 finally{btn.disabled=false}
};
function updateTtsSamplingControls(){
 const mode=el("ttsGenerationMode").value;
 el("ttsSeed").disabled=mode!=="seeded";
 for(const id of ["ttsTemperature","ttsTopP","ttsTopK"])el(id).disabled=mode==="greedy";
}
el("ttsGenerationMode").onchange=updateTtsSamplingControls;
function readVoiceProfile(){
 const design=state.selected.tts?.includes("design")??false;
 return {...state.voiceProfile,mode:design?"design":"custom",
  speaker:el("voiceSelect").value,description:el("voiceDescription").value.trim(),
  style:el("voiceStyle").value.trim(),generationMode:el("ttsGenerationMode").value,
  seed:Number(el("ttsSeed").value),temperature:Number(el("ttsTemperature").value),
  topP:Number(el("ttsTopP").value),topK:Number(el("ttsTopK").value),
  repetitionPenalty:Number(el("ttsPenalty").value)};
}
async function saveVoiceProfile(){
 state.voiceProfile=await api("/api/setup/voice","PUT",readVoiceProfile());
 el("ttsSavedMessage").textContent="Настройки сохранены";
 return state.voiceProfile;
}
el("ttsSaveVoice").onclick=async()=>{
 const button=el("ttsSaveVoice");button.disabled=true;
 try{await saveVoiceProfile()}catch(error){el("ttsSavedMessage").textContent=error.message}
 finally{button.disabled=false}
};
el("testTts").onclick=async()=>{const b=el("testTts");b.disabled=true;el("ttsMetrics").textContent="Синтезируем выбранным голосом…";
 try{
 const design=state.selected.tts?.includes("design");
 await saveVoiceProfile();
 const res=await fetch("/api/control/test/tts",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({text:el("ttsPhrase").value,voice:state.selected.tts?.includes("design")?null:el("voiceSelect").value,model:null})});
 if(!res.ok){const d=await res.json().catch(()=>({}));throw Error(d.error||"Ошибка TTS")}
 const blob=await res.blob();if(state.voiceUrl)URL.revokeObjectURL(state.voiceUrl);state.voiceUrl=URL.createObjectURL(blob);
 el("ttsPlayer").src=state.voiceUrl;el("downloadWav").href=state.voiceUrl;el("downloadWav").classList.remove("hidden");
 el("ttsMetrics").textContent="Синтез: "+res.headers.get("X-Synthesis-Ms")+" мс · Длительность: "+res.headers.get("X-Audio-Seconds")+" с · RTF "+res.headers.get("X-RTF");
 await el("ttsPlayer").play().catch(()=>{});
 }catch(e){el("ttsMetrics").textContent=e.message}finally{b.disabled=false}};
async function init(){try{
 state.catalog=await api("/api/setup/catalog");
 for(const role of ["llm","stt","tts"]){state.selected[role]=state.catalog.find(m=>m.role===role&&m.default)?.id;renderCards(role)}
 const profile=await api("/api/setup/voice").catch(()=>null);
 if(profile){
  state.voiceProfile=profile;
  el("voiceSelect").value=profile.speaker||"Ryan";
  el("voiceDescription").value=profile.description||el("voiceDescription").value;
  el("voiceStyle").value=profile.style||"";
  el("ttsGenerationMode").value=profile.generationMode||"seeded";
  el("ttsSeed").value=profile.seed??42;
  el("ttsTemperature").value=profile.temperature??0.75;
  el("ttsTopP").value=profile.topP??0.9;
  el("ttsTopK").value=profile.topK??40;
  el("ttsPenalty").value=profile.repetitionPenalty??1.05;
  if(profile.mode==="design"){state.selected.tts="qwen-tts-fast-design";renderCards("tts")}
 }
 updateTtsSamplingControls();
 await refreshInstalled();
 await poll();
 const gpu=await api("/api/models").catch(()=>null);const cards=gpu?.status?.gpus||[];
 const best=cards.slice().sort((a,b)=>(b.totalMiB-b.usedMiB)-(a.totalMiB-a.usedMiB))[0];
 if(best)el("gpuInfo").textContent="Доступно GPU: "+cards.length+". По умолчанию будет выбрана подходящая карта.";
 else el("gpuInfo").textContent="GPU не обнаружен. Возможен запуск на CPU.";
 const picker=el("gpuSelect");
 for(const card of cards){
  const name="GPU "+card.index+" · "+card.name+" · "+(card.totalMiB-card.usedMiB)+" МиБ свободно";
  picker.add(new Option(name,card.uuid));
  el("sttGpu").add(new Option(name,card.uuid));
  el("ttsGpu").add(new Option(name,card.uuid));
 }
 updateVoiceMode();
 }catch(e){el("serverState").textContent="Ошибка подключения";showError(e.message)}
 const hash=location.hash.slice(1);if(steps.includes(hash))show(hash);
}
init();setInterval(poll,2000);


/* Optional music connection during setup */
let setupAuthSession=null,setupAuthEpoch=0,setupAuthTimer=null;
function stopSetupAuth(){setupAuthEpoch++;setupAuthSession=null;clearTimeout(setupAuthTimer);setupAuthTimer=null;el("setupMusicAuth").classList.add("hidden")}
async function setupMusicStatus(){
 const v=await api("/api/integrations");
 el("setupMusicState").textContent=v.yandexMusic?.connected?"Аккаунт подключён: "+(v.yandexMusic.account||""):"Не подключено";
 if(v.yandexMusic?.connected)el("setupMusicLogin").textContent="Переподключить";
}
el("setupMusicSkip").onclick=()=>{stopSetupAuth();show("complete")};
el("setupMusicLogin").onclick=async()=>{
 const btn=el("setupMusicLogin");btn.disabled=true;stopSetupAuth();
 try{
  const v=await api("/api/integrations/yandex/device/start","POST");
  const epoch=++setupAuthEpoch;setupAuthSession=v.session;
  el("setupMusicCode").textContent=v.userCode;el("setupMusicUrl").href=v.verificationUrl;
  el("setupMusicAuth").classList.remove("hidden");
  const poll=async()=>{
   if(epoch!==setupAuthEpoch||!setupAuthSession)return;
   try{
    const result=await api("/api/integrations/yandex/device/poll","POST",{session:setupAuthSession});
    if(epoch!==setupAuthEpoch)return;
    if(result.state==="connected"){stopSetupAuth();await setupMusicStatus();show("complete");return}
    if(result.state==="expired"||result.state==="error"){el("setupMusicProgress").textContent=result.message;return}
    el("setupMusicProgress").textContent=result.message||"Ожидание подтверждения";
    setupAuthTimer=setTimeout(poll,Math.max(5,Number(result.interval)||5)*1000);
   }catch(e){el("setupMusicProgress").textContent=e.message;setupAuthTimer=setTimeout(poll,10000)}
  };
  setupAuthTimer=setTimeout(poll,Math.max(5,Number(v.interval)||5)*1000);
 }catch(e){alert("Яндекс Музыка: "+e.message)}finally{btn.disabled=false}
};
el("setupMusicCancel").onclick=async()=>{stopSetupAuth();await api("/api/integrations/yandex/device/cancel","POST").catch(()=>{})};
el("setupMusicSaveToken").onclick=async()=>{const b=el("setupMusicSaveToken");b.disabled=true;try{await api("/api/integrations/yandex/token","POST",{token:el("setupMusicToken").value.trim()});el("setupMusicToken").value="";stopSetupAuth();await setupMusicStatus();show("complete")}catch(e){alert(e.message)}finally{b.disabled=false}};
for(const btn of document.querySelectorAll('[data-step="music"],[data-next="music"]'))btn.addEventListener("click",()=>setupMusicStatus().catch(e=>el("setupMusicState").textContent=e.message));
