/* Shared matrix configuration editor: server settings and initial setup.
   One configuration model and one rendering grammar with the C# matrix renderer. */
(() => {
 "use strict";
 const names={offline:"Отключено",idle:"Ожидание",listening:"Слушает",thinking:"Обрабатывает",speaking:"Отвечает",music:"Музыка",error:"Ошибка"};
 const effects={off:"Выключено",solid:"Заливка",breath:"Дыхание",wave:"Волна",bars:"Эквалайзер",orbit:"Бегущий импульс",pulse:"Пульс",sparkles:"Искры",eyes:"Глаза",scan:"Сканирование"};
 const defaultEffects={
  offline:{preset:"off",color:"#60646A",speed:1},idle:{preset:"breath",color:"#789BC1",speed:0.6},
  listening:{preset:"bars",color:"#69ACE5",speed:1.2},thinking:{preset:"orbit",color:"#99AACE",speed:1},
  speaking:{preset:"wave",color:"#B0BFE1",speed:1},music:{preset:"bars",color:"#82A8DD",speed:1.5},
  error:{preset:"pulse",color:"#C78282",speed:0.7}
 };
 const defaults=()=>({width:32,height:10,fps:25,brightness:65,serpentine:false,
   states:JSON.parse(JSON.stringify(defaultEffects))});
 function clamp(x,min,max){return Math.max(min,Math.min(max,x))}
 function imageData(config,mode,t,level=0.42){
  const w=config.width,h=config.height,brightness=config.brightness/100;
  const effect=config.states[mode]||defaultEffects.idle;
  const hex=effect.color||"#789BC1",base=[1,3,5].map(i=>parseInt(hex.slice(i,i+2),16)||0);
  const frame=new Uint8ClampedArray(w*h*4),speed=effect.speed||1,phase=t*speed;
  for(let y=0;y<h;y++)for(let x=0;x<w;x++){
    const xx=(x+.5)/w,yy=(y+.5)/h,nx=(x-(w-1)/2)/Math.max(w/2,1),ny=(y-(h-1)/2)/Math.max(h/2,1);
    let power=0;
    switch(effect.preset){
     case "solid":power=1;break;
     case "breath":power=.10+.33*(1+Math.sin(phase*2*Math.PI*.35));break;
     case "pulse":power=.15+.8*Math.pow((1+Math.sin(phase*2*Math.PI*.7))/2,5);break;
     case "wave":power=Math.max(0,1-Math.abs(ny-.65*Math.sin(xx*12-phase*3))/.32)*(.38+.62*level);break;
     case "bars":{
       const bar=.12+.88*clamp(level*.75+.4*Math.abs(Math.sin(x*.49+phase*3)*Math.cos(x*.16-phase*1.3)),0,1);
       power=yy>=1-bar? .35+.65*(1-(yy-(1-bar))/bar) : 0;break;}
     case "orbit":{const d=Math.abs(x-((phase*5)% (w+7)-3));power=Math.max(0,1-d/4);break;}
     case "sparkles":{let n=(x*733+y*1399+Math.floor(phase*8)*451)%997;n=((n*151+173)%997)/997;power=n>.97?1:n>.94?.35:0;break;}
     case "eyes":{
       const centerX=x<w/2?w*.32:w*.68;
       const radius=Math.sqrt(Math.pow((x-centerX)/(w*.12),2)+Math.pow((y-h*.5)/(h*.3),2));
       power=radius<=1?(.25+.6*Math.pow(Math.max(0,1-radius),.8)):0;break;}
     case "scan":{const cursor=(phase*9)%Math.max(w,1);power=Math.max(0,1-Math.abs(x-cursor)/4);break;}
     default:power=0;
    }
    const i=(y*w+x)*4;
    for(let c=0;c<3;c++)frame[i+c]=Math.round(base[c]*brightness*clamp(power,0,1));
    frame[i+3]=255;
  }
  return new ImageData(frame,w,h);
 }
 function element(tag,cls,text){const e=document.createElement(tag);if(cls)e.className=cls;if(text!=null)e.textContent=text;return e}
 function field(label,input){const node=element("label","matrix-field");node.append(element("span","",label),input);return node}
 function setOptions(select,items){for(const [key,title] of Object.entries(items))select.add(new Option(title,key));return select}
 function create(root){
  const config=defaults();let selected="idle",time0=performance.now(),raf=0,lastPaint=0;
  const top=element("div","matrix-editor-heading");
  top.append(element("h3","","Матрица и анимации"));
  const banner=element("p","matrix-hint","Отображение на устройствах: размер, яркость и эффекты по состояниям.");
  const form=element("div","matrix-fields");
  const width=element("input");width.type="number";width.min="1";width.max="256";width.value=32;
  const height=element("input");height.type="number";height.min="1";height.max="128";height.value=10;
  const fps=element("input");fps.type="number";fps.min="1";fps.max="60";fps.value=25;
  const brightness=element("input");brightness.type="range";brightness.min="1";brightness.max="100";brightness.value=65;
  const brightnessLabel=element("output","matrix-value","65%");
  const brightField=field("Яркость",brightness);brightField.append(brightnessLabel);
  const snake=element("input");snake.type="checkbox";
  const snakeField=field("Змейка, нечётные строки",snake);
  form.append(field("Ширина, LED",width),field("Высота, LED",height),field("Частота, кадр/с",fps),brightField,snakeField);
  const stateButtons=element("div","matrix-state-tabs");stateButtons.setAttribute("role","tablist");
  const canvas=element("canvas","matrix-preview");canvas.setAttribute("aria-label","Предпросмотр матрицы");
  const screen=element("div","matrix-screen");screen.append(canvas);
  const choices=element("div","matrix-choices");
  const preset=setOptions(element("select"),effects);
  const color=element("input");color.type="color";
  const speed=element("input");speed.type="range";speed.min=".1";speed.max="5";speed.step=".1";
  const speedLabel=element("output","matrix-value");
  const speedField=field("Скорость",speed);speedField.append(speedLabel);
  choices.append(field("Эффект",preset),field("Цвет",color),speedField);
  const actions=element("div","matrix-editor-actions");
  const save=element("button","button primary","Сохранить анимации");save.type="button";
  const reset=element("button","button secondary","Сбросить");reset.type="button";
  const msg=element("span","minor");msg.setAttribute("role","status");
  actions.append(save,reset,msg);
  root.append(top,banner,form,stateButtons,screen,choices,actions);
  function syncEditor(){const fx=config.states[selected];preset.value=fx.preset;color.value=fx.color;speed.value=fx.speed;speedLabel.textContent=Number(fx.speed).toFixed(1)+"×";for(const b of stateButtons.children){b.classList.toggle("selected",b.dataset.mode===selected);b.setAttribute("aria-selected",String(b.dataset.mode===selected));}}
  for(const [key,title] of Object.entries(names)){const b=element("button","matrix-state",title);b.type="button";b.dataset.mode=key;b.setAttribute("role","tab");b.onclick=()=>{selected=key;syncEditor()};stateButtons.append(b);}
  function paint(now){
   if(!root.isConnected)return;
   raf=requestAnimationFrame(paint);
   if(document.hidden||root.getClientRects().length===0)return;
   if(now-lastPaint<1000/clamp(config.fps,1,60))return;
   lastPaint=now;
   const w=config.width,h=config.height;
   canvas.width=w;canvas.height=h;
   const ctx=canvas.getContext("2d",{alpha:false});if(!ctx)return;
   ctx.putImageData(imageData(config,selected,(now-time0)/1000),0,0);
   canvas.style.aspectRatio=w+" / "+h;
   canvas.style.imageRendering="pixelated";
  }
  function dims(){
   config.width=clamp(Math.round(Number(width.value)||32),1,256);
   config.height=clamp(Math.round(Number(height.value)||10),1,128);
   if(config.width*config.height>8192){height.value=Math.max(1,Math.floor(8192/config.width));config.height=Number(height.value)}
   config.fps=clamp(Math.round(Number(fps.value)||25),1,60);
   config.brightness=clamp(Math.round(Number(brightness.value)||65),1,100);
   config.serpentine=snake.checked;
   brightnessLabel.textContent=config.brightness+"%";
  }
  for(const field of [width,height,fps,brightness,snake])field.addEventListener("input",dims);
  for(const field of [preset,color,speed])field.addEventListener("input",()=>{
   config.states[selected]={preset:preset.value,color:color.value,speed:clamp(Number(speed.value)||1,.1,5)};
   syncEditor();
  });
  async function load(){
   try{
    const response=await fetch("/api/display",{cache:"no-store"});
    if(!response.ok)throw Error("HTTP "+response.status);
    const loaded=await response.json();
    Object.assign(config,loaded);
    config.states={...JSON.parse(JSON.stringify(defaultEffects)),...loaded.states};
    width.value=config.width;height.value=config.height;fps.value=config.fps;
    brightness.value=config.brightness;snake.checked=!!config.serpentine;
    dims();syncEditor();
    msg.textContent="";
   }catch(e){msg.textContent="Не удалось загрузить настройки: "+e.message}
  }
  save.onclick=async()=>{
   dims();save.disabled=true;msg.textContent="";
   try{
    const r=await fetch("/api/display",{method:"PUT",headers:{"Content-Type":"application/json"},body:JSON.stringify(config)});
    const result=await r.json().catch(()=>({}));
    if(!r.ok)throw Error(result.error||"HTTP "+r.status);
    msg.textContent="Сохранено";
   }catch(e){msg.textContent=e.message}finally{save.disabled=false}
  };
  reset.onclick=()=>{const d=defaults();Object.assign(config,d);
   width.value=d.width;height.value=d.height;fps.value=d.fps;brightness.value=d.brightness;snake.checked=d.serpentine;
   dims();syncEditor();msg.textContent="Выбраны значения по умолчанию. Нажмите «Сохранить».";
  };
  dims();syncEditor();load();raf=requestAnimationFrame(paint);
  window.addEventListener("pagehide",()=>cancelAnimationFrame(raf),{once:true});
 }
 document.addEventListener("DOMContentLoaded",()=>document.querySelectorAll("[data-display-editor]").forEach(create));
})();