'use strict';
// Service worker: keeps one WebSocket to Iris (127.0.0.1 only) and relays commands to the active tab.
const BRIDGE = 'ws://127.0.0.1:47831';
let socket = null;
let keepAlive = 0;

const settings = () => chrome.storage.local.get({ token: '', enabled: true });
const setStatus = (status) => chrome.storage.local.set({ status });

async function connect() {
  const { enabled, token } = await settings();
  if (!enabled) {
    if (socket) { try { socket.close(); } catch { /* already closed */ } }
    socket = null;
    setStatus('offline');
    return;
  }
  if (!token || (socket && socket.readyState <= 1)) return;
  open({ type: 'hello', token });
}

function open(firstMessage) {
  if (socket) { try { socket.close(); } catch { /* already closed */ } }
  try { socket = new WebSocket(BRIDGE); } catch { return; }
  const mine = socket;
  mine.onopen = () => {
    mine.send(JSON.stringify(firstMessage));
    clearInterval(keepAlive);
    // Traffic every 20 s keeps the service worker alive while connected.
    keepAlive = setInterval(() => mine.readyState === 1 && mine.send('{"type":"ping"}'), 20000);
  };
  mine.onmessage = (event) => onMessage(mine, JSON.parse(event.data));
  mine.onclose = () => { clearInterval(keepAlive); if (socket === mine) { socket = null; setStatus('offline'); } };
  mine.onerror = () => {};
}

async function onMessage(ws, message) {
  if (message.type === 'paired') { await chrome.storage.local.set({ token: message.token }); setStatus('connected'); return; }
  if (message.type === 'pair-failed') { setStatus('pair-failed'); ws.close(); return; }
  if (message.type === 'unauthorized') { await chrome.storage.local.set({ token: '' }); setStatus('offline'); return; }
  if (message.type === 'ready') { setStatus('connected'); return; }
  if (!message.cmd) return;

  const reply = (payload) => ws.readyState === 1 && ws.send(JSON.stringify({ id: message.id, ...payload }));
  const { enabled } = await settings();
  if (!enabled) return reply({ ok: false, error: 'Browser control is switched off in the Iris Browser extension.' });
  try { reply({ ok: true, result: await run(message.cmd, message.args || {}) }); }
  catch (error) { reply({ ok: false, error: error.message }); }
}

async function activeTab() {
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!tab || !tab.id) throw new Error('There is no active browser tab.');
  return tab;
}

function waitForLoad(tabId) {
  return new Promise((resolve) => {
    const finish = () => { chrome.tabs.onUpdated.removeListener(listener); clearTimeout(timer); resolve(); };
    const listener = (id, info) => { if (id === tabId && info.status === 'complete') finish(); };
    const timer = setTimeout(finish, 20000);
    chrome.tabs.onUpdated.addListener(listener);
  });
}

async function toTab(tabId, cmd, args) {
  const ask = () => chrome.tabs.sendMessage(tabId, { cmd, args });
  let answer;
  try { answer = await ask(); }
  catch { // page was open before the extension was installed
    await chrome.scripting.executeScript({ target: { tabId }, files: ['content.js'] });
    answer = await ask();
  }
  if (!answer) throw new Error('The page did not answer.');
  if (!answer.ok) throw new Error(answer.error);
  return answer.result;
}

async function run(cmd, args) {
  if (cmd === 'tabs') {
    const tabs = await chrome.tabs.query({});
    const [active] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    return tabs
      .filter((t) => t.id && t.url && !/^(chrome|edge|about|chrome-extension|devtools):/.test(t.url))
      .slice(0, 60)
      .map((t) => ({
        id: t.id,
        title: String(t.title || '').slice(0, 160),
        url: String(t.url).slice(0, 500),
        active: !!(active && active.id === t.id),
        audible: !!t.audible,
        muted: !!(t.mutedInfo && t.mutedInfo.muted),
        windowId: t.windowId,
      }));
  }
  if (cmd === 'switch') {
    const target = await chrome.tabs.get(Number(args.id));
    if (!target) throw new Error('That tab is gone.');
    await chrome.windows.update(target.windowId, { focused: true });
    await chrome.tabs.update(target.id, { active: true });
    return { title: target.title, url: target.url };
  }

  const tab = await activeTab();
  if (cmd === 'status') return { title: tab.title, url: tab.url };
  if (cmd === 'navigate') {
    const loaded = waitForLoad(tab.id);
    await chrome.tabs.update(tab.id, { url: args.url });
    await loaded;
    const now = await chrome.tabs.get(tab.id);
    return { title: now.title, url: now.url };
  }
  if (/^(chrome|edge|about|chrome-extension|devtools):/.test(tab.url || '')) throw new Error('Iris cannot use this kind of browser page.');
  return toTab(tab.id, cmd, args);
}

chrome.runtime.onMessage.addListener((message) => {
  if (message && message.type === 'pair' && /^\d{6}$/.test(message.code)) open({ type: 'pair', code: message.code });
  if (message && message.type === 'reconnect') connect();
});
chrome.alarms.create('reconnect', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(connect);
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
connect();
