/* ORBIT scene engine — understated, responsive, low-power celestial background. */
(() => {
  "use strict";
  const canvas = document.getElementById("orbitCanvas");
  if (!canvas) return;
  const ctx = canvas.getContext("2d", {alpha:true, desynchronized:true});
  if (!ctx) return;
  const reduced = window.matchMedia("(prefers-reduced-motion: reduce)");
  const fine = window.matchMedia("(pointer:fine)");
  let width=1,height=1,dpr=1,points=[],dust=[],raf=0,last=0,active=true;
  let aim={x:0.5,y:0.5},cursor={x:0.5,y:0.5},elapsed=0;
  const random=(lo,hi)=>lo+Math.random()*(hi-lo);
  const fresh=()=> {
    const count = Math.min(128,Math.max(44,Math.round(width*height/14500)));
    points=Array.from({length:count},()=>({
      x:Math.random()*width,y:Math.random()*height,
      vx:random(-.08,.08),vy:random(-.075,.075),
      r:random(.5,1.45),a:random(.13,.55),
      pulse:random(0,Math.PI*2),speed:random(.3,.7)
    }));
    dust=Array.from({length:Math.min(9,Math.max(4,Math.round(width/190)))},()=>({
      x:random(0,width),y:random(0,height),r:random(120,360),
      phase:random(0,Math.PI*2),dx:random(-.13,.13),dy:random(-.13,.13)
    }));
  };
  function resize() {
    dpr=Math.min(window.devicePixelRatio||1,1.5);
    width=Math.max(1,innerWidth);height=Math.max(1,innerHeight);
    canvas.width=Math.floor(width*dpr);canvas.height=Math.floor(height*dpr);
    canvas.style.width=width+"px";canvas.style.height=height+"px";
    ctx.setTransform(dpr,0,0,dpr,0,0);
    fresh();draw(0);
  }
  function draw(dt) {
    ctx.clearRect(0,0,width,height);
    const move=!reduced.matches && active;
    elapsed+=dt;
    cursor.x+=(aim.x-cursor.x)*.045;cursor.y+=(aim.y-cursor.y)*.045;
    for (const g of dust) {
      if(move){g.x+=g.dx*dt*.06;g.y+=g.dy*dt*.06}
      if(g.x<-g.r)g.x=width+g.r;if(g.x>width+g.r)g.x=-g.r;
      if(g.y<-g.r)g.y=height+g.r;if(g.y>height+g.r)g.y=-g.r;
      const grd=ctx.createRadialGradient(g.x,g.y,0,g.x,g.y,g.r);
      const a=.038+.018*Math.sin(elapsed*.0002+g.phase);
      grd.addColorStop(0,"rgba(123,170,255,"+Math.max(0,a)+")");
      grd.addColorStop(.48,"rgba(99,126,224,.018)");
      grd.addColorStop(1,"rgba(75,90,160,0)");
      ctx.fillStyle=grd;ctx.beginPath();ctx.arc(g.x,g.y,g.r,0,Math.PI*2);ctx.fill();
    }
    const shiftX=(cursor.x-.5)*19,shiftY=(cursor.y-.5)*13;
    for(let i=0;i<points.length;i++){
      const p=points[i];
      if(move){
        p.x+=p.vx*dt*.06;p.y+=p.vy*dt*.06;
        if(p.x<-4)p.x=width+4;if(p.x>width+4)p.x=-4;
        if(p.y<-4)p.y=height+4;if(p.y>height+4)p.y=-4;
      }
      const x=p.x+shiftX*p.r*.65,y=p.y+shiftY*p.r*.65;
      const alpha=p.a*(.82+.18*Math.sin(elapsed*.0005*p.speed+p.pulse));
      ctx.fillStyle="rgba(212,226,255,"+alpha+")";
      ctx.beginPath();ctx.arc(x,y,p.r,0,Math.PI*2);ctx.fill();
      if(i%7===0){
        const glow=ctx.createRadialGradient(x,y,0,x,y,12);
        glow.addColorStop(0,"rgba(162,193,255,"+alpha*.19+")");
        glow.addColorStop(1,"rgba(162,193,255,0)");
        ctx.fillStyle=glow;ctx.fillRect(x-12,y-12,24,24);
      }
    }
    // Sparse constellations — not an all-to-all neural network.
    for(let i=0;i<points.length;i+=3){
      const p=points[i],q=points[(i+5)%points.length];
      const distance=Math.hypot(p.x-q.x,p.y-q.y);
      if(distance>210)continue;
      ctx.beginPath();ctx.moveTo(p.x+shiftX,p.y+shiftY);ctx.lineTo(q.x+shiftX,q.y+shiftY);
      ctx.strokeStyle="rgba(147,177,231,"+(1-distance/210)*.075+")";
      ctx.lineWidth=.7;ctx.stroke();
    }
  }
  function tick(now){
    raf=0;
    if(!active || document.hidden)return;
    if(now-last>=30){draw(Math.min(now-last,75));last=now}
    raf=requestAnimationFrame(tick);
  }
  document.addEventListener("visibilitychange",()=>{
    active=!document.hidden;
    if(active&&!raf){last=performance.now();raf=requestAnimationFrame(tick)}
    if(!active&&raf){cancelAnimationFrame(raf);raf=0}
  });
  if(fine.matches){
    window.addEventListener("pointermove",e=>{
      aim.x=Math.min(1,Math.max(0,e.clientX/Math.max(innerWidth,1)));
      aim.y=Math.min(1,Math.max(0,e.clientY/Math.max(innerHeight,1)));
    },{passive:true});
  }
  window.addEventListener("resize",resize,{passive:true});
  resize();
  if(!reduced.matches)raf=requestAnimationFrame(tick);
  reduced.addEventListener?.("change",()=>{
    if(reduced.matches){if(raf)cancelAnimationFrame(raf);raf=0;draw(0)}
    else if(!raf){last=performance.now();raf=requestAnimationFrame(tick)}
  });
})();
