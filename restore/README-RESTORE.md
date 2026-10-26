# Fresh-PC restore guide (the "I reset my PC" path)

Everything needed to bring Iris back on a new machine lives in this repository.
The heavy binaries (her brain, her ears, the speech runtimes) are never committed —
GitHub cannot hold them — but every byte is fetched by pinned, hash-verified scripts.

## The 5 steps

```powershell
# 1. Clone
git clone https://github.com/DarkOrbitWeaver/ProjectIRIS.git
cd ProjectIRIS

# 2. Fetch her whole body: brain (Qwen3-VL-4B), ears (Parakeet), llama.cpp,
#    transcribe.cpp, Pocket runtime, and the kokoro venv + ONNX weights.
powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
# (add -Force to re-download anything that is already present)

# 3. Restore her identity and settings (this folder IF the PC is brand new:
#    copy into %LOCALAPPDATA%\CompanionOS\config\)
Copy-Item restore\config\* "$env:LOCALAPPDATA\CompanionOS\config\" -Force

# 4. Optional: restore playlists
Copy-Item restore\workspace\playlists\* "$env:LOCALAPPDATA\CompanionOS\workspace\playlists\" -Force

# 5. Build and run
dotnet build src\CompanionOS.App\CompanionOS.App.csproj
Start-Process src\CompanionOS.App\bin\Debug\net9.0-windows10.0.19041.0\CompanionOS.exe
```

## What lives where on a working PC

| Thing | Location | Recoverable from |
|---|---|---|
| Code + all scripts | this repo | `git clone` |
| Brain / ears / llama.cpp / transcribe.cpp | `%LOCALAPPDATA%\CompanionOS\models`, `runtimes` | `scripts\setup.ps1` (pinned + sha256) |
| Kokoro voice weights + venv | `runtimes\kokoro-tts`, `models\kokoro-cache` | `scripts\setup.ps1` (calls `prepare-kokoro.py`, pinned hashes) |
| Settings, persona, permissions | `%LOCALAPPDATA%\CompanionOS\config` | `restore\config\` copies in this repo |
| Playlists | `%LOCALAPPDATA%\CompanionOS\workspace\playlists` | `restore\workspace\playlists\` |
| Memory graph (her past) | `%LOCALAPPDATA%\CompanionOS\memory` (sqlite) | NOT in this repo — copy that folder **before** a reset |

## Honest limits

- **Memory db is not included** by design (privacy + binary). If you want her life
  backed up, copy `%LOCALAPPDATA%\CompanionOS\memory` somewhere real before resetting.
- **Your music files** are wherever they are on disk; settings.json names those folders
  (`MusicSearchDirectories`). If the new PC uses different drive letters, fix those paths
  in `%LOCALAPPDATA%\CompanionOS\config\settings.json` after step 3.
- The docker of a fresh machine still needs: .NET 9 SDK, a CUDA-capable GPU (or the
  Vulkan fallback the setup script picks automatically), and git.
