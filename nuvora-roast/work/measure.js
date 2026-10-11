const { chromium } = require('playwright');
const fs = require('fs');
const URL = 'https://nuvora-ug.netlify.app/';
const UA = 'Mozilla/5.0 (Linux; Android 11; SM-A125F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36';
(async () => {
  const browser = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium-1194/chrome-linux/chrome', args: ['--no-sandbox','--use-gl=swiftshader','--enable-unsafe-swiftshader'] });
  const ctx = await browser.newContext({ viewport: {width:360,height:740}, deviceScaleFactor:2, isMobile:true, hasTouch:true, userAgent: UA });
  const page = await ctx.newPage();
  const cdp = await ctx.newCDPSession(page);
  await cdp.send('Network.enable');
  await cdp.send('Network.emulateNetworkConditions', { offline:false, latency:400, downloadThroughput: 400*1000/8, uploadThroughput: 400*1000/8 });
  await cdp.send('Network.setCacheDisabled', {cacheDisabled:true});
  const reqs = {}; const t0 = Date.now();
  cdp.on('Network.requestWillBeSent', e => { reqs[e.requestId] = {url:e.request.url, start:Date.now()-t0, bytes:0}; });
  cdp.on('Network.responseReceived', e => { if(reqs[e.requestId]) { reqs[e.requestId].status=e.response.status; reqs[e.requestId].type=e.type; reqs[e.requestId].enc=e.response.headers['content-encoding']; }});
  cdp.on('Network.loadingFinished', e => { if(reqs[e.requestId]) { reqs[e.requestId].bytes=e.encodedDataLength; reqs[e.requestId].end=Date.now()-t0; }});
  // splash first appears
  const snap = async (p)=>{ try{ const r=await cdp.send('Page.captureScreenshot',{format:'png'}); fs.writeFileSync(p, Buffer.from(r.data,'base64')); }catch(e){console.error('snap fail',e.message)} };
  const marks = {};
  page.on('console', m => { if (/error|warn/i.test(m.type())) (marks.console ||= []).push(m.text().slice(0,200)); });
  const nav = page.goto(URL, {waitUntil:'commit', timeout:300000});
  await nav;
  const tick = async (label, ms) => { await page.waitForTimeout(ms); };
  // poll for splash gone / flutter glass pane / first frame
  let firstPaintMs=null, splashSeenMs=null, flutterGlassMs=null, splashGoneMs=null;
  const start = Date.now();
  while (Date.now()-start < 240000) {
    const s = await page.evaluate(() => ({
      splash: !!document.getElementById('nv-splash'),
      glass: !!document.querySelector('flutter-view, flt-glass-pane'),
      fp: (performance.getEntriesByName('first-paint')[0]||{}).startTime ?? null,
      fcp: (performance.getEntriesByName('first-contentful-paint')[0]||{}).startTime ?? null
    })).catch(()=>null);
    if (s) {
      if (s.fp!=null && firstPaintMs==null) { firstPaintMs=s.fp; await snap('../screenshots/01-mobile-splash.png'); }
      if (s.fcp!=null) marks.fcp = s.fcp;
      if (s.glass && flutterGlassMs==null) flutterGlassMs = Date.now()-t0;
      if (!s.splash && s.glass && splashGoneMs==null) { splashGoneMs = Date.now()-t0; break; }
    }
    await page.waitForTimeout(250);
  }
  await page.waitForTimeout(1500);
  await snap('../screenshots/02-mobile-first-ui-3g.png');
  const list = Object.values(reqs).sort((a,b)=>b.bytes-a.bytes);
  const total = list.reduce((a,r)=>a+r.bytes,0);
  const out = { firstPaintMs, fcpMs: marks.fcp, flutterGlassAppearedMs: flutterGlassMs, firstRealUiMs: splashGoneMs, totalBytes: total, requestCount: list.length, top10: list.slice(0,10).map(r=>({url:r.url,bytes:r.bytes,status:r.status,type:r.type,enc:r.enc,endMs:r.end})), allUrls: list.map(r=>({url:r.url,bytes:r.bytes,status:r.status})), console: marks.console };
  fs.writeFileSync('perf-3g.json', JSON.stringify(out,null,2));
  console.log(JSON.stringify(out,null,2));
  await browser.close();
})();
