# Telegram Personal MCP module

This module uses a local MTProto client to send as your personal Telegram account.
It is not a bot and does not use Telegram Login / OAuth.

## Setup

1. In Iris, open **Modules** and choose **Set up Telegram**. The module installs its
   Python dependencies into its own `.venv` folder.
2. In the Telegram card, enter your `api_id` and `api_hash` from
   [my.telegram.org](https://my.telegram.org) and save them. Iris stores these in its
   current-user DPAPI secret store. These are Telegram application credentials, not a
   BotFather bot token.
3. Choose **Connect account**. A local setup page displays a Telegram QR code. Scan
   it with the Telegram app from **Settings → Devices → Link Desktop Device**. If
   Telegram asks for your two-step verification password, enter it only in that
   local setup page.

The QR sign-in session and contact nickname list are DPAPI protected for the current
Windows user. The login code or password is never a model tool argument. The module
binds its setup page to `127.0.0.1` only.

If the account is not connected when Iris starts, Iris shows a dismissible setup
window. Close it to continue; open the Telegram card in **Modules** whenever you want
to connect later.

## Tools

- Add a Telegram user by nickname and current `@username` (or a peer ID already known
  to Telegram). Iris stores the stable peer ID and access hash; it reports failures
  instead of silently substituting a different user.
- List and remove saved nicknames.
- Send a text message or a local media file to a saved nickname. Every send needs a
  fresh Iris approval, then shows a 10-second pending-send window. Say **CANCEL** to
  discard it or **SKIP** to send immediately.

Media paths must refer to an existing local file no larger than 50 MiB. The module
does not crawl folders or attach files unless the path is supplied for that send.

## Runtime

- Windows 10 or later
- Python 3.12 and network access for the one-time dependency install and Telegram
  connection
- Iris MCP transport

Telegram's API credentials belong to your Telegram application. Do not share your
`api_hash`, login QR code, verification code, two-step verification password, or
session file with anyone.
