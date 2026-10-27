"""Telegram Personal MCP server for Iris. Windows-only DPAPI session storage."""
from __future__ import annotations

import asyncio
import base64
import ctypes
import html
import json
import os
import sys
import time
import urllib.parse
import webbrowser
from ctypes import wintypes
from pathlib import Path
from typing import Any

from telethon import TelegramClient
from telethon.errors import SessionPasswordNeededError, UsernameNotOccupiedError, UsernameInvalidError
from telethon.sessions import StringSession

APP_HOME = Path(os.environ.get("COMPANIONOS_HOME") or (Path(os.environ.get("LOCALAPPDATA", Path.home())) / "CompanionOS"))
DATA_DIR = APP_HOME / "telegram"
SESSION_FILE = DATA_DIR / "session.dpapi"
CONTACTS_FILE = DATA_DIR / "contacts.dpapi"
API_ID = os.environ.get("IRIS_SECRET_API_ID", "").strip()
API_HASH = os.environ.get("IRIS_SECRET_API_HASH", "").strip()
PORT = int(os.environ.get("IRIS_TELEGRAM_SETUP_PORT", "47832"))
MAX_MEDIA_BYTES = 50 * 1024 * 1024
SEND_DELAY = 10


class DATA_BLOB(ctypes.Structure):
    _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_byte))]


