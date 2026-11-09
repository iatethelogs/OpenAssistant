"use strict";
/* Unified LLM, STT, TTS and model storage editor. Existing endpoints remain authoritative. */
(() => {
 const roles=["llm","stt","tts"],suffix={llm:"Llm",stt:"Stt",tts:"Tts"};
 const $=id=>document.getElementById(id);
 let active="llm",filter="all",search="",dirty=false;
 let downloadingRole="llm";
 const drafts=new Map();
 function status(role){
   const r=state.models?.status?.running?.[role],apiState=state.models?.status?.api?.[role]?.state;
   const n=$("roleStatus"+suffix[role]);
   if(n)n.textContent=r?.running?"Процесс "+r.pid:apiState==="ready"?"API доступен":"Не запущена";
 }
 function capture(){
   if(!$("sharedRoleEditor")?.isConnected||!state.models?.profiles)return;
   if(dirty)drafts.set(active,readProfile());
 }
 function restore(role){
   const d=drafts.get(role);if(!d)return;
   $("profileExecutable").value=d.executable||"";$("profileModel").value=d.modelPath||"";
   $("profileCwd").value=d.workingDirectory||"";$("profileHealth").value=d.healthUrl||"";
   $("profileArgs").value=(d.arguments||[]).join("\n");
   for(const c of $("profileGpus").querySelectorAll("input[type=checkbox]"))
     c.checked=(d.gpuUuids||[]).includes(c.value);
 }
 function mountRole(role){
   if(!roles.includes(role)||!$("sharedRoleEditor"))return;
   if(active!==role)capture();
   active=role;$("profileRole").value=role;
   $("roleMount"+suffix[role]).append($("sharedRoleEditor"));
   loadProfile();restore(role);dirty=drafts.has(role);status(role);
 }
 function select(role){
   if(!roles.includes(role)&&role!=="storage")return;
   const tab=window.OpenAssistantUI?.selectTab;
   tab?.(role);
   if(roles.includes(role))mountRole(role);
 }
 function fileEntry(item){
   const row=document.createElement("div");row.className="installed-row";
   const name=document.createElement("div");name.className="model-file-label";
   const strong=document.createElement("strong");strong.textContent=item.name||"Модель";
   const path=document.createElement("small");path.textContent=item.path||"";path.title=item.path||"";
   const info=document.createElement("small");
   const sz=item.bytes==null?"":(item.bytes/1073741824).toFixed(2)+" ГиБ · ";
   info.textContent=sz+(item.source==="huggingface-cache"||item.source==="gigaam-cache"?"Кеш модели":"Файлы сервера");
   name.append(strong,path,info);row.append(name);
   const actions=document.createElement("div");actions.className="installed-actions";
   if(roles.includes(item.role)&&item.path&&item.source!=="piper-cache"){
     const selectButton=document.createElement("button");selectButton.type="button";selectButton.className="button secondary";selectButton.textContent="Выбрать";
     selectButton.onclick=()=>{
       select(item.role);mountRole(item.role);
       $("profileModel").value=item.path;
       dirty=true;drafts.set(item.role,readProfile());
       $("profileModel").focus();
       $("profileModel").setAttribute("aria-label","Выбранная модель, сохраните профиль запуска");
     };
     actions.append(selectButton);
   }
   if(item.removable){
     const del=document.createElement("button");del.type="button";del.className="button danger";del.textContent="Удалить";
     del.onclick=busy(del,async()=>{
       if(!confirm("Удалить файл «"+item.name+"»?"))return;
       await api("/api/models/weights/delete","POST",{path:item.path});
       await refreshModels();
     });
     actions.append(del);
   }
   row.append(actions);return row;
 }
 function renderIndex(){
   const list=state.downloads?.installed||[];
   const filtered=list.filter(item=>(filter==="all"||item.role===filter)&&
     (!search||[item.name,item.repo,item.path].some(value=>String(value||"").toLowerCase().includes(search))));
   for(const role of roles){
     status(role);
     const box=$("roleInstalled"+suffix[role]);if(!box)continue;
     box.replaceChildren();
     const matches=list.filter(x=>x.role===role && (!search||[x.name,x.repo,x.path].some(value=>String(value||"").toLowerCase().includes(search))));
     matches.slice(0,150).forEach(item=>box.append(fileEntry(item)));
     if(!matches.length)box.textContent=search?"Нет совпадений":"Нет установленных моделей";
   }
   if($("storageRoot"))$("storageRoot").textContent=state.downloads?.root||"—";
   const inventory=$("installedModels");
   if(inventory){
     const rows=[...inventory.querySelectorAll(".installed-row")];
     rows.forEach((row,i)=>{
       const item=list[i];
       row.hidden=!item||!filtered.includes(item);
     });
   }
   const summary=$("storageSummary");
   if(summary){
     const bytes=list.reduce((total,item)=>total+(typeof item.bytes==="number"?item.bytes:0),0);
     const known=bytes>0?" · "+(bytes/1073741824).toFixed(2)+" ГиБ учтено":"";
     summary.textContent=roles.map(r=>r.toUpperCase()+": "+list.filter(x=>x.role===r).length).join("   ·   ")+known;
   }
   const found=$("storageFound");if(found)found.textContent=filtered.length+" из "+list.length;
 }
 function mount(){
   const old=$("models"),host=$("modelStorageMount"),inventory=$("storageInventoryMount");
   if(!old||!host)return;
   const cards=$("modelCards"),downloads=old.querySelector(".overview-grid"),editor=old.querySelector(".model-editor");
   if(cards)host.append(cards);
   if(downloads)host.append(downloads);
   // Keep one installed-list renderer and move it beneath the type filter.
   const installed=$("installedModels");
   if(installed&&inventory){
     const parent=installed.parentElement;
     const heading=installed.previousElementSibling;
     if(heading?.classList.contains("panel-heading"))heading.remove();
     if(installed.previousElementSibling?.classList.contains("rule"))installed.previousElementSibling.remove();
     inventory.append(installed);
   }
   if(editor){
     editor.id="sharedRoleEditor";
     editor.classList.add("model-profile-inline");
     const heading=editor.querySelector(".panel-heading");
     if(heading){
       heading.querySelector("h2").textContent="Запуск движка";
       const sub=heading.querySelector("p");
       if(sub)sub.textContent="Путь модели, GPU и управление процессом";
     }
     $("roleMountLlm").append(editor);
     const mainFields=editor.querySelector(".field-grid");
     if(mainFields){
       const advanced=document.createElement("details");advanced.className="profile-advanced";
       const summary=document.createElement("summary");summary.textContent="Дополнительные параметры запуска";
       advanced.append(summary);
       const advancedFields=document.createElement("div");advancedFields.className="field-grid";
       for(const id of ["profileExecutable","profileCwd","profileHealth"]){
         const input=$(id);if(input)advancedFields.append(input.closest("div"));
       }
       advanced.append(advancedFields);
       const argLabel=editor.querySelector('label[for="profileArgs"]'),args=$("profileArgs");
       if(argLabel&&args)advanced.append(argLabel,args);
       const buttonRow=editor.querySelector(".button-row");
       editor.insertBefore(advanced,buttonRow||null);
     }
     const logs=$("profileLogs");
     if(logs){
       const disclosure=document.createElement("details");
       disclosure.className="profile-logs";
       const title=document.createElement("summary");
       title.textContent="Журнал процесса";
       disclosure.append(title);
       logs.before(disclosure);
       disclosure.append(logs);
     }
     const roleSelect=$("profileRole");
     roleSelect.classList.add("hidden");
     const label=document.querySelector('label[for="profileRole"]');label?.classList.add("hidden");
   }
   // Model sampling is authored directly in the LLM section. Voice and
   // language are authored directly in the TTS section; do not move controls
   // into hidden Qwen panels when Piper is selected.
   const llm=$("llmSamplingFields");
   const extra=$("llmExtraFields");
   if(extra&&llm){while(extra.firstElementChild)llm.append(extra.firstElementChild);extra.remove()}
   // Keep existing model operations and API calls, only relocate their UI.
   old.remove();
   const toolsRow=document.createElement("div");toolsRow.className="library-tools";
   const query=document.createElement("input");query.type="search";query.placeholder="Поиск по имени, пути или репозиторию";
   query.setAttribute("aria-label","Поиск моделей");query.autocomplete="off";
   query.oninput=()=>{search=query.value.trim().toLowerCase();renderIndex()};
   const found=document.createElement("span");found.id="storageFound";found.className="minor";
   const refresh=document.createElement("button");refresh.className="button secondary";refresh.type="button";refresh.textContent="Обновить";
   refresh.onclick=busy(refresh,async()=>{await refreshModels()});
   toolsRow.append(query,found,refresh);
   const storageHeading=$("storageSummary");storageHeading?.before(toolsRow);
   const repoField=$("hfRepo");
   if(repoField){
     const roleWrapper=document.createElement("div");roleWrapper.className="repository-role";
     const label=document.createElement("label");label.htmlFor="downloadRole";label.textContent="Тип загружаемой модели";
     const select=document.createElement("select");select.id="downloadRole";
     for(const [value,title] of [["llm","Языковая модель · LLM"],["stt","Распознавание речи · STT"],["tts","Синтез речи · TTS"]]){
       const opt=document.createElement("option");opt.value=value;opt.textContent=title;select.append(opt);
     }
     select.onchange=()=>{downloadingRole=select.value};
     roleWrapper.append(label,select);
     repoField.parentElement?.insertBefore(roleWrapper,repoField);
   }
   document.querySelectorAll("[data-storage-role]").forEach(button=>button.addEventListener("click",()=>{
     filter=button.dataset.storageRole;
     document.querySelectorAll("[data-storage-role]").forEach(x=>x.classList.toggle("selected",x===button));
     renderIndex();
   }));
   const editorRoot=$("sharedRoleEditor");
   editorRoot?.addEventListener("input",()=>{dirty=true;drafts.set(active,readProfile())});
   editorRoot?.addEventListener("change",()=>{dirty=true;drafts.set(active,readProfile())});
   for(const role of roles)
     document.querySelector('[data-settings-tab="'+role+'"]')?.addEventListener("click",()=>mountRole(role));
   if(!$("roleStatusTts")){
     const statusText=document.createElement("span");statusText.id="roleStatusTts";statusText.className="minor";
     $("roleMountTts")?.before(statusText);
   }
   mountRole("llm");
   renderIndex();
   if(location.hash==="#models")select("storage");
 }
 function refreshStatuses(){for(const role of roles)status(role)}
 function saved(role){drafts.delete(role);if(active===role)dirty=false}
 function hasUnsavedChanges(){return dirty}
 function downloadRole(){return downloadingRole}
 window.OpenAssistantModelUI={mountRole,select,renderIndex,refreshStatuses,saved,hasUnsavedChanges,downloadRole};
 document.addEventListener("DOMContentLoaded",mount);
})();
