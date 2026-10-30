"use strict";
const el=id=>document.getElementById(id);
const state={catalog:[],selected:{},jobs:{},step:"llm",pcm:null,voiceUrl:null,recordUrl:null,key:"",starting:{},shown:{},last:{}};
const captions={llm:"Интеллект",stt:"Слух",tts:"Голос"};
const steps=["llm","stt","tts","device","complete"];
function showError(message){alert(message)}
async function api(url,method="GET",data){const init={method,cache:"no-store"};if(data!==undefined){init.headers={"Content-Type":"application/json"};init.body=JSON.stringify(data)}
 const r=await fetch(url,init);const body=await r.json().catch(()=>({}));if(!r.ok)throw Error(body.error||body.message||"HTTP "+r.status);return body}
function show(step){if(!steps.includes(step))return;state.step=step;for(const sec of document.querySelectorAll(".section"))sec.classList.toggle("active",sec.id===step);
 for(const elem of document.querySelectorAll(".step")){const ix=steps.indexOf(elem.dataset.step),now=steps.indexOf(step);elem.classList.toggle("current",ix===now);elem.classList.toggle("done",ix<now)}
 history.replaceState(null,"","#"+step);window.scrollTo({top:0,behavior:"smooth"})}
for(const btn of document.querySelectorAll("[data-next]"))btn.onclick=()=>show(btn.dataset.next);
function modelCard(m){const b=document.createElement("button");b.type="button";b.className="model";b.dataset.model=m.id;
 const type=document.createElement("span");type.className="type";type.textContent=m.role==="llm"?"Языковая модель":m.role==="stt"?"Распознавание речи":"Синтез речи";
 const name=document.createElement("strong");name.textContent=m.name;const desc=document.createElement("small");desc.textContent=m.summary;
 const foot=document.createElement("span");foot.className="foot";foot.textContent=m.diskGb.toLocaleString("ru-RU")+" ГБ · "+m.quality;
 b.append(type,name,desc,foot);
 if(m.recommended){const label=document.createElement("span");label.className="tag";label.textContent="Рекомендуем";b.append(label)}
 b.onclick=()=>{state.selected[m.role]=m.id;renderCards(m.role);start(m.role).catch(e=>showError(e.message))};return b}
function renderCards(role){const box=el(role+"Models");box.replaceChildren();for(const m of state.catalog.filter(x=>x.role===role)){const b=modelCard(m);if(state.selected[role]===m.id)b.classList.add("selected");box.append(b)}}
function updateButtons(){for(const role of ["llm","stt","tts"]){const b=el("start"+role.toUpperCase().slice(0,1)+role.slice(1));if(!b)continue;const status=state.jobs[role]?.status;b.disabled=status==="running";b.textContent=status==="ready"?"Установить другую модель ↻":status==="running"?"Устанавливаем…":"Установить и проверить →"}}
async function start(role){if(state.starting[role]||state.jobs[role]?.status==="running")return;const modelId=state.selected[role];if(!modelId)throw Error("Сначала выберите модель");
 state.starting[role]=true;
 try{const result=await api("/api/setup/"+role,"POST",{modelId});state.jobs[role]=result;state.shown[role]=false;el("result"+role.charAt(0).toUpperCase()+role.slice(1)).classList.remove("visible");updateRole(role,result);updateButtons()}
 catch(e){showError("Не удалось начать установку: "+e.message)}finally{state.starting[role]=false}}
