"use strict";
const $=id=>document.getElementById(id);
const state={settings:null,models:null,downloads:null,devices:null,system:null,skills:[],catalog:[],repoFiles:[],selectedFiles:new Set(),selectedRepo:"",microphone:null,lastLlm:"",key:"",busy:false};
function toast(message,error=false){const el=$("toast");el.textContent=message;el.className="toast show"+(error?" error":"");clearTimeout(toast.timer);toast.timer=setTimeout(()=>el.className="toast",6500)}
async function api(url,method="GET",payload){const init={method,cache:"no-store"};if(payload!==undefined){init.headers={"Content-Type":"application/json"};init.body=JSON.stringify(payload)}const response=await fetch(url,init);const d=await response.json().catch(()=>({}));if(!response.ok)throw Error(d.error||d.message||"HTTP "+response.status);return d}
function node(tag,text,cls){const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(cls)n.className=cls;return n}
function run(button,fn){return async()=>{button.disabled=true;try{await fn()}catch(e){toast(e.message,true)}finally{button.disabled=false}}}
function show(page){for(const el of document.querySelectorAll(".screen"))el.classList.toggle("active",el.id===page);for(const el of document.querySelectorAll("#mainNav button"))el.classList.toggle("active",el.dataset.page===page);const titles={overview:["Обзор системы","Компоненты, модели и подключённые устройства"],wizard:["Первоначальная настройка","Шесть шагов от чистого сервера до первого разговора"],models:["Каталог и управление моделями","Скачивание, GPU и llama.cpp"],assistant:["Ассистент и голос","Настройки речи и генерации"],skills:["Навыки и аккаунты","Подключение инструментов к LLM"],devices:["Колонка и устройства","WebSocket, ключи и активные соединения"],tests:["Тесты производительности","LLM, STT, TTS и микрофон"],chat:["Диалог","Сеанс с ассистентом"],media:["Музыка и радио","Проверка воспроизведения"],advanced:["Дополнительные настройки","Параметры сервера"]};const t=titles[page]||titles.overview;$("pageTitle").textContent=t[0];$("pageSubtitle").textContent=t[1];history.replaceState(null,"","#"+page)}
for(const b of document.querySelectorAll("#mainNav button"))b.onclick=()=>show(b.dataset.page);
for(const b of document.querySelectorAll("[data-jump]"))b.onclick=()=>show(b.dataset.jump);
function fmtSize(n){if(!Number.isFinite(n))return"—";if(n>=1073741824)return(n/1073741824).toFixed(2)+" ГБ";if(n>=1048576)return(n/1048576).toFixed(1)+" МБ";return Math.round(n/1024)+" КБ"}
function profile(role){return state.models?.status?.running?.[role]||null}
function health(role){return state.models?.status?.api?.[role]?.state||"unknown"}
function statusLabel(role){const p=profile(role);return p?.running?"Запущен":"Не запущен"}
function percent(a,b){return b>0?Math.min(100,Math.max(0,100*a/b)):0}
function renderOverview(){
 const status=state.models?.status;
 for(const role of ["llm","stt","tts"]){const key=role[0].toUpperCase()+role.slice(1);$("overview"+key).textContent=statusLabel(role);$("overview"+key+"Health").textContent="HTTP: "+health(role)+(profile(role)?.pid?" · PID "+profile(role).pid:"")}
 const cards=$("gpuDashboard");cards.replaceChildren();
 for(const gpu of status?.gpus||[]){const el=node("div",undefined,"gpu");el.append(node("strong","GPU "+gpu.index+" · "+gpu.name),node("div",(gpu.temperature??"N/A")+" °C · "+(gpu.utilization??"N/A")+"% загрузка","muted"));const bar=node("div",undefined,"progress"),fill=node("div");fill.style.width=percent(gpu.usedMiB,gpu.totalMiB)+"%";bar.append(fill);el.append(bar,node("div",(gpu.usedMiB??"—")+"/"+(gpu.totalMiB??"—")+" MiB · "+(gpu.powerWatts??"N/A")+" Вт","muted"));cards.append(el)}
 if(!cards.children.length)cards.append(node("p","Нет данных nvidia-smi. Проверь драйвер и GPU."));
 const specs=[["CPU",state.system?.cpuThreads+" логических потоков"],["Память сервера",state.system?.serverMemoryMiB+" МиБ"],["Свободно на диске",state.system?.diskFreeGiB+" ГиБ"]];const systemBox=$("systemDashboard");systemBox.replaceChildren();for(const [name,value] of specs){const card=node("div",undefined,"card");card.append(node("small",name),node("div",value.includes("undefined")?"—":value,"metric"));systemBox.append(card)}
 $("deviceCount").textContent=state.devices?.count??"—";
 $("downloadCount").textContent=(state.downloads?.jobs||[]).filter(j=>["queued","downloading"].includes(j.state)).length;
 $("serverChip").textContent="Сервер доступен";
 $("wizardState").textContent=["GPU: "+(status?.gpus?.length??0),
 "LLM: "+statusLabel("llm")+", API "+health("llm"),
 "STT: "+statusLabel("stt")+", API "+health("stt"),
 "TTS: "+statusLabel("tts")+", API "+health("tts"),
 "Колонки: "+(state.devices?.count??0),
 "Скачивания: "+(state.downloads?.jobs?.length??0)].join("\n");
 $("testApiStatus").textContent=["LLM "+health("llm"),"STT "+health("stt"),"TTS "+health("tts")].join("\n");
 const l=profile("llm");$("llamaStatus").textContent=l?"PID "+l.pid+" · "+(l.running?"работает":"завершён, код "+l.exitCode)+"\nAPI: "+health("llm")+"\n"+(l.logs||[]).slice(-10).join("\n"):"LLM пока не запущена из этой панели";
 renderDevices();
}
function renderDevices(){const el=$("devicesPanel");el.replaceChildren();const list=state.devices?.connections||[];if(!list.length){el.textContent="Активных подключений нет";return}for(const d of list)el.append(node("div",d.id+" · "+d.remote+" · "+new Date(d.connectedAt).toLocaleString("ru-RU"),"line"))}
function renderDownloads(){const el=$("downloadsPanel");el.replaceChildren();const jobs=state.downloads?.jobs||[];if(!jobs.length){el.textContent="Нет загрузок";return}
 for(const j of jobs){const wrap=node("div",undefined,"line");wrap.append(node("strong",j.repo),node("div",j.state+" · "+j.filesDone+"/"+j.filesTotal+" файлов","muted"));const bar=node("div",undefined,"progress"),fill=node("div");fill.style.width=percent(j.bytes,j.total)+"%";bar.append(fill);wrap.append(bar,node("div",fmtSize(j.bytes)+" / "+fmtSize(j.total)+" · "+(j.error||j.file||""),"muted"));if(["queued","downloading"].includes(j.state)){const b=node("button","Отменить","warn");b.onclick=run(b,async()=>{await api("/api/models/downloads/"+j.id+"/cancel","POST");await poll()});wrap.append(b)}el.append(wrap)}
}
function renderInstalled(){const box=$("installedFiles"),select=$("llamaInstalled"),keep=select.value;box.replaceChildren();select.replaceChildren(new Option("Выбрать скачанный GGUF…",""));const files=state.downloads?.files||[];for(const file of files.slice(0,1000)){const b=node("button",file.split("/").slice(-3).join("/"),"ghost");b.style.cssText="display:block;text-align:left;margin:4px 0;width:100%;word-break:break-all";b.onclick=()=>{$("llamaModel").value=file;toast("Путь модели выбран")};box.append(b);if(file.toLowerCase().endsWith(".gguf"))select.add(new Option(file.split("/").slice(-3).join("/"),file))}if([...select.options].some(x=>x.value===keep))select.value=keep;if(!files.length)box.append(node("p","Пока нет файлов моделей"))}
function refreshGpuChoice(){const container=$("llamaGpus"),previous=[...container.querySelectorAll("input:checked")].map(x=>x.value),had=!!container.querySelector("input");container.replaceChildren();for(const gpu of state.models?.status?.gpus||[]){const label=node("label",undefined,"check"),input=node("input");input.type="checkbox";input.value=gpu.uuid;input.checked=had?previous.includes(gpu.uuid):false;label.append(input,node("span","GPU "+gpu.index+" · "+gpu.name+" · "+gpu.totalMiB+" МиБ"));container.append(label)}if(!container.children.length)container.textContent="Нет данных GPU. Без ограничений CUDA движок увидит все доступные карты."}
let polling=false;
async function poll(){if(polling)return;polling=true;try{const [models,downloads,devices,system]=await Promise.all([api("/api/models"),api("/api/models/downloads"),api("/api/control/devices"),api("/api/control/system")]);const old=state.downloads?.files?.join("\n");state.models=models;state.downloads=downloads;state.devices=devices;state.system=system;renderOverview();renderDownloads();if(old!==downloads.files?.join("\n"))renderInstalled();refreshGpuChoice()}catch(e){$("serverChip").textContent="Ошибка соединения";toast("Мониторинг: "+e.message,true)}finally{polling=false}}
function renderCatalog(){const el=$("catalogItems");el.replaceChildren();for(const m of state.catalog){const b=node("button",undefined);b.append(node("strong",m.name),node("small",m.role.toUpperCase()+" · "+m.repo));b.onclick=()=>{for(const btn of el.children)btn.classList.remove("selected");b.classList.add("selected");$("hfRepo").value=m.repo;$("hfRevision").value="main";state.selectedRepo=m.role;toast(m.hint)};el.append(b)}}
function renderFiles(){const el=$("hfFiles");el.replaceChildren();for(const f of state.repoFiles){const l=node("label",undefined,"check"),check=node("input"),text=node("span",f.path+" · "+fmtSize(f.size));check.type="checkbox";check.checked=state.selectedFiles.has(f.path);check.onchange=()=>{check.checked?state.selectedFiles.add(f.path):state.selectedFiles.delete(f.path);selectionSummary()};l.append(check,text);el.append(l)}if(!state.repoFiles.length)el.append(node("p","Файлы не загружены"));selectionSummary()}
function selectionSummary(){$("hfSelectionInfo").textContent=state.selectedFiles.size+" из "+state.repoFiles.length+" файлов, "+fmtSize(state.repoFiles.filter(f=>state.selectedFiles.has(f.path)).reduce((n,f)=>n+(f.size||0),0))}
$("hfInspect").onclick=run($("hfInspect"),async()=>{const d=await api("/api/models/hub?repo="+encodeURIComponent($("hfRepo").value.trim())+"&revision="+encodeURIComponent($("hfRevision").value.trim()));state.repoFiles=d.files;state.selectedFiles=new Set();const gguf=d.files.filter(f=>f.path.toLowerCase().endsWith(".gguf"));const preferred=gguf.find(f=>/Q8_0/i.test(f.path))||gguf.find(f=>/F16|BF16/i.test(f.path));if(state.selectedRepo==="llm"&&preferred)state.selectedFiles.add(preferred.path);if(state.selectedRepo==="stt"||state.selectedRepo==="tts")state.selectedFiles=new Set(d.files.filter(f=>!f.path.endsWith(".gitattributes")&&!f.path.endsWith("README.md")).map(f=>f.path));renderFiles();toast("Ревизия "+d.commit.slice(0,9)+" · проверьте выбор файлов")});
$("hfSelectAll").onclick=()=>{state.selectedFiles=new Set(state.repoFiles.map(x=>x.path));renderFiles()};
$("hfSelectNone").onclick=()=>{state.selectedFiles.clear();renderFiles()};
$("hfDownload").onclick=run($("hfDownload"),async()=>{if(!state.selectedFiles.size)throw Error("Сначала выберите файлы");const result=await api("/api/models/downloads","POST",{repo:$("hfRepo").value.trim(),revision:$("hfRevision").value.trim(),files:[...state.selectedFiles]});toast("Загрузка началась: "+result.path);await poll()});
$("hfSaveToken").onclick=run($("hfSaveToken"),async()=>{await api("/api/models/hf-token","POST",{token:$("hfToken").value});$("hfToken").value="";toast("Токен Hugging Face сохранён")});
$("hfRemoveToken").onclick=run($("hfRemoveToken"),async()=>{await api("/api/models/hf-token","DELETE");toast("Токен Hugging Face удалён")});
$("llamaInstalled").onchange=()=>{if($("llamaInstalled").value)$("llamaModel").value=$("llamaInstalled").value};
$("llamaScan").onclick=run($("llamaScan"),async()=>{const r=await api("/api/control/llama/scan");if(r.found?.length){$("llamaExe").value=r.found[0];toast("Найден llama-server: "+r.found[0])}else toast("llama-server не найден в стандартных каталогах. Укажите путь вручную.",true)});
$("llamaStart").onclick=run($("llamaStart"),async()=>{const input={executable:$("llamaExe").value.trim(),modelPath:$("llamaModel").value.trim(),gpuUuids:[...$("llamaGpus").querySelectorAll("input:checked")].map(x=>x.value),contextSize:Number($("llamaContext").value),gpuLayers:Number($("llamaLayers").value),threads:Number($("llamaThreads").value),port:Number($("llamaPort").value),parallel:Number($("llamaParallel").value)};await api("/api/control/llama/start","POST",input);toast("llama.cpp запущен. Ожидайте загрузку весов и готовность API.");await poll()});
$("llamaStop").onclick=run($("llamaStop"),async()=>{await api("/api/models/llm/stop","POST");toast("LLM остановлена");await poll()});

