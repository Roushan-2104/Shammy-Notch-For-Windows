# Writing a Shammy module

A module is one feature (timers, clipboard, music commands...). Shammy finds every non-abstract class
implementing `IShammyModule` by reflection and starts it once, so **a module never edits shared files**.
The whole API is in [`ModuleApi.cs`](ModuleApi.cs); read it first.

## Rules

- Put the module in `src/Shammy.App/Modules/<Feature>/`, namespace `Shammy.Modules.<Feature>`.
- Pure logic (parsing "remind me at 6pm", unit conversion...) goes in `src/Shammy.Core/<Feature>/`
  (namespace `Shammy.Core.<Feature>`) with xUnit tests in `tests/Shammy.Core.Tests/<Feature>Tests.cs`.
- Only add new files in those places. Do not edit anything else. If the API is missing something,
  work around it inside your module and say so in your report.
- No new NuGet packages. Available: WPF, WinForms (tray only), WinRT (`Windows.*` APIs),
  NAudio 2.2.1, ZXing.Net 0.16.10, System.Management, WebView2. `HttpClient` via `ctx.Http`.
- Everything runs on the UI thread unless you start background work; marshal back with `ctx.Ui`.
- Fail soft: catch exceptions from the network, files and Windows APIs, log with `ctx.Warn`, and keep running.
- No paid APIs, no API keys. Free endpoints only, and the feature must degrade gracefully when offline.

## Extension points

| Want to... | Use |
|---|---|
| React to typed (tap Right Alt) or spoken (hold Right Alt) text | `ctx.AddCommand(ITextCommand)`; `CanHandle` must be cheap and specific so it never steals normal web searches |
| Say what your command will do in the typing palette ("= 42", "Start a 25 min timer") | Also implement `ICommandPreview` on the command; cheap, no side effects, null if unsure |
| A command that must win over broader matchers (the launcher's "open …") | `ctx.AddCommandFirst(ITextCommand)`, only for fixed phrase lists |
| Show something live in the compact notch | `ctx.Post(new Activity("<module>.<id>", ActivityKind.Custom, priority, payload))` where payload implements `ICompactContent`. Keep it for events (a timer running, a call); anything that is just nice to know belongs in a glance, so the resting notch stays the pet's |
| A card in the open notch | `ctx.AddCard(new NotchCard { Id, Title, Width, Compact, Detail, Order, IsRelevant, Chip, Live, Peek })`: `Compact` is the rail tile (a `WallTile`, 168×140), `Detail` builds the 924×248 view it opens into in place (a `WallDetail`; any other view gets a Back + title row unless `OwnsHeader`). `IsRelevant` decides whether it sits on the rail right now; a card that can leave the row needs `Chip = true` (chips that don't fit go under "More") or nothing can open it |
| The card's relevance changed | `ctx.CardsChanged()` (cheap; call it whenever `IsRelevant` may have flipped) |
| Open your card from a command or a pill | `ctx.OpenCard("<card id>")`: opens its detail in place once the command's confirmation has closed, held until the pointer has visited and left (or 8 s) |
| A line in the resting notch's rotating glance and the hover peek | `ctx.AddGlance("<id>", () => GlanceItem?)`: ~18 characters, an accent from `NotchPalette`, a priority, an optional click, and `StartsAt` for events (the pet uses it for "meeting soon"); return null when there's nothing to say. Providers are asked often, so keep them cheap and pick rotating text by the clock |
| A bigger popup (QR code, picker) | `ctx.ShowCard(element, width, height, interactive)` / `ctx.CloseCard()` |
| Show over a fullscreen app (only for things the user asked to be interrupted for, like alarms) | `BreaksFullscreen = true` on the `Activity`, or `ctx.ShowCard(..., breaksFullscreen: true)`; it never takes focus there |
| Act on a circle before it becomes a search | `ctx.AddSelectionHandler(ISelectionHandler)` (return null to pass) |
| A chip on the results card | `ctx.AddResultChip(...)` |
| Right-click action on shelf files | `ctx.AddShelfAction(...)` |
| Tray menu item or toggle | `ctx.AddTrayItem(text, onClick, visible)` (`visible` is asked each time the menu opens) / `ctx.AddTrayToggle` |
| Mouse wheel / swipe over the notch | `ctx.NotchWheel`, `ctx.NotchHorizontalWheel` |
| Flash the notch edge for an event | `ctx.FlashEdge(color)` |
| Keep the edge lit while a state lasts | `ctx.SetGlow(color)` / `ctx.SetGlow(null)` |
| Save settings or data | `ctx.Load<T>()` / `ctx.Save(data)` (JSON per module) |
| Test hooks | `ctx.AddPipeCommand("<module>-<verb>", arg => "...")`; tests send lines to the `Shammy.Commands` named pipe |

Activity priorities: HUD 90, notification 80, low battery 75, charging 70, Bluetooth 60,
messages 50, downloads 40, voice-model 30, media 20. Pick something that fits between them.

## Look and feel

Cards follow Focus Wall (`Views/Cards/CardKit`, the one card kit): the open notch is a rail of 168×140 artwork tiles
(`WallTile`: an artwork field from `Fields`, plus `Art` always shown, `Rest` while dimmed, `Lit` while focused with
the one primary control), and a card opens into a `WallDetail` (a 300 px art panel the notch puts Back on, a tracked
kicker, a big title, one line, then the controls). Colour comes only from artwork fields and meaning (red); the
accent `#FF8A3D` is only for focus and the primary control. `Live` says who gets the focus when the notch opens and
`Peek` what the hover peek shows while the tile is focused. `CardKit.H24` says whether to write times on a 24-hour
clock; `Modules/Kit/CardKit` is an older facade over the same tokens.

Pure black notch, white text. Use the theme resources from `Theme/Theme.xaml`: brushes `Fg`, `Fg2`
(secondary text), `Fg3` (hints), `Fill1`–`Fill3` (subtle surfaces), `AccentBrush` (the Windows accent),
`Green`, `Red`, `Amber`; fonts `ShammyFont`, `ShammyDisplayFont`, `IconFont` (Segoe Fluent Icons; write glyphs
as `""` escapes in C#); styles `IconButton`, `Chip`, `TabButton`, `BareTextBox`. Compact ears are
32 px tall with text at 12 px SemiBold. Animate with `Shammy.Motion.SpringValue` / `Animator.Subscribe`,
never storyboards, and stop subscribing when nothing moves so idle CPU stays ~0.

## Building

```bash
dotnet build src/Shammy.App
dotnet test tests/Shammy.Core.Tests
```

Don't launch Shammy.exe from an agent: only one instance can run, and it drives the real screen.
