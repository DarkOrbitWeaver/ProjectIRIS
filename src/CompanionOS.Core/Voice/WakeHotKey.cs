using System.Runtime.InteropServices;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core.Voice;

/// <summary>
/// The system-wide invoke switch, Siri-style: one press anywhere in Windows, however
/// badly her ears would have understood the sentence, reliably opens her to listen —
/// and a second press closes them again. This exists because the microphone leg
/// (a wireless headset RMS floor) is the least honest part of the voice loop; her name
/// passing that leg is exactly the miss this switch does not depend on.
///
/// It registers the combo through RegisterHotKey with no window at all: the operating
/// system then delivers WM_HOTKEY straight to the owning thread's queue, which keeps
/// the whole switch isolated on one tiny background pump thread. It steals the key
/// system-wide while she runs and returns it the moment her process exits. The combo
/// comes from settings, so a key that collides with the user's own tools can be
/// changed without a rebuild.
/// </summary>
public sealed class WakeHotKey : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const int WmAppQuit = 0x8001;               // WM_APP range, our private quit
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessageW(out Message message, nint window, uint min, uint max);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PostThreadMessageW(uint threadId, int message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nint First;
        public nint Second;
        public uint Time;
        public int CursorX;
        public int CursorY;
    }

    /// <summary>Registration id used for both RegisterHotKey and its WM_HOTKEY dispatch.</summary>
    private const int HotKeyId = 1;

    private readonly Action _onPressed;
    private readonly string _combo;
    private readonly ManualResetEventSlim _pumpReady = new(false);

    private Thread? _pump;
    private uint _pumpThreadId;
    private uint _modifiers;
    private uint _key;
    private bool _registered;
    private volatile bool _isDisposed;

    private WakeHotKey(string combo, Action onPressed)
    {
        _combo = combo;
        _onPressed = onPressed;
    }

    /// <summary>The combo as the user configured it, e.g. "Ctrl+K".</summary>
    public string Combo => _combo;

    /// <summary>True once the operating system accepted the combo.</summary>
    public bool IsRegistered
    {
        get { _pumpReady.Wait(TimeSpan.FromSeconds(2)); return _registered; }
    }

    /// <summary>
    /// Creates her invoke switch from settings. Returns null when the combo is empty or
    /// unparseable; registration failures (the combo already owned by another tool) log
    /// honestly and leave her running without it.
    /// </summary>
    public static WakeHotKey? Create(VoiceSettings settings, Action onPressed)
    {
        if (string.IsNullOrWhiteSpace(settings.WakeHotkey))
        {
            return null;
        }

        WakeHotKey hotKey = new(settings.WakeHotkey.Trim(), onPressed);
        hotKey.Start();
        return hotKey;
    }

    private void Start()
    {
        if (!TryParseCombo(_combo, out _modifiers, out _key))
        {
            CompanionLog.Warning("Voice",
                $"The wake hotkey '{_combo}' is not a supported combo; set Voice:WakeHotkey to " +
                "Ctrl+K, Ctrl+Alt+something, an F-key or Space. Her name still wakes her.");
            return;
        }

        _pump = new Thread(Pump)
        {
            IsBackground = true,
            Name = "iris wake hotkey",
            Priority = ThreadPriority.Lowest
        };
        _pump.Start();
    }

    /// <summary>
    /// Registers every message-driven step on the thread that will pump it: the
    /// RegisterHotKey ownership, the quit handling and the hotkey dispatch itself all
    /// want the same thread, so this one loop is where all of them live.
    /// </summary>
    private void Pump()
    {
        try
        {
            _pumpThreadId = GetCurrentThreadId();
            _registered = RegisterHotKey(nint.Zero, HotKeyId, _modifiers, _key);
            if (!_registered)
            {
                int error = Marshal.GetLastWin32Error();
                CompanionLog.Warning("Voice",
                    $"The wake hotkey '{_combo}' was not registered (Win32 error {error}, " +
                    "1409 means another program already owns it), so the switch is off. Her wake phrase " +
                    "still works; set Voice:WakeHotkey in her settings to a free combo.");
            }
        }
        finally
        {
            _pumpReady.Set();
        }

        if (!_registered)
        {
            return;
        }

        CompanionLog.Info("Voice", $"The invoke switch is on: {_combo} anywhere in Windows opens her ears.");

        while (GetMessageW(out Message message, nint.Zero, 0, 0) > 0)
        {
            if (message.Id == WmHotKey && message.First == HotKeyId)
            {
                // The pump thread must never block on her wake-up path; the handler
                // owns everything from here.
                Task.Run(_onPressed);
                continue;
            }

            if (message.Id == WmAppQuit)
            {
                UnregisterHotKey(nint.Zero, HotKeyId);
                _registered = false;
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        // Posting the quit message is thread-safe; the unregistration itself happens on
        // the pump thread, which owns the hotkey. Never join a thread whose id is unset.
        if (_pumpThreadId != 0)
        {
            PostThreadMessageW(_pumpThreadId, WmAppQuit, nint.Zero, nint.Zero);
            _pump?.Join(TimeSpan.FromSeconds(2));
        }

        _pumpReady.Dispose();
    }

    /// <summary>
    /// Parses combos like "Ctrl+K", "Ctrl+Alt+F5", "Ctrl+Space": modifier tokens, then
    /// one final key. Letters and digits use their virtual-key codes; F1-F12 and a few
    /// named keys are accepted. False means "this string is not a switch" and the
    /// switch simply does not register.
    /// </summary>
    private static bool TryParseCombo(string combo, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        string[] tokens = combo.Split('+', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        for (int index = 0; index < tokens.Length - 1; index++)
        {
            switch (tokens[index].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "windows":
                case "meta":
                    modifiers |= ModWin;
                    break;
                default:
                    return false;
            }
        }

        if (modifiers == 0)
        {
            // A bare key would swallow ordinary typing everywhere; an invoke switch is
            // always a chord.
            return false;
        }

        string last = tokens[^1];
        if (last.Length == 1)
        {
            key = char.ToUpperInvariant(last[0]);
            return key is >= 0x30 and <= 0x5A; // digits through latin capitals
        }

        if (last.Length is 2 or 3 &&
            (last[0] == 'F' || last[0] == 'f') &&
            int.TryParse(last[1..], out int function) &&
            function is >= 1 and <= 24)
        {
            key = 0x70 + (uint)(function - 1);
            return true;
        }

        key = last.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "enter" or "return" => 0x0D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            _ => 0
        };
        return key != 0;
    }
}