const PRESETS={
 assistant:"Ты голосовой ассистент платформы OpenAssistant, русскоязычный голосовой ассистент. Отвечай ясно, короткими предложениями, которые удобно произносить вслух. Используй доступные инструменты только для действий пользователя, передавай вызовы через function calling и не утверждай, что действие выполнено, до получения результата инструмента. Не выдумывай факты, не раскрывай ключи и не произноси служебный JSON.",
 brief:"Ты голосовой ассистент. Отвечай по-русски максимально кратко. Сначала дай конкретный ответ. Если нужно выполнить действие, вызови разрешённый инструмент и подтверди результат только после фактического выполнения. Не говори о недоступных возможностях.",
 home:"Ты голосовой ассистент платформы OpenAssistant, домашний голосовой ассистент. Основной язык русский. Для получения сведений об умном доме используй ha_get_state; для изменения состояния устройств используй ha_call_service только при явной просьбе. Для музыки используй music_search и music_play. Результаты проверяй по ответам инструментов. Не выдумывай выполненных команд. Отвечай естественно, короткими фразами."
};
const settingIds=["systemPrompt","llmTemperature","llmTopP","llmTopK","llmRepeatPenalty","llmMaxTokens","ttsVoice","ttsLanguage","llmUrl","llmModelName","sttUrl","sttModelName","ttsUrl","ttsModelName"];
const settingKeys=["systemPrompt","llmTemperature","llmTopP","llmTopK","llmRepeatPenalty","llmMaxTokens","ttsVoice","ttsLanguage","llmUrl","llmModel","sttUrl","sttModel","ttsUrl","ttsModel"];
function loadSettings(){for(let i=0;i<settingIds.length;i++)$(settingIds[i]).value=state.settings?.[settingKeys[i]]??""}
function settingsPayload(){const p={...state.settings};delete p.hasDeviceToken;delete p.hasHomeAssistantToken;for(let i=0;i<settingIds.length;i++){const id=settingIds[i],key=settingKeys[i],value=$(id).value;p[key]=["llmTemperature","llmTopP","llmTopK","llmRepeatPenalty","llmMaxTokens"].includes(key)?Number(value):value}return p}
async function saveSettings(){const body=settingsPayload();await api("/api/settings","POST",body);state.settings=body;toast("Настройки ассистента сохранены. Новые запросы используют обновлённые параметры.")}
$("promptPreset").onchange=()=>{const name=$("promptPreset").value;if(PRESETS[name])$("systemPrompt").value=PRESETS[name]};
for(const id of ["saveAssistant","saveVoice","saveEndpoints"])$(id).onclick=run($(id),saveSettings);
function renderSkills(){const box=$("skillList");box.replaceChildren();for(const skill of state.skills){const row=node("label",undefined,"check"),check=node("input"),details=node("span");check.type="checkbox";check.dataset.skill=skill.name;check.checked=skill.enabled;details.append(node("strong",skill.title),node("div",skill.description,"muted"));if(skill.sensitive)details.append(node("small","Требует отдельного разрешения; изменяет устройства умного дома"));row.append(check,details);box.append(row)}}
$("saveSkills").onclick=run($("saveSkills"),async()=>{const selected={};for(const input of document.querySelectorAll("#skillList input"))selected[input.dataset.skill]=input.checked;state.skills=await api("/api/control/skills","PUT",selected);renderSkills();toast("Настройки навыков сохранены")});
function updateWs(){let host=$("deviceHost").value.trim().replace(/^wss?:\/\//,"").replace(/\/.*$/,"");$("deviceWs").textContent="ws://"+(host||"SERVER_IP")+":5055/ws"}
$("deviceHost").oninput=updateWs;
async function copyText(value){if(!navigator.clipboard?.writeText)throw Error("Буфер обмена требует HTTPS или localhost");await navigator.clipboard.writeText(value);toast("Скопировано")}
$("copyWs").onclick=run($("copyWs"),()=>copyText($("deviceWs").textContent));
$("copyKey").onclick=run($("copyKey"),async()=>{if(!state.key)throw Error("Сначала создайте новый ключ");await copyText(state.key)});
$("generateKey").onclick=run($("generateKey"),async()=>{if(!confirm("Сменить ключ устройства? Для новых подключений старый ключ перестанет работать."))return;const result=await api("/api/control/device/key","POST");state.key=result.token;$("pairKey").textContent=result.token;toast("Новый ключ создан. Скопируйте его и укажите в Windows Simulator.")});
async function testLlm(prompt){const result=await api("/api/control/test/llm","POST",{prompt,model:$("llmTestModel").value.trim()||null});state.lastLlm=result.text;return result}
function llmText(r){return "Первый фрагмент: "+(r.firstTokenMs??"—")+" мс\nПолное время: "+r.totalMs+" мс\nФрагментов SSE: "+r.chunks+"\nОценка: "+r.estimatedTokensPerSecond+" ток./с (приблизительно)\n\n"+r.text}
$("runLlmTest").onclick=run($("runLlmTest"),async()=>{$("llmTestResults").textContent="Запрос выполняется…";const r=await testLlm($("llmTestPrompt").value.trim());$("llmTestResults").textContent=llmText(r);toast("Тест LLM завершён")});
async function testTts(text,voice){const res=await fetch("/api/control/test/tts",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({text,voice,model:$("ttsTestModel").value.trim()||null})});if(!res.ok){const obj=await res.json().catch(()=>({}));throw Error(obj.error||"HTTP "+res.status)}const blob=await res.blob();if(testTts.url)URL.revokeObjectURL(testTts.url);testTts.url=URL.createObjectURL(blob);$("ttsAudio").src=testTts.url;$("ttsDownload").href=testTts.url;$("ttsDownload").style.display="inline";const synthesis=res.headers.get("X-Synthesis-Ms"),duration=res.headers.get("X-Audio-Seconds"),rtf=res.headers.get("X-RTF");$("ttsTestResults").textContent="Синтез: "+synthesis+" мс\nАудио: "+duration+" с\nReal-time factor: "+rtf+"\nРазмер WAV: "+fmtSize(blob.size);return{blob,synthesis,duration,rtf}}
$("runTtsTest").onclick=run($("runTtsTest"),async()=>{$("ttsTestResults").textContent="Генерация…";await testTts($("ttsTestText").value.trim(),$("ttsTestVoice").value.trim()||null);await $("ttsAudio").play().catch(()=>{});toast("Тестовая запись синтезирована")});
$("runCombined").onclick=run($("runCombined"),async()=>{$("combinedResults").textContent="LLM отвечает…";const llm=await testLlm($("llmTestPrompt").value.trim());$("combinedResults").textContent="LLM: "+llm.totalMs+" мс\nTTS синтезирует…";const text=llm.text.trim().slice(0,280);if(!text)throw Error("LLM вернула пустой ответ");const t=await testTts(text,$("ttsTestVoice").value.trim()||null);$("combinedResults").textContent="LLM: "+llm.totalMs+" мс\nTTS: "+t.synthesis+" мс\nДлительность голоса: "+t.duration+" с\nОжидание прослушивания";await $("ttsAudio").play().catch(()=>{});toast("Сквозной тест LLM → TTS завершён")});
function writeWav(pcm,rate=16000){const data=new ArrayBuffer(44+pcm.byteLength),view=new DataView(data);const chunk=(p,t)=>{for(let i=0;i<t.length;i++)view.setUint8(p+i,t.charCodeAt(i))};chunk(0,"RIFF");view.setUint32(4,36+pcm.byteLength,true);chunk(8,"WAVE");chunk(12,"fmt ");view.setUint32(16,16,true);view.setUint16(20,1,true);view.setUint16(22,1,true);view.setUint32(24,rate,true);view.setUint32(28,rate*2,true);view.setUint16(32,2,true);view.setUint16(34,16,true);chunk(36,"data");view.setUint32(40,pcm.byteLength,true);new Uint8Array(data,44).set(pcm);return data}
async function recordMic(){
 if(!navigator.mediaDevices?.getUserMedia)throw Error("Микрофон доступен только при HTTPS или на localhost");
 const stream=await navigator.mediaDevices.getUserMedia({audio:{channelCount:1,echoCancellation:false,noiseSuppression:false}});
 const ctx=new (window.AudioContext||window.webkitAudioContext)(),source=ctx.createMediaStreamSource(stream),processor=ctx.createScriptProcessor(4096,1,1);
 const chunks=[];let length=0;
 try{
  processor.onaudioprocess=e=>{const src=e.inputBuffer.getChannelData(0),data=new Float32Array(src);chunks.push(data);length+=data.length;e.outputBuffer.getChannelData(0).fill(0)};
  source.connect(processor);processor.connect(ctx.destination);
  $("micResults").textContent="Идёт запись… 5 секунд. Произнесите тестовую фразу.";
  await new Promise(resolve=>setTimeout(resolve,5000));
  processor.disconnect();source.disconnect();
  const captured=new Float32Array(length);let at=0;for(const c of chunks){captured.set(c,at);at+=c.length}
  const rate=16000,scale=ctx.sampleRate/rate,count=Math.floor(captured.length/scale);
  const pcm=new Uint8Array(count*2),view=new DataView(pcm.buffer);
  for(let i=0;i<count;i++){const position=i*scale,low=Math.floor(position),high=Math.min(captured.length-1,low+1),mix=position-low;const sample=(captured[low]||0)*(1-mix)+(captured[high]||0)*mix;view.setInt16(i*2,Math.round(Math.max(-1,Math.min(1,sample))*32767),true)}
  state.microphone=pcm;
  if(recordMic.url)URL.revokeObjectURL(recordMic.url);
  recordMic.url=URL.createObjectURL(new Blob([writeWav(pcm)],{type:"audio/wav"}));
  $("recordedAudio").src=recordMic.url;$("recordDownload").href=recordMic.url;$("recordDownload").style.display="inline";$("runSttTest").disabled=false;
  $("micResults").textContent="Записано: "+(count/rate).toFixed(2)+" с, "+fmtSize(pcm.length)+", PCM16 16 кГц. Можно прослушать или отправить в STT.";
 }finally{try{processor.disconnect();source.disconnect()}catch{}stream.getTracks().forEach(t=>t.stop());await ctx.close()}
}
$("recordMic").onclick=run($("recordMic"),recordMic);
$("runSttTest").onclick=run($("runSttTest"),async()=>{
 if(!state.microphone)throw Error("Сначала запишите микрофон");
 $("sttTestResults").textContent="Распознавание…";
 const response=await fetch("/api/control/test/stt?model="+encodeURIComponent($("sttTestModel").value.trim()),{method:"POST",headers:{"Content-Type":"application/octet-stream"},body:state.microphone});
 const result=await response.json().catch(()=>({}));
 if(!response.ok)throw Error(result.error||"HTTP "+response.status);
 $("sttTestResults").textContent="Распознано: "+result.text+"\nВремя: "+result.totalMs+" мс\nДлительность: "+result.audioSeconds+" с\nReal-time factor: "+result.realtimeFactor;toast("Тест STT завершён");
});
$("reload").onclick=run($("reload"),async()=>{await poll();toast("Данные обновлены")});
async function init(){
 try{
  const [settings,catalog,skills]=await Promise.all([api("/api/settings"),api("/api/control/catalog"),api("/api/control/skills")]);
  state.settings=settings;state.catalog=catalog;state.skills=skills;loadSettings();$("llmTestModel").value=settings.llmModel||"";$("ttsTestModel").value=settings.ttsModel||"";$("sttTestModel").value=settings.sttModel||"";renderCatalog();renderSkills();updateWs();
  const llama=await api("/api/control/llama/scan");if(llama.found?.length)$("llamaExe").value=llama.found[0];
  await poll();toast("Панель подключена к серверу");
 }catch(e){toast("Инициализация: "+e.message,true)}
 const page=location.hash.replace("#","");show(document.getElementById(page)?.classList.contains("screen")?page:"overview");
}
init();setInterval(poll,6000);