for(const role of ["llm","stt","tts"]){const button=el("start"+role.charAt(0).toUpperCase()+role.slice(1));button.onclick=()=>start(role)}
const titles={download:"Скачиваем модель",install:"Устанавливаем компоненты",runtime:"Подготавливаем движок",launch:"Запускаем нейросеть",loading:"Загружаем веса в память",test:"Проверяем первый ответ",ready:"Готово к работе",failed:"Установка остановлена",cancelled:"Установка отменена",starting:"Готовимся к работе"};
const stageNums={starting:2,download:12,install:25,runtime:45,launch:60,loading:80,test:95,ready:100};
function fmt(n){if(!n)return"0 МБ";if(n>1073741824)return(n/1073741824).toFixed(2)+" ГБ";return(n/1048576).toFixed(0)+" МБ"}
function updateRole(role,job){
 const cap=role[0].toUpperCase()+role.slice(1);
 el("progress"+cap).classList.add("visible");
 el("title"+cap).textContent=titles[job.step]||"Настраиваем";
 el("desc"+cap).textContent=job.error||job.description||"Подготовка";
 const progress=job.step==="download"&&job.total>0?12+Math.round(35*job.bytes/job.total):stageNums[job.step]??30;
 el("bar"+cap).style.width=Math.min(progress,100)+"%";
 el("bytes"+cap).textContent=job.total?fmt(job.bytes)+" / "+fmt(job.total):"Подготовка компонентов";
 el("stage"+cap).textContent=job.step==="ready"?"100%":job.step;
 el("logs"+cap).textContent=(job.logs||[]).slice(-90).join("\n");
 const ring=el("ring"+cap);ring.classList.toggle("ready",job.status==="ready");
 el("progress"+cap).classList.toggle("error",job.status==="failed");
 if(job.status==="failed"){el("desc"+cap).textContent=job.error||"Проверьте технические подробности";updateButtons()}
 if(job.status==="ready"&&!state.shown[role]){state.shown[role]=true;el("result"+cap).classList.add("visible");if(role==="llm")llmResult(job);if(role==="stt")el("sttRecognized").textContent="Модель готова. Запишите тестовую фразу.";if(role==="tts")el("ttsMetrics").textContent="Первичная проверка завершена. Выберите голос и прослушайте результат.";}
 if(job.status==="ready")el("result"+cap).classList.add("visible");
}
function llmResult(job){let data={};try{data=JSON.parse(job.testResult||"{}")}catch{}
 el("answerLlm").textContent=data.text||"Модель успешно ответила на тестовый запрос";
 const metrics=el("metricsLlm");metrics.replaceChildren();
 for(const [label,value] of [["Первый фрагмент",data.firstTokenMs!=null?data.firstTokenMs+" мс":"—"],["Весь ответ",data.totalMs!=null?data.totalMs+" мс":"—"],[data.tokensExact?"Скорость":"Оценка скорости",data.tokensPerSecond!=null?data.tokensPerSecond+" ток/с":"—"]]){const x=document.createElement("span");x.className="pill";x.textContent=label+" · "+value;metrics.append(x)}
}
async function poll(){try{
 const [result,dev]=await Promise.all([api("/api/setup/status"),api("/api/control/devices")]);
 el("dot").classList.add("on");el("serverState").textContent="Сервер на связи";
 for(const job of result.jobs||[]){state.jobs[job.role]=job;updateRole(job.role,job)}
 updateButtons();el("deviceStatus").textContent=dev.count?"Подключено устройств: "+dev.count:"Ожидаем подключение колонки…";
 }catch(e){el("dot").classList.remove("on");el("serverState").textContent="Связь с сервером потеряна"}}
el("testLlm").onclick=async()=>{
 const b=el("testLlm");b.disabled=true;el("manualLlm").classList.remove("hidden");el("manualLlm").textContent="Проверяем ответ…";
 try{const r=await api("/api/control/test/llm","POST",{prompt:el("testPrompt").value,model:"local"});el("manualLlm").textContent=r.text+"\n\nПервый фрагмент: "+r.firstTokenMs+" мс · Время: "+r.totalMs+" мс · Оценка скорости: "+r.estimatedTokensPerSecond+" ток/с"}
 catch(e){el("manualLlm").textContent=e.message}finally{b.disabled=false}
};
function wav(data,rate=16000){const output=new ArrayBuffer(44+data.byteLength),d=new DataView(output);
 const str=(offset,text)=>{for(let i=0;i<text.length;i++)d.setUint8(offset+i,text.charCodeAt(i))};
 str(0,"RIFF");d.setUint32(4,36+data.byteLength,true);str(8,"WAVE");str(12,"fmt ");d.setUint32(16,16,true);
 d.setUint16(20,1,true);d.setUint16(22,1,true);d.setUint32(24,rate,true);d.setUint32(28,rate*2,true);
 d.setUint16(32,2,true);d.setUint16(34,16,true);str(36,"data");d.setUint32(40,data.byteLength,true);new Uint8Array(output,44).set(data);return output}
