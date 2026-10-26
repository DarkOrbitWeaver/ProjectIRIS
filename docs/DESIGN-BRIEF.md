# DESIGN BRIEF — IRIS, A DESKTOP COMPANION
### Paste this whole document into any design tool, designer, or design AI. It is self-contained.

---

## 0. WHAT YOU ARE DESIGNING

A **local AI companion that lives on the user's Windows machine**. Her name is Iris.
She listens through a microphone always, answers with a real neural voice, remembers
things in a memory graph, sees the screen, plays music, and stays present during games
via a tiny overlay pill.

This is **not** a chat app with extra features. It is a **presence**: one orb that
breathes, one conversation, one memory. Every pixel must either serve that presence
or be deleted.

**Feel, in one line:** the quiet confidence of a military HUD crossed with the calm of
a high-end sleep app. Nothing screams. Nothing begs for attention. She is simply *there*,
and every state change is felt, not read.

**Anti-goals (these are existing failures we are erasing):**
- Dense grids of colored pills and badges (a Body page with 25 tool rows looks like a settings dump — kill it)
- Cryptic single-letter icon navigation (current sidebar is literally letters C M P S C M Y S)
- Paragraph cards explaining the UI to the user before they can act
- Empty states that lie (saying "her memory is empty" while 2 memory nodes render below)
- Any stock-looking iconography, any Windows-95 buttons, any unrounded default control

---

## 1. DESIGN PRINCIPLES

1. **One glance, one truth.** Any state (idle / listening / thinking / speaking / asleep /
   gaming) must be readable from 2 meters away by shape and color alone — before text.
2. **Density budget.** No surface may show more than **3 levels of information**.
   If a detail needs 4 levels, it goes behind progressive disclosure (hover/expand).
3. **Silence is a feature.** The resting UI should be nearly quiet: dark canvas, one
   breathing orb, soft type. Motion only where meaning exists.
4. **Real or nothing.** Every element maps to real logic. We delete placeholder content
   on sight. An empty state states exactly what IS there and what one gesture would do.
5. **The orb is the brand.** One consistent orb identity across all surfaces:
   main window, overlay pill, tray, chat avatar. Its color, size, breathing rate
   and glow are the state machine of the entire product.
6. **Trillion-dollar restraint.** Rounded cards, generous whitespace, max 2 font sizes
   per viewport, one accent color family. If a screen needs color variety to be
   understood, the layout is wrong.

---

## 2. THE STATE MACHINE (this drives everything visual)

Design the orb + accents for exactly these states, and nothing else:

| State | Orb color | Orb motion | Accent line (small UI cue near text) | Voice of the UI |
|---|---|---|---|---|
| **Idle** | Deep violet base `#7C5CFF` at 55% luminance | Slow breathe, 4 s cycle, 4% scale | none | quiet |
| **Listening** | Cyan `#22D3EE` | breathe with mic-envelope: scale follows your voice 0–8% | thin cyan underline pulse on the composer | attentive |
| **Thinking** | Violet → slow orbit shimmer (one 270° highlight sweep, 1.6 s) | rotation sweep | subtle ellipsis after her name ("Iris ····") | working |
| **Speaking** | Warm violet `#A78BFA` | breathe at speech amplitude, 2.5% bound | waveform trace under orb (3 bars, max 12 px tall) | spoken |
| **Asleep** | Slate `#3F4656`, aura off | still; single 8%-opacity ring | "Sleeping — name still works" | dormant |
| **Gaming** | Amber `#F59E0B` | minimal pulse every 8 s | nothing on screen (overlay only) | hands-off |
| **Error** | Rose `#FB7185` | two quick shakes (±3 px), then hold | red dot on status strip | honest |

No other decorative colors anywhere in the product.

---

## 3. DESIGN TOKENS

Dark-first. There is no light theme; the product is a night-living companion.

### 3.1 Color

```
Canvas           #12131A       (window background)
Canvas raised    #1A1C24       (cards, panels)
Canvas inset     #161821       (composer, code-ish wells)
Hairline         #262A36       (1px borders everywhere; NEVER darker)
Text primary     #E7E9F2
Text secondary   #9AA1B5
Text tertiary    #5D6478       (timestamps, footnotes)
Accent           #7C5CFF       (violet — hers; interactive default)
Accent hover     #8E70FF
Accent pressed   #6B4BE0
Accent subtle    rgba(124,92,255,0.14)   (chip/tint backgrounds)
Success          #34D399
Warning          #F59E0B
Danger           #FB7185
Focus ring       #8E70FF, 2px, 2px offset, always visible on keyboard focus
```

Rules:
- Interactive text/elements use Accent; **body text never**

