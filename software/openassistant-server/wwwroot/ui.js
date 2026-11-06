/* Small client-side UI primitives. No frameworks, dependencies or background animation. */
(() => {
  "use strict";
  const key = "openassistant-theme";
  const paths = {
    "grid":'<rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/>',
    "sliders":'<path d="M4 7h9M17 7h3M4 17h3M11 17h9"/><circle cx="15" cy="7" r="2"/><circle cx="9" cy="17" r="2"/>',
    "layers":'<path d="m12 3 9 5-9 5-9-5 9-5ZM3 12l9 5 9-5M3 16l9 5 9-5"/>',
    "mic":'<rect x="9" y="2" width="6" height="13" rx="3"/><path d="M5 10a7 7 0 0 0 14 0M12 17v5m-4 0h8"/>',
    "speaker":'<rect x="5" y="3" width="14" height="18" rx="2"/><circle cx="12" cy="14" r="3"/><path d="M12 7h.01"/>',
    "plug":'<path d="M9 2v6m6-6v6M7 8h10v3a5 5 0 0 1-10 0V8Zm5 8v6"/>',
    "message":'<path d="M21 11.5a8.5 8.5 0 0 1-8.5 8.5 9 9 0 0 1-4-.9L3 21l1.9-5.5a9 9 0 1 1 16.1-4Z"/>',
    "music":'<path d="M9 18V5l12-2v13M9 8l12-2"/><ellipse cx="6" cy="18" rx="3" ry="2"/><ellipse cx="18" cy="16" rx="3" ry="2"/>',
    "clock":'<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    "terminal":'<rect x="3" y="4" width="18" height="16" rx="2"/><path d="m7 9 3 3-3 3m6 0h4"/>',
    "settings":'<path d="M4 7h16M4 12h16M4 17h16"/><circle cx="9" cy="7" r="2" fill="var(--surface)" stroke="currentColor"/><circle cx="16" cy="12" r="2" fill="var(--surface)" stroke="currentColor"/><circle cx="11" cy="17" r="2" fill="var(--surface)" stroke="currentColor"/>',
    "sun":'<circle cx="12" cy="12" r="4"/><path d="M12 2v2m0 16v2M4.9 4.9l1.4 1.4m11.4 11.4 1.4 1.4M2 12h2m16 0h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
    "moon":'<path d="M20.6 13.2A9 9 0 0 1 10.8 3.4 9 9 0 1 0 20.6 13.2Z"/>',
    "arrow-left":'<path d="m12 19-7-7 7-7M5 12h14"/>',
    "arrow-right":'<path d="M5 12h14m-7-7 7 7-7 7"/>',
    "chevron-right":'<path d="m9 18 6-6-6-6"/>',
    "chevron-down":'<path d="m6 9 6 6 6-6"/>',
    "refresh":'<path d="M20 7v5h-5M4 17v-5h5"/><path d="M5.6 9a7 7 0 0 1 12.3-2L20 12M4 12l2.1 5a7 7 0 0 0 12.3-2"/>',
    "check":'<path d="m5 12 4 4L19 6"/>',
    "headphones":'<path d="M3 14v-3a9 9 0 0 1 18 0v3M3 14v4a2 2 0 0 0 2 2h3v-8H5a2 2 0 0 0-2 2Zm18 0v4a2 2 0 0 1-2 2h-3v-8h3a2 2 0 0 1 2 2Z"/>',
    "hard-drive":'<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M3 13h18M7 16h.01M11 16h.01"/>',
    "cpu":'<rect x="6" y="6" width="12" height="12" rx="2"/><path d="M9 1v5m6-5v5M9 18v5m6-5v5M1 9h5m-5 6h5m12-6h5m-5 6h5"/><rect x="9" y="9" width="6" height="6"/>',
    "close":'<path d="M18 6 6 18M6 6l12 12"/>',
    "search":'<circle cx="11" cy="11" r="7"/><path d="m16.5 16.5 4 4"/>'
  };
  function icon(name){
    const svg = document.createElementNS("http://www.w3.org/2000/svg","svg");
    svg.setAttribute("viewBox","0 0 24 24");
    svg.setAttribute("aria-hidden","true");
    svg.classList.add("ui-icon");
    svg.innerHTML=paths[name]||paths["grid"];
    return svg;
  }
  function fillIcons(){
    document.querySelectorAll("[data-icon]").forEach(node=>{
      const name=node.getAttribute("data-icon");
      node.replaceChildren(icon(name));
    });
  }
  function savedTheme(){
    try{ return localStorage.getItem(key) }catch{ return null }
  }
  function resolveTheme(){
    const saved=savedTheme();
    return saved === "dark"||saved === "light" ? saved :
      (window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark");
  }
  function setTheme(theme,save){
    document.documentElement.dataset.theme=theme;
    document.querySelectorAll("[data-theme-toggle]").forEach(btn=>{
      const target=theme==="dark"?"light":"dark";
      btn.setAttribute("title",target==="light"?"Светлая тема":"Тёмная тема");
      btn.setAttribute("aria-label",target==="light"?"Светлая тема":"Тёмная тема");
      btn.replaceChildren(icon(target==="light"?"sun":"moon"));
    });
    if(save)try{localStorage.setItem(key,theme)}catch{}
  }
  setTheme(resolveTheme(),false);
  document.addEventListener("DOMContentLoaded",()=>{
    fillIcons();setTheme(resolveTheme(),false);
    document.querySelectorAll("[data-theme-toggle]").forEach(btn=>{
      btn.addEventListener("click",()=>setTheme(
        document.documentElement.dataset.theme==="dark"?"light":"dark",true));
    });
    const tabs=[...document.querySelectorAll("[data-settings-tab]")];
    function setTab(value,focus=false){
      if(!tabs.some(tab=>tab.dataset.settingsTab===value))return;
      tabs.forEach(tab=>{
        const chosen=tab.dataset.settingsTab===value;
        tab.classList.toggle("selected",chosen);
        tab.setAttribute("aria-selected",String(chosen));
        tab.tabIndex=chosen?0:-1;
        if(chosen&&focus)tab.focus();
      });
      document.querySelectorAll("[data-settings-panel]").forEach(panel=>{
        panel.hidden=panel.dataset.settingsPanel!==value;
      });
      document.querySelector("#settingsForm>.form-footer")?.toggleAttribute("hidden",value==="storage");
      window.dispatchEvent(new CustomEvent("openassistant:settings-tab",{detail:{value}}));
    }
    if(tabs.length){
      tabs.forEach((tab,i)=>{
        tab.addEventListener("click",()=>setTab(tab.dataset.settingsTab));
        tab.addEventListener("keydown",e=>{
          if(e.key!=="ArrowRight"&&e.key!=="ArrowLeft")return;
          e.preventDefault();
          let next=(i+(e.key==="ArrowRight"?1:-1)+tabs.length)%tabs.length;
          setTab(tabs[next].dataset.settingsTab,true);
        });
      });
      window.OpenAssistantUI={selectTab:setTab};
      setTab(location.hash==="#models"?"storage":tabs[0].dataset.settingsTab);
    }
  });
})();
