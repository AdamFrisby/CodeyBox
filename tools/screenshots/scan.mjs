import { chromium } from 'playwright-core';
const BASE='http://localhost:5070';
const PAGES=[['01-queue','/'],['02-fleet','/fleet'],['05-capacity','/capacity'],
 ['06-releases','/releases'],['07-suggestions','/suggestions'],['08-plugins','/plugins'],
 ['09-new-item','/work-items/new'],['10-detail',null]];
const b=await chromium.launch({executablePath:process.env.CHROMIUM_PATH});
const p=await (await b.newContext({viewport:{width:1440,height:900}})).newPage();
for(const [n,u] of PAGES){
  let url=u;
  if(!url){ await p.goto(BASE+'/',{waitUntil:'networkidle'});
            url=await p.locator('table tbody tr td a').first().getAttribute('href'); }
  await p.goto(BASE+url,{waitUntil:'networkidle'}); await p.waitForTimeout(500);
  // Any element whose class or role signals a warning/error/alert banner.
  const banners=await p.$$eval('[class*=alert],[class*=warn],[class*=error],[class*=danger],[role=alert],[class*=banner],[class*=notice]',
    els=>els.map(e=>e.innerText.trim().replace(/\s+/g,' ').slice(0,150)).filter(t=>t.length>0));
  // Red/amber text is the other tell.
  const colored=await p.$$eval('*', els=>els.filter(e=>{
      const c=getComputedStyle(e).color;
      const m=c.match(/\d+/g); if(!m) return false;
      const [r,g,bl]=m.map(Number);
      return (r>140 && g<110 && bl<110) || (r>180 && g>110 && g<190 && bl<90);
    }).map(e=>e.innerText.trim().replace(/\s+/g,' ').slice(0,80)).filter(t=>t&&t.length<120).slice(0,6));
  console.log(`\n### ${n}`);
  banners.forEach(t=>console.log(`  BANNER: ${t}`));
  [...new Set(colored)].forEach(t=>console.log(`  RED/AMBER: ${t}`));
  if(!banners.length && !colored.length) console.log('  (clean)');
}
await b.close();
