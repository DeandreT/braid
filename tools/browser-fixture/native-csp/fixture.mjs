// Local controls only. Run inside a fresh user/network namespace, never against a live target.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { readFile } from 'node:fs/promises';
import {execFileSync} from 'node:child_process';
import { createRequire } from 'node:module';
import { resolve, join } from 'node:path';

export const origin = 'http://braid.invalid';
export async function launchFixture() {
  const { BRAID_CHROME, BRAID_DRIVER, BRAID_CHROME_SHA256, BRAID_BUNDLE_SHA256 } = process.env;
  // A fresh namespace has only its (down) loopback interface. Do not enable it.
  const interfaces = (await readFile('/proc/net/dev', 'utf8')).trim().split('\n').slice(2);
  assert.deepEqual(interfaces.map(line => line.split(':')[0].trim()), ['lo']);
  const [lo] = JSON.parse(execFileSync('/usr/bin/ip', ['-j','link','show','dev','lo'], {encoding:'utf8'}));
  assert.equal(lo.flags.includes('UP'), false);
  for (const [path, pin] of [[BRAID_CHROME, BRAID_CHROME_SHA256], [join(BRAID_DRIVER,'lib/coreBundle.js'), BRAID_BUNDLE_SHA256]]) {
    assert.match(pin, /^[0-9a-f]{64}$/);
    const hash = createHash('sha256');
    for await (const chunk of createReadStream(path)) hash.update(chunk);
    assert.equal(hash.digest('hex'), pin);
  }
  const {chromium} = createRequire(import.meta.url)(resolve(BRAID_DRIVER));
  const browser = await chromium.launch({executablePath: resolve(BRAID_CHROME), chromiumSandbox:true,
    headless:true, timeout:10000, env:{HOME:process.env.HOME,TMPDIR:process.env.TMPDIR,PATH:'/usr/bin'}});
  assert.equal(browser.version(), '157.0.8090.0');
  return browser;
}
export async function openFixture(browser) {
  const page = await browser.newPage({serviceWorkers:'block'});
  page.setDefaultTimeout(3000);
  await page.route('**/*', route => {
    if (![origin+'/fixture', origin+'/replacement'].includes(route.request().url())) return route.abort();
    return route.fulfill({status:200,headers:{'content-type':'text/html','content-security-policy':"worker-src 'none'; object-src 'none'"},body:'<!doctype html><title>local CSP control</title>'});
  });
  await page.goto(origin+'/fixture');
  return page;
}
export async function violate(page) {
  // This page signal synchronizes the stimulus only. It is NOT the receipt.
  return page.evaluate(() => new Promise(resolve => {
    const timer = setTimeout(() => resolve(false), 1500);
    document.addEventListener('securitypolicyviolation', e => {
      clearTimeout(timer);
      resolve(e.isTrusted && e.effectiveDirective === 'worker-src' && e.disposition === 'enforce');
    }, {once:true});
    new Worker('/owned-worker.js').onerror = e => e.preventDefault();
  }));
}