def _dpapi(data: bytes, decrypt: bool = False) -> bytes:
    if os.name != "nt":
        raise RuntimeError("Telegram session protection requires Windows DPAPI.")
    source = ctypes.create_string_buffer(data)
    blob = DATA_BLOB(len(data), ctypes.cast(source, ctypes.POINTER(ctypes.c_byte)))
    result = DATA_BLOB()
    crypt = ctypes.windll.crypt32.CryptUnprotectData if decrypt else ctypes.windll.crypt32.CryptProtectData
    if decrypt:
        ok = crypt(ctypes.byref(blob), None, None, None, None, 0x1, ctypes.byref(result))
    else:
        ok = crypt(ctypes.byref(blob), "Iris Telegram module", None, None, None, 0x1, ctypes.byref(result))
    if not ok:
        raise ctypes.WinError()
    try:
        return ctypes.string_at(result.pbData, result.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(result.pbData)


def load_protected(path: Path, default: Any) -> Any:
    try:
        raw = _dpapi(path.read_bytes(), decrypt=True)
        return json.loads(raw.decode("utf-8"))
    except FileNotFoundError:
        return default
    except Exception as exc:
        raise RuntimeError(f"Could not decrypt {path.name}; it may belong to another Windows account: {exc}") from exc


def save_protected(path: Path, value: Any) -> None:
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    encrypted = _dpapi(json.dumps(value, ensure_ascii=False).encode("utf-8"))
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_bytes(encrypted)
    os.replace(temporary, path)


def mcp_tool(name: str, description: str, schema: dict[str, Any]) -> dict[str, Any]:
    return {"name": name, "description": description, "inputSchema": {"type": "object", **schema}}


TOOLS = [
    mcp_tool("telegram_status", "Check personal Telegram connection and pending send status.", {"properties": {}, "additionalProperties": False}),
    mcp_tool("telegram_list_contacts", "List saved Telegram contacts by nickname.", {"properties": {}, "additionalProperties": False}),
    mcp_tool("telegram_add_contact", "Save a person by nickname and current Telegram @username.", {"properties": {"nickname": {"type": "string"}, "username": {"type": "string"}}, "required": ["nickname", "username"], "additionalProperties": False}),
    mcp_tool("telegram_remove_contact", "Remove a saved Telegram nickname.", {"properties": {"nickname": {"type": "string"}}, "required": ["nickname"], "additionalProperties": False}),
    mcp_tool("telegram_send_message", "Prepare a reviewed Telegram text message. Iris approval is required; after approval it waits 10 seconds. CANCEL discards it; SKIP sends immediately.", {"properties": {"nickname": {"type": "string"}, "message": {"type": "string"}}, "required": ["nickname", "message"], "additionalProperties": False}),
    mcp_tool("telegram_send_media", "Prepare a reviewed Telegram local media file with an optional caption. Iris approval is required; after approval it waits 10 seconds. CANCEL discards it; SKIP sends immediately.", {"properties": {"nickname": {"type": "string"}, "file_path": {"type": "string"}, "caption": {"type": "string"}}, "required": ["nickname", "file_path"], "additionalProperties": False}),
    mcp_tool("telegram_cancel_send", "Cancel the pending Telegram send before delivery.", {"properties": {"pending_id": {"type": "string"}}, "required": ["pending_id"], "additionalProperties": False}),
    mcp_tool("telegram_skip_send", "Skip the remaining timer and send the reviewed Telegram message now.", {"properties": {"pending_id": {"type": "string"}}, "required": ["pending_id"], "additionalProperties": False}),
    mcp_tool("telegram_pending_send", "Show the current Telegram send preview and remaining timer.", {"properties": {}, "additionalProperties": False}),
]


class TelegramModule:
    def __init__(self) -> None:
        self.client: TelegramClient | None = None
        self.setup_client: TelegramClient | None = None
        self.qr = None
        self.setup_task: asyncio.Task | None = None
        self.setup_state = "not_started"
        self.setup_error = ""
        self.contacts: dict[str, dict[str, Any]] = {}
        self.pending: dict[str, Any] | None = None
        self.pending_task: asyncio.Task | None = None
        self.last_send_result = ""
        self.http_server: asyncio.AbstractServer | None = None
        self.web_token = base64.urlsafe_b64encode(os.urandom(24)).decode().rstrip("=")
        self.lock = asyncio.Lock()

    def _require_credentials(self) -> tuple[int, str]:
        if not API_ID or not API_HASH:
            raise RuntimeError("Set api_id and api_hash in Iris Modules, then refresh Telegram Personal.")
        try:
            return int(API_ID), API_HASH
        except ValueError as exc:
            raise RuntimeError("api_id must be the numeric value from my.telegram.org.") from exc

    async def connect(self) -> TelegramClient:
        if self.client and self.client.is_connected() and await self.client.is_user_authorized():
            return self.client
        api_id, api_hash = self._require_credentials()
        saved = load_protected(SESSION_FILE, "")
        if not saved:
            raise RuntimeError("Telegram account is not connected. Open Modules → Telegram Personal → Connect account.")
        client = TelegramClient(StringSession(saved), api_id, api_hash)
        await client.connect()
        if not await client.is_user_authorized():
            await client.disconnect()
            SESSION_FILE.unlink(missing_ok=True)
            raise RuntimeError("Saved Telegram session expired or was revoked. Connect the account again from Modules.")
        self.client = client
        return client

    async def start_http(self) -> None:
        if self.http_server is None:
            self.http_server = await asyncio.start_server(self._http_client, "127.0.0.1", PORT)

    async def _http_client(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        try:
            header = await asyncio.wait_for(reader.readuntil(b"\r\n\r\n"), 3)
            lines = header.decode("latin-1").split("\r\n")
            method, target, _ = lines[0].split(" ", 2)
            headers = {line.split(":", 1)[0].lower(): line.split(":", 1)[1].strip() for line in lines[1:] if ":" in line}
            length = min(int(headers.get("content-length", "0")), 8192)
            body = await reader.readexactly(length) if length else b""
            route = urllib.parse.urlsplit(target).path
            if method == "GET" and route == "/":
                status, content_type, payload = 200, "text/html; charset=utf-8", self._setup_html().encode()
            elif method == "GET" and route == "/api/status":
                status, content_type, payload = 200, "application/json", json.dumps(self._setup_status()).encode()
            elif method == "GET" and route == "/qr.svg":
                if not self.qr:
                    status, content_type, payload = 404, "text/plain", b"No QR login has been started."
                else:
                    import qrcode
                    from qrcode.image.svg import SvgPathImage
                    image = qrcode.make(self.qr.url, image_factory=SvgPathImage)
                    output = __import__("io").BytesIO(); image.save(output)
                    status, content_type, payload = 200, "image/svg+xml", output.getvalue()
            elif method == "POST" and route in ("/api/start", "/api/password"):
                origin = headers.get("origin", "")
                if headers.get("x-iris-token") != self.web_token or origin != f"http://127.0.0.1:{PORT}":
                    status, content_type, payload = 403, "application/json", b'{"error":"Local setup token or origin did not match. Reload the Iris setup page."}'
                else:
                    try:
                        data = json.loads(body or b"{}")
                        if route.endswith("start"):
                            await self.begin_login()
                        else:
                            await self.submit_password(str(data.get("password", "")))
                        status, content_type, payload = 200, "application/json", json.dumps(self._setup_status()).encode()
                    except Exception as exc:
                        status, content_type, payload = 400, "application/json", json.dumps({"error": str(exc)}).encode()
            else:
                status, content_type, payload = 404, "text/plain", b"Not found"
            reason = "OK" if status == 200 else "Error"
            writer.write(f"HTTP/1.1 {status} {reason}\r\nContent-Type: {content_type}\r\nContent-Length: {len(payload)}\r\nCache-Control: no-store\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'self'; style-src 'unsafe-inline'; img-src 'self' data:; script-src 'unsafe-inline'\r\n\r\n".encode() + payload)
            await writer.drain()
        except Exception:
            pass
        finally:
            writer.close()
            try: await writer.wait_closed()
            except Exception: pass

    def _setup_status(self) -> dict[str, Any]:
        connected = bool(self.client and self.client.is_connected())
        return {"state": self.setup_state, "error": self.setup_error, "qr": bool(self.qr), "connected": connected, "token": self.web_token}

    def _setup_html(self) -> str:
        token = html.escape(self.web_token)
        return f'''<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Iris · Telegram setup</title>
<main><h1>Connect Telegram to Iris</h1><p>This page only listens on this computer. Scan the QR in Telegram → Settings → Devices → Link Desktop Device.</p>
<button id="start">Show login QR</button><div id="qr"></div><div id="password" hidden><p>Telegram requires your two-step verification password.</p><input id="pw" type="password" autocomplete="current-password"><button id="submit">Continue</button></div><p id="state">Checking setup…</p></main>
<style>body{{font:16px system-ui;background:#11131a;color:#eee;max-width:620px;margin:10vh auto;padding:24px}}button,input{{font:inherit;padding:10px;margin:8px 4px}}button{{background:#7957ff;color:white;border:0;border-radius:7px}}#qr img{{width:260px;background:white;padding:10px;margin-top:12px}}p{{line-height:1.5}}</style>
<script>const token='{token}', state=document.querySelector('#state'); async function call(path,data={{}}){{let r=await fetch(path,{{method:'POST',headers:{{'Content-Type':'application/json','X-Iris-Token':token}},body:JSON.stringify(data)}});let j=await r.json();if(!r.ok)throw Error(j.error||'Setup failed');return j}} async function poll(){{try{{let r=await fetch('/api/status'),j=await r.json();state.textContent=j.error||({{not_started:'Ready to connect.',waiting_qr:'Scan the QR code with Telegram.',waiting_password:'Enter your Telegram two-step password here.',connected:'Telegram is connected to Iris.',failed:'Setup failed.'}}[j.state]||j.state);document.querySelector('#password').hidden=j.state!=='waiting_password';document.querySelector('#qr').innerHTML=j.qr&&j.state==='waiting_qr'?'<img src="/qr.svg">':''}}catch(e){{state.textContent=e}}}} document.querySelector('#start').onclick=async()=>{{try{{await call('/api/start');poll()}}catch(e){{state.textContent=e.message}}}};document.querySelector('#submit').onclick=async()=>{{try{{await call('/api/password',{{password:document.querySelector('#pw').value}});document.querySelector('#pw').value='';poll()}}catch(e){{state.textContent=e.message}}}};poll();setInterval(poll,2000);</script>'''

    async def begin_login(self) -> None:
        api_id, api_hash = self._require_credentials()
        if self.setup_task and not self.setup_task.done():
            return
        self.setup_state, self.setup_error = "waiting_qr", ""
        self.setup_client = TelegramClient(StringSession(), api_id, api_hash)
        await self.setup_client.connect()
        self.qr = await self.setup_client.qr_login()
        self.setup_task = asyncio.create_task(self._await_qr())

    async def _await_qr(self) -> None:
        try:
            await self.qr.wait()
            self._store_session()
        except SessionPasswordNeededError:
            self.setup_state = "waiting_password"
        except Exception as exc:
            self.setup_state, self.setup_error = "failed", str(exc)

    async def submit_password(self, password: str) -> None:
        if self.setup_state != "waiting_password" or not password:
            raise RuntimeError("There is no pending two-step password request.")
        try:
            await self.setup_client.sign_in(password=password)
            self._store_session()
        except Exception as exc:
            self.setup_error = "Telegram did not accept that password. Check it and try again."
            raise RuntimeError(self.setup_error) from exc

    def _store_session(self) -> None:
        save_protected(SESSION_FILE, self.setup_client.session.save())
        self.setup_state, self.setup_error = "connected", ""
        self.qr = None
        asyncio.create_task(self.setup_client.disconnect())

    async def handle_tool(self, name: str, args: dict[str, Any]) -> str:
        if name == "telegram_status":
            try:
                client = await self.connect()
                me = await client.get_me()
                suffix = f" Last send: {self.last_send_result}" if self.last_send_result else ""
                return f"Connected as {getattr(me, 'first_name', '')} (@{getattr(me, 'username', None) or 'no username'}). Setup page: http://127.0.0.1:{PORT}/{suffix}"
            except Exception as exc:
                return f"Not connected: {exc} Setup page: http://127.0.0.1:{PORT}/"
        if name == "telegram_pending_send":
            if not self.pending: return self.last_send_result or "No Telegram send is pending."
            remain = max(0, int(self.pending['deadline'] - time.time()))
            return f"Pending {self.pending['id']} to {self.pending['nickname']}: {self.pending['preview']!r}. {remain}s remain. Say CANCEL or SKIP."
        if name == "telegram_cancel_send":
            await self._match_pending(args["pending_id"])
            async with self.lock:
                if self.pending_task: self.pending_task.cancel()
                self.pending = self.pending_task = None
                self.last_send_result = "Canceled. Nothing was sent."
            return "Canceled. Nothing was sent."
        if name == "telegram_skip_send":
            pending = await self._match_pending(args["pending_id"])
            async with self.lock:
                if self.pending_task: self.pending_task.cancel()
                self.pending = self.pending_task = None
            await self._deliver(pending)
            self.last_send_result = f"Sent to {pending['nickname']} immediately after SKIP."
            return f"Sent immediately to {pending['nickname']}."
        if name == "telegram_list_contacts":
            self.contacts = load_protected(CONTACTS_FILE, {})
            if not self.contacts: return "No contacts saved. Add one using their current @username and a nickname."
            return "Saved contacts: " + "; ".join(f"{k}: @{v.get('username','')} (Telegram ID {v['id']})" for k,v in sorted(self.contacts.items()))
        if name in ("telegram_add_contact", "telegram_remove_contact"):
            client = await self.connect()
            self.contacts = load_protected(CONTACTS_FILE, {})
            nickname = str(args["nickname"]).strip()
            key = nickname.casefold()
            if not nickname or len(nickname) > 48: raise RuntimeError("Nickname must be 1–48 characters.")
            if name == "telegram_remove_contact":
                if key not in self.contacts: raise RuntimeError(f"No saved contact named '{nickname}'. Nothing changed.")
                del self.contacts[key]; save_protected(CONTACTS_FILE, self.contacts)
                return f"Removed contact '{nickname}'."
            if key in self.contacts:
                raise RuntimeError(f"Nickname '{nickname}' is already saved. Remove it first if you want to replace its Telegram account.")
            username = str(args["username"]).strip().lstrip("@").strip()
            try: entity = await client.get_entity(username)
            except (UsernameNotOccupiedError, UsernameInvalidError, ValueError) as exc:
                raise RuntimeError(f"Could not find @{username}. Check the current username in Telegram; contact was not saved.") from exc
            if not getattr(entity, "id", None) or not getattr(entity, "access_hash", None):
                raise RuntimeError("Telegram did not return a stable user ID/access hash. Contact was not saved.")
            self.contacts[key] = {"nickname": nickname, "id": entity.id, "access_hash": entity.access_hash, "username": getattr(entity, "username", username), "name": getattr(entity, "first_name", "")}
            save_protected(CONTACTS_FILE, self.contacts)
            return f"Saved '{nickname}' as Telegram user ID {entity.id} (currently @{self.contacts[key]['username']}). Sends will use that saved account ID even if their username changes."
        if name in ("telegram_send_message", "telegram_send_media"):
            self.contacts = load_protected(CONTACTS_FILE, {})
            nickname = str(args["nickname"]).strip()
            contact = self.contacts.get(nickname.casefold())
            if not contact: raise RuntimeError(f"No saved Telegram contact '{nickname}'. Save the contact first; no message was sent.")
            client = await self.connect()
            from telethon.tl.types import InputPeerUser
            peer = InputPeerUser(int(contact["id"]), int(contact["access_hash"]))
            try: entity = await client.get_entity(peer)
            except Exception as exc: raise RuntimeError(f"Could not resolve saved contact '{nickname}' (ID {contact['id']}). Ask them to message you, or remove this nickname and add their current @username again. Nothing was sent.") from exc
            if name == "telegram_send_message":
                message = str(args["message"])
                if not message.strip() or len(message) > 4096: raise RuntimeError("Message must contain 1–4096 characters.")
                item = {"kind":"text", "payload":message, "caption":""}
                preview = message
            else:
                path = Path(str(args["file_path"])).expanduser()
                if not path.is_absolute(): raise RuntimeError("Media file_path must be an absolute local path.")
                if not path.is_file(): raise RuntimeError(f"Media file does not exist: {path}")
                if path.stat().st_size > MAX_MEDIA_BYTES: raise RuntimeError("Media file exceeds the 50 MiB limit.")
                caption = str(args.get("caption", ""))
                item = {"kind":"media", "payload":str(path), "caption":caption}
                preview = f"media file {path.name}" + (f" with caption {caption!r}" if caption else "")
            async with self.lock:
                if self.pending: raise RuntimeError(f"A send is already pending for {self.pending['nickname']} (ID {self.pending['id']}). Cancel or skip it first.")
                pending = {"id":base64.urlsafe_b64encode(os.urandom(6)).decode().rstrip("="), "nickname":contact["nickname"], "peer":{"id":contact["id"],"access_hash":contact["access_hash"]}, **item, "preview":preview, "deadline":time.time()+SEND_DELAY}
                self.pending = pending
                self.pending_task = asyncio.create_task(self._timer_send(pending["id"]))
            return f"REVIEW: Send to {contact['nickname']} (Telegram ID {contact['id']})? {preview!r}. A 10-second send timer is running. Say CANCEL (pending_id {pending['id']}) to stop or SKIP (pending_id {pending['id']}) to send now."
        raise RuntimeError(f"Unknown tool: {name}")

    async def _match_pending(self, pending_id: str) -> dict[str, Any]:
        if not self.pending or self.pending["id"] != pending_id:
            raise RuntimeError("That send is no longer pending or the pending ID did not match. Nothing was sent.")
        return self.pending

    async def _timer_send(self, pending_id: str) -> None:
        try:
            await asyncio.sleep(SEND_DELAY)
            async with self.lock:
                if not self.pending or self.pending["id"] != pending_id: return
                pending, self.pending = self.pending, None
                self.pending_task = None
            try:
                await self._deliver(pending)
                self.last_send_result = f"Sent to {pending['nickname']} after the 10-second review timer."
            except Exception as exc:
                self.last_send_result = f"Send to {pending['nickname']} failed; Telegram may not have received it. Check the chat before retrying. Error: {exc}"
        except asyncio.CancelledError:
            return

    async def _deliver(self, pending: dict[str, Any]) -> None:
        client = await self.connect()
        from telethon.tl.types import InputPeerUser
        peer = InputPeerUser(int(pending["peer"]["id"]), int(pending["peer"]["access_hash"]))
        if pending["kind"] == "text": await client.send_message(peer, pending["payload"])
        else:
            path = Path(pending["payload"])
            if not path.is_file(): raise RuntimeError(f"Canceled delivery: media file no longer exists: {path}")
            if path.stat().st_size > MAX_MEDIA_BYTES: raise RuntimeError("Canceled delivery: media file grew beyond 50 MiB.")
            await client.send_file(peer, str(path), caption=pending["caption"] or None)


async def run() -> None:
    module = TelegramModule()
    try:
        await module.start_http()
    except OSError as exc:
        module.setup_error = f"Local setup page could not start on 127.0.0.1:{PORT}: {exc}"
        module.setup_state = "failed"
    async def reply(message: dict[str, Any]) -> None:
        response: dict[str, Any] = {"jsonrpc": "2.0", "id": message.get("id")}
        method = message.get("method")
        try:
            if method == "initialize":
                response["result"] = {"protocolVersion": message.get("params", {}).get("protocolVersion", "2024-11-05"), "capabilities": {"tools": {}}, "serverInfo": {"name": "telegram-personal", "version": "0.1.0"}}
            elif method == "ping": response["result"] = {}
            elif method == "tools/list": response["result"] = {"tools": TOOLS}
            elif method == "tools/call":
                params = message.get("params", {})
                try:
                    text = await module.handle_tool(str(params.get("name", "")), params.get("arguments") or {})
                    response["result"] = {"content": [{"type": "text", "text": text}], "isError": False}
                except Exception as exc:
                    response["result"] = {"content": [{"type": "text", "text": str(exc)}], "isError": True}
            else:
                response["error"] = {"code": -32601, "message": f"Method not found: {method}"}
            if "id" in message:
                sys.stdout.write(json.dumps(response, ensure_ascii=False, separators=(",", ":")) + "\n")
                sys.stdout.flush()
        except Exception as exc:
            if "id" in message:
                response["error"] = {"code": -32603, "message": str(exc)}
                sys.stdout.write(json.dumps(response, ensure_ascii=False, separators=(",", ":")) + "\n")
                sys.stdout.flush()
    try:
        while True:
            line = await asyncio.to_thread(sys.stdin.readline)
            if not line:
                break
            if not line.strip(): continue
            try: message = json.loads(line)
            except json.JSONDecodeError: continue
            await reply(message)
    finally:
        if module.pending_task: module.pending_task.cancel()
        if module.client: await module.client.disconnect()
        if module.setup_client: await module.setup_client.disconnect()
        if module.http_server: module.http_server.close(); await module.http_server.wait_closed()


if __name__ == "__main__":
    asyncio.run(run())
