'use strict';
// Loads content.js into jsdom with a fake chrome API and exercises each command.
const { JSDOM } = require('jsdom');
const fs = require('fs');
const assert = require('assert');

const dom = new JSDOM(`<body>
  <h1>Main title</h1><h2>Second title</h2>
  <a href="/next">Next page</a>
  <button id="go">Go</button>
  <input id="name" placeholder="Your name" value="secret-typed-before">
  <input id="pw" type="password" aria-label="Password">
  <input id="card" autocomplete="cc-number" aria-label="Card">
  <input id="card2" name="credit_card_number" aria-label="Payment number">
  <div contenteditable="true" id="editor">draft private text</div>
  <input id="locked" aria-label="Locked" readonly value="locked">
  <select id="size" aria-label="Size"><option value="s">Small</option><option value="l">Large</option></select>
  <p style="display:none">hidden</p>
</body>`, { runScripts: 'outside-only', url: 'https://example.com/' });
const { window } = dom;
window.Element.prototype.getBoundingClientRect = () => ({ left: 10, top: 10, width: 120, height: 24, right: 130, bottom: 34 });
window.Range.prototype.getClientRects = () => [{ left: 10, top: 10, width: 120, height: 24, right: 130, bottom: 34 }];
window.Element.prototype.scrollIntoView = () => {};
window.scrollTo = window.scrollBy = () => {};
// jsdom has no innerText; mimic Chrome by skipping display:none subtrees.
Object.defineProperty(window.HTMLElement.prototype, 'innerText', { get() {
  const walk = (n) => n.nodeType === 3 ? n.textContent
    : n.nodeType === 1 && window.getComputedStyle(n).display === 'none' ? '' : [...n.childNodes].map(walk).join(' ');
  return walk(this);
} });
let listener;
window.chrome = { storage: { sync: { get: (d, cb) => cb(d) } }, runtime: { onMessage: { addListener: (fn) => { listener = fn; } } } };
window.eval(fs.readFileSync('extension/content.js', 'utf8'));
const ask = (cmd, args = {}) => new Promise((res) => listener({ cmd, args }, {}, res));
let clicks = 0; window.document.getElementById('go').addEventListener('click', () => clicks++);

(async () => {
  const page = (await ask('read')).result;
  const kinds = page.elements.map((e) => e.kind + ':' + e.text);
  assert.ok(kinds.includes('heading:Main title') && kinds.includes('button:Go') && kinds.includes('link:Next page'), 'elements listed');
  assert.ok(!JSON.stringify(page).includes('secret-typed-before'), 'typed field values are never read');
  assert.ok(!JSON.stringify(page).includes('draft private text'), 'editable text is never read as page prose');
  assert.ok(!JSON.stringify(page).includes('hidden'), 'hidden text is not read');
  assert.match(page.notice, /untrusted/);
  const idx = (kind, text) => page.elements.find((e) => e.kind === kind && e.text === text).i;

  const longParagraph = window.document.createElement('p');
  longParagraph.textContent = 'page context '.repeat(400);
  window.document.body.appendChild(longParagraph);
  const conciseRead = (await ask('read')).result;
  const detailedRead = (await ask('read', { fullDetail: true })).result;
  assert.ok(conciseRead.text.length <= 1210, 'concise page read is capped');
  assert.ok(detailedRead.text.length > conciseRead.text.length, 'full detail is available on request');

  let r = await ask('focus', { index: idx('button', 'Go') });
  assert.ok(r.ok && window.document.getElementById('__iris_attention'), 'overlay drawn');
  assert.equal(window.document.getElementById('__iris_attention').style.opacity, '1');

  r = await ask('click', { index: idx('button', 'Go') });
  assert.ok(r.ok && clicks === 1, 'click fired once');

  r = await ask('type', { index: idx('field', 'Your name'), text: 'Iris' });
  assert.ok(r.ok && r.result.verified && window.document.getElementById('name').value === 'Iris', 'typed and verified');

  for (const label of ['Password', 'Card', 'Payment number']) {
    r = await ask('type', { index: idx('field', label), text: 'nope' });
    assert.ok(!r.ok && /password or payment/.test(r.error), label + ' refused');
  }
  assert.equal(window.document.getElementById('pw').value, '', 'password untouched');
  r = await ask('type', { index: idx('field', 'Locked'), text: 'changed' });
  assert.ok(!r.ok && /disabled or read-only/.test(r.error), 'read-only field refused');

  const sel = page.elements.find((e) => e.type === 'select-one');
  r = await ask('type', { index: sel.i, text: 'Large' });
  assert.ok(r.ok && window.document.getElementById('size').value === 'l', 'select chosen');
  r = await ask('type', { index: sel.i, text: 'Huge' });
  assert.ok(!r.ok && /no option/.test(r.error), 'unknown option reported');

  r = await ask('type', { index: idx('button', 'Go'), text: 'x' });
  assert.ok(!r.ok && /not a field/.test(r.error), 'non-field refused');

  r = await ask('attend', { dwellMs: 10 });
  assert.ok(r.ok && r.result.visited === 2 && r.result.titles[0] === 'Main title', 'sweeps headings and reports them');

  r = await ask('click', { index: 999 });
  assert.ok(!r.ok && /browser_read_page/.test(r.error) && /stale index/.test(r.error),
    'stale index instructs the agent to read the page again and not retry');
  console.log('ALL EXTENSION CHECKS PASSED');
})().catch((e) => { console.error('FAILED:', e.stack || e.message); process.exit(1); });