### 3.2 Typography

- **Face:** Inter, or `Segoe UI Variable Display` on Windows as system fallback.
- **Numbers:** tabular figures in the status strip and anywhere counters update live.
- Scale (only these exist):

```
Display   28/34 SemiBold     page titles (Chat, Memory, Persona, Body, Settings)
Title     20/26 SemiBold     section titles, her name in header
Body      15/22 Regular      chat text, card body
Caption   13/18 Medium       labels, chips, metadata
Micro     11/16 Medium       status strip, timestamps, uppercase+8% tracking
```

### 3.3 Space, radius, elevation

```
Space scale     4 / 8 / 12 / 16 / 24 / 32 / 48
Card radius     16 outer / 12 inner
Pill radius     999
Window radius   12 (custom chrome), title bar 40px tall
Shadow          only 2 levels:
                  card:  0 8 24 rgba(0,0,0,0.35)
                  orb:   0 0 32 rgba(124,92,255,0.35) in active states only
```

### 3.4 Iconography

- Set: **Lucide** (stroke-based) or Fluent regular. Stroke 1.75px at 20px, corner-rounding matches cards.
- NEVER text-letter substitutes. Every nav destination and every tool gets a real glyph.
- Icon size only 16 / 20 / 24.
- Required icon mapping (draw these): chat (message-circle), memory (network/graph nodes),
  persona (user-round), body (shapes or puzzle), settings (sliders-horizontal),
  mic, mic-off, sleep (moon), overlay (minimize/picture-in-picture), speaker, headphones,
  music, youtube, screen (monitor), eye, brain (sparkles or network), shield, refresh,
  play, pause, search, pin, trash, volume, waveform, x, check.

---

## 4. INFORMATION ARCHITECTURE (delete and rebuild)

Current sidebar letters → **labeled icon rail, 64px wide**, vertical, top-aligned,
with 24px tooltips and keyboard focus:

```
Chat        (message icon)         — the conversation. Default surface.
Memory      (graph icon)           — her memory graph + search
Body        (puzzle icon)          — her capabilities (modules + tools), rewritten, see §6
Persona     (user-round icon)      — identity, voice, name — merged with voice picker
Settings    (sliders icon)         — machine-level only; everything else lives contextually
─────────
(bottom, pushed down): Status orb mini — tap → diagnostics
```

