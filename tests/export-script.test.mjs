import { createServer } from 'node:http';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { readFile, writeFile, mkdtemp, mkdir, readdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';

const sourceFile = path.resolve(process.argv[2] || fileURLToPath(new URL('../scripts/Export-Dumper.ps1', import.meta.url)));
const source = await readFile(sourceFile, 'utf8');
assert.ok(!/Start-Process|ProcessStartInfo|Add-Type|Assembly\]::Load|DownloadFile|OutFile|New-Item|Join-Path.*LocalApplicationData/.test(source), 'entry point must use only built-in APIs without helper downloads');
const root = await mkdtemp(path.join(tmpdir(), 'dumper-script-test-'));
const id = '79379301-38a4-4d9c-ad40-e8c61dc1fef1';
const credential = 'usd_' + 'T'.repeat(43);
const sha = data => createHash('sha256').update(data).digest('hex');
const data = Buffer.from('\ufeffHWID: synthetic\r\n中文 😀\r\n' + 'test'.repeat(40000));
const split = 27; // Split in the middle of UTF-8; the client must preserve raw bytes.
let mode, requests, attempts;
const server = createServer((req, res) => {
  requests.push(req.url);
  assert.equal(req.headers.authorization, `Bearer ${credential}`);
  assert.ok(!req.url.includes(credential));
  const json = value => { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(value)); };
  if (mode === 'denied') { res.writeHead(401); res.end(); return; }
  if (mode === 'redirect') { res.writeHead(302, { Location: '/unexpected' }); res.end(); return; }
  if (req.url.endsWith('/resolve')) return json({ ok: true, archiveId: id });
  const plain = mode === 'empty' ? Buffer.alloc(0) : data;
  const parts = mode === 'empty' ? [plain] : [plain.subarray(0, split), plain.subarray(split)];
  if (req.url.endsWith('/export')) return json({
    ok: true, version: 2, archiveId: mode === 'wrong-archive' ? '00000000-0000-4000-8000-000000000000' : id,
    partCount: parts.length, totalPlainBytes: plain.length,
    plaintextSha256: mode === 'wrong-total-hash' ? '0'.repeat(64) : sha(plain),
    parts: parts.map((p, index) => ({ index, plainBytes: mode === 'oversized-manifest' ? 16777217 : p.length })),
  });
  const match = req.url.match(/\/export\/parts\/(\d+)$/);
  if (!match) { res.writeHead(404); res.end(); return; }
  const index = Number(match[1]);
  attempts[index] = (attempts[index] || 0) + 1;
  if (index === 1 && (mode === 'revoked' || mode === 'retry-503' && attempts[index] === 1)) {
    res.writeHead(mode === 'revoked' ? 401 : 503, { 'Retry-After': '0' }); res.end(); return;
  }
  const part = parts[index];
  res.setHeader('X-USD-Plain-SHA256', mode === 'bad-part-hash' && index === 1 ? '0'.repeat(64) : sha(part));
  res.setHeader('X-USD-Part-Index', mode === 'wrong-index' ? '99' : String(index));
  if (mode === 'excess-body') { res.end(Buffer.concat([part, Buffer.from('extra')])); return; }
  if (mode === 'short-body') { res.end(part.subarray(0, Math.max(0, part.length - 1))); return; }
  if (mode === 'truncated-retry' && index === 1 && attempts[index] === 1) {
    res.writeHead(200, { 'Content-Length': part.length }); res.write(part.subarray(0, 10)); setTimeout(() => res.destroy(), 30); return;
  }
  res.end(part);
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}/api/dumper/archives`;
let passed = 0;
try {
  for (const shell of ['powershell.exe', 'pwsh.exe']) {
    for (mode of ['success','bundle','empty','retry-503','truncated-retry','denied','redirect','bad-part-hash','wrong-total-hash','wrong-index','wrong-archive','oversized-manifest','excess-body','short-body','revoked','existing','extension']) {
      requests = []; attempts = {};
      const directory = path.join(root, `${shell}-${mode}`);
      await mkdir(directory);
      const destination = path.join(directory, mode === 'extension' ? 'result.bin' : 'result.txt');
      if (mode === 'existing') await writeFile(destination, 'keep me');
      const env = { ...process.env, TEST_SCRIPT: sourceFile, TEST_ORIGIN: origin, LOCALAPPDATA: directory, TEMP: directory, TMP: directory };
      delete env.PSModulePath;
      const command = "$ErrorActionPreference='Stop'; function Read-Host { param([string]$Prompt,[switch]$AsSecureString) $value=[Console]::ReadLine(); if($AsSecureString) { ConvertTo-SecureString -String $value -AsPlainText -Force } else { $value } }; $source=[IO.File]::ReadAllText($env:TEST_SCRIPT).Replace('https://www.screenshare.cn/api/dumper/archives',$env:TEST_ORIGIN); & ([scriptblock]::Create($source))";
      const input = mode === 'bundle' ? `usd-restore-v1:00000000-0000-4000-8000-000000000000:${credential}` : credential;
      const result = await new Promise((resolve, reject) => {
        const child = spawn(shell, ['-NoProfile', '-Command', command], { env, windowsHide: true, stdio: ['pipe','pipe','pipe'] });
        let output = ''; child.stdout.on('data', b => output += b); child.stderr.on('data', b => output += b);
        child.on('error', reject); child.on('exit', code => resolve({ code, output })); child.stdin.end(`${input}\n${destination}\n`);
      });
      assert.ok(!result.output.includes(credential), 'credential in process output');
      const success = ['success','bundle','empty','retry-503','truncated-retry'].includes(mode);
      assert.equal(result.code === 0, success, `${shell} ${mode}: ${result.output}`);
      if (success) {
        assert.deepEqual(await readFile(destination), mode === 'empty' ? Buffer.alloc(0) : data);
        assert.deepEqual(await readdir(directory), ['result.txt'], 'only TXT may be created');
      } else if (mode === 'existing') { assert.equal(await readFile(destination, 'utf8'), 'keep me'); }
      else { assert.deepEqual(await readdir(directory), [], 'failed export must leave no files'); }
      assert.ok(requests.every(url => /\/resolve$|\/export(?:\/parts\/\d+)?$/.test(url)), 'unexpected helper or redirect download');
      if (['retry-503','truncated-retry'].includes(mode)) assert.equal(attempts[1], 2);
      passed++; console.log(`PASS ${shell} ${mode}`);
    }
  }
} finally { await new Promise(resolve => server.close(resolve)); }
console.log(`${passed} checks passed. Only synthetic TXT outputs retained at ${root}`);
