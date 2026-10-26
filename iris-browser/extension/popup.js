'use strict';
const $ = (id) => document.getElementById(id);
const LABELS = { connected: 'Connected to Iris', offline: 'Not connected', 'pair-failed': 'That code did not work. Ask Iris for a new one.' };

async function load() {
  const local = await chrome.storage.local.get({ enabled: true, status: 'offline' });
  const sync = await chrome.storage.sync.get({ style: { color: '#ff3b4d', width: 3 } });
  $('enabled').checked = local.enabled;
  $('color').value = sync.style.color;
  $('width').value = sync.style.width;
  $('status').textContent = LABELS[local.status] || local.status;
}

const saveStyle = () => chrome.storage.sync.set({ style: { color: $('color').value, width: Number($('width').value) } });
$('enabled').onchange = () => { chrome.storage.local.set({ enabled: $('enabled').checked }); chrome.runtime.sendMessage({ type: 'reconnect' }); };
$('color').oninput = saveStyle;
$('width').oninput = saveStyle;
$('pair').onclick = () => chrome.runtime.sendMessage({ type: 'pair', code: $('code').value.trim() });
chrome.storage.onChanged.addListener((changes) => { if (changes.status) $('status').textContent = LABELS[changes.status.newValue] || changes.status.newValue; });
load();