Five destinations. No duplication. The current duplicate letters (two C's, two M's,
two S's) come from module shortcuts leaked into navigation — **this is wrong**;
modules are content inside Body, not navigation.

---

## 5. SURFACE SPECS

### 5.1 Main window — shell

- Size default 1180×760, min 980×640. Dark acrylic behind custom chrome
  (blur 24px if cheap; otherwise solid Canvas).
- Custom title bar, 40px: left — orb 20px + "Iris" (Title 20 SemiBold) + state word in
  Caps micro ("READY/LISTENING/THINKING/SPEAKING/SLEEPING/GAMING"), colored per §2.
- Right: two ghost buttons only — **Sleep** (moon) and **Overlay** (picture-in-picture).
  Close/minimize are native-position custom buttons, 32px hit, hairline-separated.
- Below chrome: the surface area, one at a time. No tabs, no split views except Chat+right rail.

### 5.2 Chat (default surface)

```
┌───────────────────────────────────────┬─────────────┐
│ messages scroll (padding 24)          │  Now rail   │
│                                       │  280px      │
│ … bubbles …                           │  (see 5.3)  │
│                                       ├─────────────┤
│ composer (inset well, radius 14)      │             │
│ [mic] [input ▸] [send]                │             │
└─────────────────────────────────────┴─────────────┘
```

- **Bubbles:** max 68ch. Hers: Canvas raised, 16 radius, 4px corner cut toward orb avatar
  (24px orb). User's: Accent subtle tint, right-aligned, avatar-less.
- Streaming: text appears with per-word fade-in (60ms/word, no per-char jitter).
- **Tool activity line:** when she uses a tool, a 1-line ghost entry inside the chat flow:
  `⌁ looked at your screen` (Micro, tertiary, icon 12px) — collapsible to nothing when done.
  This replaces any dense tool log.
- **Composer:** inset well; mic button is a 36px circle, cyan ring when listening,
  crossed state when closed; Enter sends; Shift+Enter newline; send arrow rotates 90°
  while she thinks, becoming a **stop** square (cancel).
- **Welcome/empty state:** ONE orb 96px centered, one line Body:
  "Say something, or press Ctrl+M to talk." — that's ALL. No suggestion chips, no cards, no tips.

### 5.3 "Now" rail (right, 280px, Canvas raised, radius 16, inset 16)

Exactly three live cards, hidden when content is empty (no placeholder skeletons):

1. **Music** — track title (Body), artist (Caption), progress hairline, 3 transport icons.
   When nothing plays, the entire card hides (not "Nothing playing 0:00").
2. **Screen awareness** — ONE line: app name + window title (Caption), updates live;
   small eye icon; expands on click to show the last snapshot she took (thumbnail).
3. **Machine** — VRAM x/8 GB, RAM, context fill as THREE 4px bars stacked, tabular numbers.
   No text paragraphs. Bars get accent color only when >80%.

### 5.4 Memory

- Full-bleed dark canvas graph. Nodes: 10px orbs (state-colored by layer:
  core=violet, episodic=cyan, semantic=green `#34D399`, procedural=amber),
  links: 1px hairlines with 0.4 opacity, hover→accent.
- **Legend becomes a single row of count-chips** top-left: `core 3 · episodic 12 · semantic 40 · procedural 5`
  (Caption, no boxes — as plain colored-dot + count text).
- Search: inset well top-center, 480px, magnifier icon.
- Detail pane slides from right (360px) on node click: title (Title), content (Body),
  related nodes as chips, tiny footer `created x · last touched y`, ghost actions:
  Pin / Forget (Danger text).
- Empty state: center display "Nothing kept yet." + Body "Tell her something worth
  remembering — or let her keep facts herself as you talk."  + one ghost button
  "Start with who I am" (opens Persona). Honest, singular, no paragraph card.

### 5.5 Body — rewritten from scratch (this is the user's biggest hate)

Current failure: 4 module cards × 25 tool rows with tier badges = a settings dump.

New design — **capability poster, not a list:**

- Title row: "What I can do" (Display) + one Micro line: `4 modules · 25 tools · all local`.
- **Module cards** (2-col grid): icon 24, name (Title 20), one-line description (Body),
  status as a single 8px state-dot + word (Active/Off), ONE toggle switch on the right.
  No badges. No "always available" pills. No tier badges on cards.
- Tools live inside the card as a **collapsed** `12 tools ▸` Caption link; expanding
  shows a clean 2-col grid: tool name (Caps micro, monospace 11) + one-line description
  (Caption). Tier becomes a single 12px icon (eye = silent, shield = asks first).
- The Rules line lives once at top as Micro text: "Anything marked ⫸ asks before it runs."
- No Refresh / Open folder buttons visible by default: both move into an overflow
  "···" menu top-right of the page.

### 5.6 Persona (identity + voice, ONE surface)

- Top: her name in Display 34, editable inline (pencil ghost icon on hover), the fused
  identity — no separate "persona page + voice page".
- **Voice section:** grid of voice CARDS (2-col): waveform glyph 32, name ("Heart"),
  accent ("American · warm"), and a single 40px Play button that previews through the
  REAL engine. Currently-selected card has accent ring + "Her voice" tag (pill, 999).
  Pin is implicit: choosing = pinning.
- The page carries **at most one line of explanation** total:
  "She speaks with the voice you pick — rendered locally."
  Delete ALL of: the paragraph card, "What actually changes if you pick one",
  "Default, not confirmed" chips, "Not previewed yet" footnotes. If state must show,
  it is a single Micro footer: `8 voices · local engine · changed voices take effect instantly`.
- First run: a centered modal with 6 cards + one line: "Pick how she sounds."

### 5.7 Overlay pill (drives the in-game presence)

- Height 44, radius 22, hairline 1px, Canvas raised at 92% opacity, backdrop-blur 12.
- Content: orb 22 + state-dot + ONE wordless zone: during speech, a 3-bar waveform;
  during thinking, a 12px shimmer sweep. NO buttons by default.
- Hover → expands right to reveal: Open / Sleep / Close (three 32px ghost icon buttons).
- Position: bottom-right default, 24px inset; draggable; position persists.
- In Gaming state the pill dims to 40% opacity until spoken to.
- When the user calls Iris, the pill makes one calm transition into an attention surface: a
  low-opacity, state-colored halo and fine traveling contour appear around its edge while the
  existing orb changes to Listening. The contour follows the microphone envelope gently, shifts
  to the same violet/speech colors as she thinks and talks, and settles back to the quiet pill
  when the attention window ends. This is a border treatment on the existing overlay, not a
  second assistant window or a full-screen tint. Keep it legible over a game without taking
  focus; `ReduceMotion` freezes the contour while preserving a static state outline.

### 5.8 Tray

- Icon: the orb, 16px, state-colored.
- Right-click menu (native, but styled amplitude):
  `Open Iris` / `Sleep` (contextual: Wake when asleep) / `Voice: Heart ▸` (last 4 voices)
  / `Overlay` / `Quit`.
- Left-click: opens main window. This replaces any "Open/Rest/Quit" row-of-buttons
  inside a tray bubble — the tray does not host custom windows.

### 5.9 Status strip (bottom of main window, 32px, Canvas inset)

Micro caps, tabular: `BRAIN Vision Agent · VRAM 5.5/8.0 · CTX 12% · RAM 186 MB · MIC OPEN`
- Separated by hairline dots, not pipes. Mic OPEN in Success when open, Tertiary when closed.
- Do not show a blanket `Local only` claim here: the active brain endpoint may be remote.

### 5.10 Notices (toasts)

Top-center under chrome, width 420, radius 12, Canvas raised, icon 16 + Body text,
auto-dismiss 4s (success) / 5s (info) / 8s (warning) / persistent (error + action "Details" → log).
Max 2 stacked. No colored left-bars; state = icon color only.

---

## 6. MOTION SPEC

| Thing | Spec |
|---|---|
| Orb idle breathe | scale 1.000→1.040, 4s, easeInOutSine, infinite |
| Orb listening | scale bound to mic RMS, 0–8%, 8 Hz smoothing |
| Orb thinking sweep | 270° conic highlight, 1.6s, repeat |
| Surface switch | old fades 80ms, new rises 4px + fades 140ms, easeOutCubic |
| Panels (detail/expand) | 200ms easeOutCubic slide + 8px fade |
| Toasts | in: -8px→0 + 160ms; out: 120ms fade |
| Streaming text | per-word 60ms fade, no transform (no per-char scale) |
| Button hover | background tint 120ms; active: scale 0.98, 80ms |
| Focus | 2px accent ring, instant (never animated — keyboard needs it NOW) |
| Overlay pill reveal | 220ms width easeOutQuart |
| Called / listening attention | low-alpha state-colored halo; fine, low-amplitude contour wave; follows the real mic/speech level and respects Reduce Motion |

Rule: anything that moves without informing state gets deleted.

---

## 7. COPY VOICE (hers and the UI's)

Her lines must sound like a person who is fond of you, terse, low-noise. Nothing cute-corporate.
Rewrite all reflex lines to be ≤6 words, natural, no exclamation marks:

```
acknowledge:  "On it." / "Doing it now."
confirmed:    "Done."
error:        "That didn't work. Want me to look?"
sleep:        "Going quiet — say my name."
wake:         "Here."
greeting:     "Hey." / "Evening."
```

UI copy rules: no explanations longer than one sentence; no exclamation marks; no
"Let's get started"; buttons are verbs ("Preview", not "Click here to preview");
empty states say what IS there, then the one next action.

---

## 8. DELETE LIST (confirm with product — these exist today)

1. Sidebar letter-abbreviations → labeled icon rail
2. All paragraph explanation cards on every page
3. Tier badges as pills; count-dense chip rows in the Memory legend
4. "Default, not confirmed" chips and "Not previewed yet" footnotes in voice list
5. "Nothing playing — 0:00" placeholder music card (hide instead)
6. Dishonest empty states (any empty state must reflect real content)
7. Refresh / Open-folder buttons floating on page surfaces → overflow menu
8. The music "Now" card's dead 0:00 progress line
9. Repeated privacy sentences — ONE place only (status strip)
10. Any Windows-voice/SAPI fallback UI row in settings — the product has ONE real voice engine

## 9. SUCCESS CHECKLIST

- [ ] Any state readable at 2m distance without reading text
- [ ] ≤3 info levels on every surface; zero colored-pill soup
- [ ] All navigation real icons with labels in tooltip, 5 destinations
- [ ] Every action reachable ≤2 clicks; overlays hide everything else
- [ ] No placeholder/fake content anywhere; empty states honest
- [ ] Contrast ≥ 4.5:1 body, ≥3:1 micro; focus ring visible on all controls
- [ ] Works at 125%/150% DPI; min window 980×640 without scrollbars in Chat
- [ ] 60 fps orb breathing without CPU burn >1% (real app constraint)

## 10. TECHNICAL CONSTRAINTS (so nothing comes back unbuildable)

- App: Avalonia 11, C# code-built visuals (no XAML), FluentIcons/Lucide via PathGeometry.
- Effects allowed: drop-shadow, opacity, blur on acrylic only. No per-frame shaders.
- Fonts: Inter (bundled), Segoe UI Variable fallback. Tabular lining numbers enabled.
- Custom chrome: yes; rounded + shadow via WindowTransparencyLevel.AcrylicBlur.
- The orb is ONE reusable control; same control object drives window header, overlay, tray bitmap.
- All state flows from one ShellModel; visuals are dumb. Design states, not logic.