async function record(){
 if(!navigator.mediaDevices?.getUserMedia)throw Error("Микрофон доступен по HTTPS или через http://localhost. Используйте SSH-туннель.");
 const stream=await navigator.mediaDevices.getUserMedia({audio:{channelCount:1}}),ctx=new (window.AudioContext||window.webkitAudioContext)();
 const src=ctx.createMediaStreamSource(stream),processor=ctx.createScriptProcessor(4096,1,1);
 let n=0;const segments=[];
 processor.onaudioprocess=e=>{const a=e.inputBuffer.getChannelData(0),part=new Float32Array(a);segments.push(part);n+=part.length;e.outputBuffer.getChannelData(0).fill(0)};
 src.connect(processor);processor.connect(ctx.destination);el("sttRecognized").textContent="Идёт запись… говорите.";
 try{await new Promise(resolve=>setTimeout(resolve,5000))}finally{processor.disconnect();src.disconnect();stream.getTracks().forEach(t=>t.stop())}
 const all=new Float32Array(n);let pos=0;for(const part of segments){all.set(part,pos);pos+=part.length}
 const ratio=ctx.sampleRate/16000,count=Math.floor(n/ratio),bytes=new Uint8Array(count*2),view=new DataView(bytes.buffer);
 for(let i=0;i<count;i++){const at=i*ratio,a=Math.floor(at),b=Math.min(n-1,a+1),w=at-a,value=(all[a]||0)*(1-w)+(all[b]||0)*w;view.setInt16(i*2,Math.max(-32768,Math.min(32767,Math.round(value*32767))),true)}
 await ctx.close();state.pcm=bytes;
 if(state.recordUrl)URL.revokeObjectURL(state.recordUrl);state.recordUrl=URL.createObjectURL(new Blob([wav(bytes)],{type:"audio/wav"}));
 el("recordPlayer").src=state.recordUrl;el("transcribeStt").disabled=false;el("sttRecognized").textContent="Запись готова. Нажмите «Распознать».";
}
el("recordStt").onclick=async()=>{const b=el("recordStt");b.disabled=true;try{await record()}catch(e){showError(e.message)}finally{b.disabled=false}};
el("transcribeStt").onclick=async()=>{const b=el("transcribeStt");b.disabled=true;el("sttRecognized").textContent="Распознаём вашу речь…";
 try{const res=await fetch("/api/control/test/stt",{method:"POST",headers:{"Content-Type":"application/octet-stream"},body:state.pcm});const data=await res.json();if(!res.ok)throw Error(data.error||"Ошибка STT");el("sttRecognized").textContent="«"+data.text+"» · "+data.totalMs+" мс · RTF "+data.realtimeFactor}
 catch(e){el("sttRecognized").textContent=e.message}finally{b.disabled=false}};
el("testTts").onclick=async()=>{const b=el("testTts");b.disabled=true;el("ttsMetrics").textContent="Синтезируем выбранным голосом…";
 try{const res=await fetch("/api/control/test/tts",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({text:el("ttsPhrase").value,voice:el("voiceSelect").value,model:null})});
 if(!res.ok){const d=await res.json().catch(()=>({}));throw Error(d.error||"Ошибка TTS")}
 const blob=await res.blob();if(state.voiceUrl)URL.revokeObjectURL(state.voiceUrl);state.voiceUrl=URL.createObjectURL(blob);
 el("ttsPlayer").src=state.voiceUrl;el("downloadWav").href=state.voiceUrl;el("downloadWav").classList.remove("hidden");
 el("ttsMetrics").textContent="Синтез: "+res.headers.get("X-Synthesis-Ms")+" мс · Длительность: "+res.headers.get("X-Audio-Seconds")+" с · RTF "+res.headers.get("X-RTF");
 await el("ttsPlayer").play().catch(()=>{});
 }catch(e){el("ttsMetrics").textContent=e.message}finally{b.disabled=false}};
el("hostInput").oninput=()=>{let host=el("hostInput").value.trim().replace(/^wss?:\/\//,"").replace(/\/.*$/,"");el("wsAddress").textContent="ws://"+(host||"SERVER_IP")+":5055/ws"};
async function copy(text){if(!navigator.clipboard?.writeText)throw Error("Копирование требует HTTPS или localhost");await navigator.clipboard.writeText(text)}
el("copyWs").onclick=async()=>{try{await copy(el("wsAddress").textContent)}catch(e){showError(e.message)}};
el("newDeviceKey").onclick=async()=>{if(!confirm("Создать новый ключ устройства? Ранее выданные ключи будут недействительны."))return;
 try{const result=await api("/api/control/device/key","POST");state.key=result.token;el("deviceKey").textContent=result.token}catch(e){showError(e.message)}};
el("copyDeviceKey").onclick=async()=>{try{if(!state.key)throw Error("Сначала создайте ключ");await copy(state.key)}catch(e){showError(e.message)}};
async function init(){try{
 state.catalog=await api("/api/setup/catalog");
 for(const role of ["llm","stt","tts"]){state.selected[role]=state.catalog.find(m=>m.role===role&&m.default)?.id;renderCards(role)}
 await poll();
 const gpu=await api("/api/models").catch(()=>null);const cards=gpu?.status?.gpus||[];
 const best=cards.slice().sort((a,b)=>(b.totalMiB-b.usedMiB)-(a.totalMiB-a.usedMiB))[0];
 if(best)el("gpuInfo").textContent="Автовыбор GPU · "+best.name+" · "+best.totalMiB+" МиБ VRAM";
 }catch(e){el("serverState").textContent="Ошибка подключения";showError(e.message)}
 const hash=location.hash.slice(1);if(steps.includes(hash))show(hash);
}
init();setInterval(poll,2000);
