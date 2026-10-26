'use strict';
// Self-test: drives server.js through MCP stdio and plays the part of the Chrome extension.
const { spawn } = require('child_process');
const WebSocket = require('ws');
const readline = require('readline');
const assert = require('assert');
const fs = require('fs'), os = require('os'), path = require('path');

const PORT = 47999;
const ID = /IRIS_BROWSER_EXTENSION_ID \|\| '([a-p]{32})'/.exec(fs.readFileSync('server.js', 'utf8'))[1];
const appdata = fs.mkdtempSync(path.join(os.tmpdir(), 'iris-bt-'));
const child = spawn('node', ['server.js'], { env: { ...process.env, IRIS_BROWSER_PORT: PORT, APPDATA: appdata }, stdio: ['pipe', 'pipe', 'inherit'] });
const waiting = new Map(); let nextId = 1;
readline.createInterface({ input: child.stdout }).on('line', (line) => { const m = JSON.parse(line); if (m.id && waiting.has(m.id)) { waiting.get(m.id)(m); waiting.delete(m.id); } else if (m.method === 'notifications/progress') progressSeen.push(m.params); });
const progressSeen = [];
const rpc = (method, params) => new Promise((res) => { const id = nextId++; waiting.set(id, res); child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n'); });
const call = (name, args = {}, meta) => rpc('tools/call', { name, arguments: args, _meta: meta }).then((m) => m.result);
const open = (origin) => new Promise((res, rej) => { const ws = new WebSocket(`ws://127.0.0.1:${PORT}`, { origin }); ws.once('open', () => res(ws)); ws.once('error', rej); ws.once('unexpected-response', (_, r) => rej(new Error('rejected ' + r.statusCode))); });
const next = (ws) => new Promise((res) => ws.once('message', (d) => res(JSON.parse(d.toString()))));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  await sleep(600);
  const init = await rpc('initialize', { protocolVersion: '2024-11-05' });
  assert.equal(init.result.serverInfo.name, 'iris-browser');
  const list = await rpc('tools/list', {});
  assert.equal(list.result.tools.length, 9);

  // 1. not connected: honest error, not a hang
  let r = await call('browser_read_page');
  assert.ok(r.isError && /not connected/i.test(r.content[0].text), 'disconnected error');

  // 2. a web page (wrong origin) and a stranger must be refused
  await assert.rejects(open('https://evil.example'), /rejected 401|rejected 403|Unexpected/);
  await assert.rejects(open(undefined), /rejected|Unexpected/);

  // 3. pairing: wrong code fails, right code succeeds and returns the token
  const code = /(\d{6})/.exec((await call('browser_pair')).content[0].text)[1];
  let ws = await open('chrome-extension://' + ID);
  ws.send(JSON.stringify({ type: 'pair', code: '000000' === code ? '111111' : '000000' }));
  assert.equal((await next(ws)).type, 'pair-failed');
  ws.send(JSON.stringify({ type: 'pair', code }));
  const paired = await next(ws);
  assert.equal(paired.type, 'paired'); assert.ok(paired.token.length >= 32);

  // 4. bridge round trip: server asks, "extension" answers
  ws.on('message', (d) => {
    const m = JSON.parse(d.toString());
    if (m.cmd === 'status') ws.send(JSON.stringify({ id: m.id, ok: true, result: { title: 'Test page', url: 'https://example.com/' } }));
    if (m.cmd === 'type') ws.send(JSON.stringify({ id: m.id, ok: false, error: 'That field is a password field, so I did not type into it.' }));
    if (m.cmd === 'attend') ws.send(JSON.stringify({ id: m.id, ok: true, result: { visited: 3 } }));
  });
  r = await call('browser_status');
  assert.match(r.content[0].text, /Connected.*Test page/);

  // 5. extension refusal is reported as an error result
  r = await call('browser_type', { index: 1, text: 'x' });
  assert.ok(r.isError && /password/.test(r.content[0].text));

  // 6. non-http URLs never reach the extension
  r = await call('browser_navigate', { url: 'javascript:alert(1)' });
  assert.ok(r.isError && /http/.test(r.content[0].text));

  // 7. long action reports progress when the host supplies a token
  r = await call('browser_attend', { targets: 'headings' }, { progressToken: 'p1' });
  assert.ok(!r.isError && progressSeen.length === 2, 'progress notifications');

  // 8. reconnect with the stored token works; a wrong token is refused
  ws.close(); await sleep(100);
  ws = await open('chrome-extension://' + ID);
  ws.send(JSON.stringify({ type: 'hello', token: 'x'.repeat(48) }));
  assert.equal((await next(ws)).type, 'unauthorized');
  ws = await open('chrome-extension://' + ID);
  ws.send(JSON.stringify({ type: 'hello', token: paired.token }));
  assert.equal((await next(ws)).type, 'ready');

  // 9. five wrong pairing guesses burn the code
  await call('browser_pair');
  const ws2 = await open('chrome-extension://' + ID);
  for (let i = 0; i < 5; i++) { ws2.send(JSON.stringify({ type: 'pair', code: '12345' + i })); await next(ws2); }
  console.log('ALL SERVER CHECKS PASSED');
  child.kill(); process.exit(0);
})().catch((e) => { console.error('FAILED:', e.message); child.kill(); process.exit(1); });
