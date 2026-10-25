# config

This folder is a placeholder in the repository. Her real configuration is created the
first time she runs, inside her data folder:

| Platform | Default data folder |
|---|---|
| Windows | `%LOCALAPPDATA%\CompanionOS` |
| Any | the folder in the `COMPANIONOS_HOME` environment variable, if set |

Inside that folder you will find:

```
config/
  settings.json          every knob, written atomically with a .bak backup
  persona.json            her name, tone, greetings, and instant phrases
  permissions.json        "always allow" answers, kept apart from settings
  modules.disabled.json   which modules are switched off
models/                   brain.gguf, brain-mmproj.gguf, whisper-base.bin
runtimes/                 llama-server, yt-dlp
memory/brain.db           her long-term memory (SQLite, WAL)
memory/journal.jsonl      append-only record used to survive crashes
memory/audit.jsonl        every action she was allowed to take
workspace/                the only folder she may write to freely
cache/                    rendered reflex phrases
logs/                     companion.log, rotated when it passes 8 MB
```

Everything is plain text or SQLite. Backing her up means copying this one folder.
