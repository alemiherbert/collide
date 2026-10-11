const { chromium } = require('playwright');
const fs = require('fs');
exports.URL='https://nuvora-ug.netlify.app/';
exports.UA='Mozilla/5.0 (Linux; Android 11; SM-A125F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36';
exports.launch = async (mobile=true, path='') => {
  const browser = await chromium.launch({ executablePath:'/opt/pw-browsers/chromium-1194/chrome-linux/chrome', args:['--no-sandbox','--use-gl=swiftshader','--enable-unsafe-swiftshader'] });
  const ctx = await browser.newContext(mobile?{viewport:{width:360,height:740},deviceScaleFactor:2,hasTouch:false}:{viewport:{width:1440,height:900}});
  const page = await ctx.newPage();
  const log=[]; page.on('request',r=>log.push({m:r.method(),u:r.url(),pd:(r.postData()||'').slice(0,300)}));
  page.on('response',async r=>{ const l=log.find(x=>x.u===r.url()&&!x.s); if(l){l.s=r.status(); }});
  await page.goto(exports.URL+path,{waitUntil:'load'});
  await page.waitForFunction(()=>!document.getElementById('nv-splash'),null,{timeout:90000});
  await page.waitForTimeout(2500);
  for (let i=0;i<4;i++){ if(await page.locator('flt-semantics').count()>0) break;
    await page.locator('flt-semantics-placeholder').focus().catch(()=>{}); await page.keyboard.press('Enter'); await page.waitForTimeout(2000); }
  return {browser,ctx,page,log};
};
exports.dump = async (page) => page.evaluate(()=>[...document.querySelectorAll('flt-semantics')].filter(e=>e.id!=='flt-semantic-node-0').map(e=>{const r=e.getBoundingClientRect();const t=(e.innerText||'').trim().replace(/\n/g,' | ');return {id:e.id.replace('flt-semantic-node-',''),role:e.getAttribute('role'),aria:e.getAttribute('aria-label'),text:t.slice(0,70),x:Math.round(r.x),y:Math.round(r.y),w:Math.round(r.width),h:Math.round(r.height),clk:e.style.pointerEvents==='auto'}}).filter(n=>n.w>0&&n.h>0&&(n.text||n.aria||n.role)));
exports.sem = (page)=>page.evaluate(()=>[...document.querySelectorAll('flt-semantics')].filter(e=>e.getAttribute('role')||(!e.childElementCount&&e.textContent.trim())).map(e=>{const r=e.getBoundingClientRect();return [e.getAttribute('role')||'-',(e.getAttribute('aria-label')||e.textContent||'').trim().slice(0,400),Math.round(r.x),Math.round(r.y),Math.round(r.width),Math.round(r.height)].join(' ; ')}).join('\n'));
exports.clickText = async (page,t)=>{ const p=await page.evaluate((t)=>{const e=[...document.querySelectorAll('flt-semantics')].filter(x=>x.getAttribute('role')==='button'&&x.textContent.includes(t))[0]; if(!e) return null; const r=e.getBoundingClientRect(); return [r.x+r.width/2,r.y+r.height/2]},t); if(!p) throw new Error('no button '+t); await page.mouse.click(p[0],p[1]); await page.waitForTimeout(1800); };
