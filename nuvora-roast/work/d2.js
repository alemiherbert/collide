const L=require('./lib'); const fs=require('fs');
(async()=>{ const {browser,page,log}=await L.launch(false); const S='../screenshots/';
await L.clickText(page,'Create an account'); await page.waitForTimeout(1500); await page.screenshot({path:S+'21-desktop-terms.png'});
await L.clickText(page,'I accept'); await page.waitForTimeout(1500); await page.screenshot({path:S+'22-desktop-signup.png'});
console.log(await page.evaluate(()=>location.hash));
await page.goto('https://nuvora-ug.netlify.app/#/login'); await page.reload(); await page.waitForFunction(()=>!document.getElementById('nv-splash'),null,{timeout:60000}); await page.waitForTimeout(2500);
await page.mouse.click(700,299); await page.keyboard.type('700000099'); await page.mouse.click(73,404); await page.keyboard.type(process.env.NUVORA_TEST_PIN);
await page.waitForTimeout(5000); console.log(await page.evaluate(()=>location.hash)); await page.screenshot({path:S+'23-desktop-dashboard.png'});
await browser.close(); })();
