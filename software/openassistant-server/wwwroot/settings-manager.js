"use strict";
/* Unified LLM, STT, TTS and model storage editor. Existing endpoints remain authoritative. */
(() => {
 const roles=["llm","stt","tts"],suffix={llm:"Llm",stt:"Stt",tts:"Tts"};
 const $=id=>document.getElementById(id);
 let active="llm",filter="all";
 const drafts=new Map();
 function status(role){
   const r=state.models?.status?.running?.[role],apiState=state.models?.status?.api?.[role]?.state;
   const n=$("roleStatus"+suffix[role]);
   if(n)n.textContent=r?.running?"Процесс "+r.pid:apiState==="ready"?"API доступен":"Не запущена";
 }
 function capture(){
   if(!$("sharedRoleEditor")?.isConnected||!state.models?.profiles)return;
   drafts.set(active,readProfile());
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
   loadProfile();restore(role);status(role);
 }
 function select(role){window.OpenAssistantUI?.selectTab?.(role);}
 function fileEntry(item){
   const row=document.createElement("div");row.className="installed-row";
   const name=document.createElement("div");name.className="model-file-label";
   const strong=document.createElement("strong");strong.textContent=item.name||"Модель";
   const path=document.createElement("small");path.textContent=item.path||"";path.title=item.path||"";
   name.append(strong,path);row.append(name);
   const actions=document.createElement("div");actions.className="installed-actions";
   if(roles.includes(item.role)&&item.path){
     const selectButton=document.createElement("button");selectButton.type="button";selectButton.className="button secondary";selectButton.textContent="Выбрать";
     selectButton.onclick=()=>{
       select(item.role);mountRole(item.role);
       $("profileModel").value=item.path;
       drafts.set(item.role,readProfile());
       $("profileModel").focus();
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
   for(const role of roles){
     status(role);
     const box=$("roleInstalled"+suffix[role]);if(!box)continue;
     box.replaceChildren();
     const matches=list.filter(x=>x.role===role);
     matches.slice(0,150).forEach(item=>box.append(fileEntry(item)));
     if(!matches.length)box.textContent="Модели не найдены";
   }
   if($("storageRoot"))$("storageRoot").textContent=state.downloads?.root||"—";
   const inventory=$("installedModels");
   if(inventory){
     const rows=[...inventory.querySelectorAll(".installed-row")];
     rows.forEach((row,i)=>{
       const item=list[i];
       row.hidden=!!item&&filter!=="all"&&item.role!==filter;
     });
   }
   const summary=$("storageSummary");
   if(summary)summary.textContent=roles.map(r=>r.toUpperCase()+": "+list.filter(x=>x.role===r).length).join("   ·   ");
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
     $("roleMountLlm").append(editor);
     const roleSelect=$("profileRole");
     roleSelect.classList.add("hidden");
     const label=document.querySelector('label[for="profileRole"]');label?.classList.add("hidden");
   }
   const llm=$("llmSamplingFields");
   for(const id of ["temperature","maxTokens"]){const input=$(id);if(input&&llm)llm.append(input.parentElement)}
   const extra=$("llmExtraFields");
   if(extra&&llm){while(extra.firstElementChild)llm.append(extra.firstElementChild);extra.remove()}
   const tts=$("settings-tts")?.querySelector(".field-grid");
   for(const id of ["language","voice"]){const input=$(id);if(input&&tts)tts.prepend(input.parentElement)}
   // The current settings form wraps all model tabs so user preferences are saved in one place.
   old.remove();
   document.querySelectorAll("[data-storage-role]").forEach(button=>button.addEventListener("click",()=>{
     filter=button.dataset.storageRole;
     document.querySelectorAll("[data-storage-role]").forEach(x=>x.classList.toggle("selected",x===button));
     renderIndex();
   }));
   $("profileSave")?.addEventListener("click",()=>drafts.delete(active));
   for(const role of roles)
     document.querySelector('[data-settings-tab="'+role+'"]')?.addEventListener("click",()=>mountRole(role));
   mountRole("llm");
   renderIndex();
   if(location.hash==="#models")select("storage");
 }
 function refreshStatuses(){for(const role of roles)status(role)}
 window.OpenAssistantModelUI={mountRole,select,renderIndex,refreshStatuses};
 document.addEventListener("DOMContentLoaded",mount);
})();
