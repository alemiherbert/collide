const L=require('./lib'); const fs=require('fs');
const {chromium}=require('playwright');
(async()=>{ const S='../screenshots/';
const browser=await chromium.launch({executablePath:'/opt/pw-browsers/chromium-1194/chrome-linux/chrome',args:['--no-sandbox','--use-gl=swiftshader','--enable-unsafe-swiftshader']});
const ctx=await browser.newContext({viewport:{width:360,height:740},deviceScaleFactor:2}); const page=await ctx.newPage();
const ready=async()=>{await page.waitForFunction(()=>!document.getElementById('nv-splash')&&document.querySelector('flt-glass-pane'),null,{timeout:90000}); await page.waitForTimeout(2500);};
// 1 path deep link
let r=await page.goto('https://nuvora-ug.netlify.app/register'); console.log('path /register ->',r.status()); await page.screenshot({path:S+'24-deeplink-path-404.png'});
// 2 hash deep link, logged out
r=await page.goto('https://nuvora-ug.netlify.app/#/register'); await ready(); console.log('#/register logged-out ->',r.status(),await page.evaluate(()=>location.hash)); await page.screenshot({path:S+'25-deeplink-hash-register.png'});
r=await page.goto('https://nuvora-ug.netlify.app/#/dashboard'); await page.reload(); await ready(); console.log('#/dashboard logged-out ->',await page.evaluate(()=>location.hash));
// 3 logged in then reload on profile-ish
await page.goto('https://nuvora-ug.netlify.app/#/login'); await page.reload(); await ready();
await page.mouse.click(180,323); await page.keyboard.type('700000099'); await page.mouse.click(70,428); await page.keyboard.type(process.env.NUVORA_TEST_PIN); await page.waitForTimeout(5000);
console.log('logged in',await page.evaluate(()=>location.hash));
await page.reload(); await ready(); console.log('after reload',await page.evaluate(()=>location.hash)); await page.screenshot({path:S+'26-after-reload-logged-in.png'});
await page.goto('https://nuvora-ug.netlify.app/#/login'); await page.reload(); await ready(); console.log('logged-in user opens #/login ->',await page.evaluate(()=>location.hash));
await browser.close(); })();
