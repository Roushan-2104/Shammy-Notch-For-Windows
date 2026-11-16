# Shammy website: the final brief

Agreed with Roushan on 16 Nov 2026. Nothing is built yet; the next step is three design directions as mockups to pick from.

## Decisions

| Question | Answer |
| --- | --- |
| Where it lives | GitHub Pages, from the Shammy repo: `roushan-2104.github.io/Shammy-Notch-For-Windows` (a custom domain can come later) |
| What it covers | Shammy for Windows in full, plus one section teasing the phone app (Shammy Island), which isn't public yet |
| Pages | One long landing page, plus **What's new** (from the release notes) and **Privacy** |
| Main button | **Download Shammy**: one click on `releases/latest/download/Shammy-Setup.exe`, with a small "All releases" link beside it |
| Live demo | A working island at the top of the page: hover to open it; it changes as you scroll (a song, a call, a timer, an alarm in the sky of the visitor's local hour) |
| Stack | Astro, in `site/` in the repo, built and deployed by a GitHub Actions workflow |
| Look | The design skill deals three fresh directions from our Cosmos boards and the app's real look; Roushan picks one from mockups |
| Voice | Calm and confident, with a wink. Humour lives in the supporting lines, never in the headline's job |
| Pet | A quiet guide: its eyes live in the island at the top and react as you scroll; it waves on the download button |
| Pictures | Fresh screenshots and short clips of the real app on test data (made-up songs, events, messages), captured with the harness |
| Going live | Only after Roushan says "Yes, publish the site": turning on Pages and pushing happen then, not before |
| Typeface | The picked direction's own font for the page; real app fragments keep Schibsted Grotesk inside them |
| Analytics | Privacy-friendly page-view counts only (cookie-free, e.g. GoatCounter), no personal data, no cookie banner |
| Footer credit | "Made by Roushan", linking to github.com/Roushan-2104; no email or other contact |
| Phone teaser | "Shammy Island", coming soon, no date, no download; a "Watch the repo" link so people get notified on GitHub |
| Where the work happens | Its own session and its own git branch (`site`), in a separate worktree folder, away from the product work on `next` |

## Skills that guide the build

- **design-taste-frontend** (Leonxlnx/taste-skill, 116.7k stars): deals the directions, writes the argument and copy deck, and measures the page in a real browser at 1440, 768 and 390 px (margins, headline lines, empty stretches, contrast). Its run: argument, directions, idea, words, build, gate, look, motion.
- **ui-ux-pro-max** (nextlevelbuilder, 150.3k stars, installed 16 Nov 2026): a design-system pass for the product type, the Astro stack checklist, and the accessibility and touch rules (4.5:1 contrast, 44 px targets, reduced motion, no layout shift).
- **impeccable** (pbakaus, 111.2k stars): the final audit and critique of type, spacing and polish before publishing.
- **antislop** (miqdadbadjuber/anti-slop, 9.3k stars, MIT, installed 16 Nov 2026), in **DURING** mode, for design, copy and code: every visual choice must pass its purpose test, every line of copy is checked against the copywriting patterns (empty vocabulary, staccato drama, trailing negations, invented specifics, em dashes), and each deliverable ends with its Delivery Gate. It is a filter; direction comes from a `site/DESIGN.md` written once a direction is picked.

**One conflict to settle at the direction stage:** the taste skill bans gradients, glass and glows in the page's own chrome, but Shammy's real look is frosted glass on an aurora. Antislop doesn't ban them; it asks what each one is for. Plan: the page itself follows the taste skill's house rules (flat fields, real type, no ornaments), and the product appears as **real interface fragments** (the live island, screenshots, clips) that keep their own glass and light, because that is what the product really looks like. Both skills allow that.

## The argument (one claim per section, with its proof)

1. **Your PC finally has a notch. It floats.** Proof: the live island at the top, which the visitor can hover and open.
2. **It keeps what's happening in view.** Music with lyrics, calls with mute, timers, downloads, meetings. Proof: the island changing as the visitor scrolls, plus real clips.
3. **Circle anything to ask about it.** Proof: the Circle clip and answer cards (flights, UPI IDs, GST sums, translations).
4. **Hold Ctrl + Win and talk; it types where you are.** English, Hinglish, and Hindi in Devanagari. Proof: the Dictation clip.
5. **Stage: a teleprompter and a ring light your call never sees.** Proof: the Stage clip.
6. **The time, always; your month beside it; alarms in the sky outside.** Proof: the calendar wing and the four alarm skies (the visitor's own hour shown first).
7. **Light enough to forget.** About 90 MB idle, no paid AI, the glass is a 26 KB copy of your wallpaper. Proof: the numbers from the README's measurements, and the installer size.
8. **Share your screen; Shammy looks away.** Codes, message text and event titles hide; Stage and the ring light never show in a share. Proof: before and after fragments.
9. **Coming to your phone: Shammy Island.** A teaser only, no download.
10. **Get Shammy. It's free.** Windows 10 (2004+) and 11; MIT licence; updates itself, usually a few MB. The download button and "All releases".

Sections without a claim don't exist (no logo wall, no testimonials, no invented numbers or reviews).

## Voice

Calm, clear sentences; the wink comes in the line under the headline. Agreed samples:

- **Hero:** "Your PC finally has a notch. It floats." / "A little island at the top of Windows for your music, calls, timers and a small pet, in about 90 MB."
- **Tech:** "About 90 MB of memory while it sits there." / "Task Manager says so; we asked it twice. The frosted glass is a 26 KB blurred copy of your wallpaper, remade only when the wallpaper changes."
- **Alarms:** "Alarms that ring in the sky outside." / "At 6:30 it's a sunrise. At 2 AM it's the moon, and we don't ask questions."
- **Privacy:** "Share your screen. Shammy looks away." / "Event titles turn to 'Busy' and your codes hide until the share ends."
- **Download:** "Get Shammy. It's free." / "It runs on Windows 10 and 11 and updates itself in the background, usually a few MB at a time."

Small moments carry humour too: the 404 page, the footer, the pet's wave. Every line goes through the antislop copywriting check before it ships.

## What we already have (the source material)

**Design canvas:** https://claude.ai/artifact/VRFDMVZrRVoFaYWxVcYDUf, with 17 boards:
- Overview, Cosmos references (24 from 20 searches), Key moments, and Looks A, B and C ("One island, five lights", "Instruments", the aurora look we built).
- The live look (LiveC), Anatomy and Dictation.
- Time and the calendar wing: CalendarWing, CalendarWow and CalendarDots.
- Alarms: references (526 looked at, 16 searches), three directions, Sunrise pushed, and the sky by the clock.

**Other boards:**
- "Light is the interface": https://claude.ai/artifact/KsbWL2gXowZYHD7fWXa1LM
- Cat wheel: https://claude.ai/artifact/YR1sT2rN9n3ACMr3UGwteB
- The phone mockup: ice-grey gallery, serif headlines, painted wallpapers (in `Halo-phone/mockup/`).

**Cosmos threads we keep coming back to:**
- Living light inside dark glass.
- A floating island, not one stuck to the edge.
- Sunrise lamps and gradient widgets.
- Thin, huge, lit numbers.
- Dots for a month.
- Calm, single bright controls.

Cosmos uploads carry no licence, so the site redraws these ideas rather than copying the pictures.

**App pictures** (`docs/images/`): the hero, alarms, calendar, cards, Circle answers and GIF, the dial, dictation, the open island in a call and while writing, palettes, Stage, waves. The wallpaper is Unsplash oMcTmNHclZI by Condor Wei, credited.

**Facts the copy may use** (all from the README):
- About 90 MB idle and a 104 MB installer.
- No paid AI; MIT licence.
- Windows 10 (2004+) or 11, with Live Captions on 11 22H2 or newer.
- Updates are usually a few MB.
- Codes are cleared after a minute and hidden in a share.

## The live island (the one thing visitors can do)

- A real HTML/CSS recreation of the island, not a video: a floating dark pill with the pet's eyes and the time on the right (the visitor's own clock).
- Hover or tap: it opens like the real one.
- As the page scrolls, it wears the moment each section talks about: a song with lyrics, a call with the mute circle, a timer, Circle, the calendar wing, then an alarm in the sky of the visitor's local hour.
- On a phone it works by tap. With reduced motion, it changes without travel. Without script, a still picture of the island.

## Build plan

1. **Directions:** the taste skill deals three directions; I mock each one as a first screen plus two sections on the design canvas; Roushan picks or pushes one.
2. **Captures:** fresh stills and clips with the harness, on test data, at website sizes. It never captures the camera, real notifications or the clipboard, and avoids the video folder.
3. **Build in Astro:**
   - Tokens, the hero with the live island, then each section from its blueprint.
   - What's new is generated from the release notes.
   - Privacy is drawn from the README's privacy section.
4. **Checks:**
   - The taste skill's lint and inspector at three widths, with no FAILs.
   - The ui-ux-pro-max accessibility and touch checks.
   - An impeccable audit, then a ponytail review before committing.
5. **Preview locally**, then ask "Yes, publish the site?" Only on yes: add the Pages workflow, turn on Pages, and push `main`.

## Round two (16 Nov 2026, after the first three directions)

The first three directions (Loud and flat, Colour chapters, The page is the object) were turned down: "not at all fascinating; the features shown are very general". New decisions:

- **Lead with the features nobody else has:** syncing with your phone (notifications both ways, sending things between them), Circle to search, Dictation in Hinglish and Hindi, Stage with the invisible ring light, and the living island. The final order comes from a last30days ranking of every feature (Reddit, Hacker News, X, YouTube, GitHub, the web).
- **Fascinating means both:** a cinematic scroll opening, then a playable Windows desktop where visitors try the features themselves.
- **The page looks like Shammy:** dark aurora light and smoked glass for the page itself. The taste skill's no-glass and no-gradient rules are switched off for this site; antislop still requires a purpose for every effect.
- **References to study:** Apple product pages, Raycast and Linear, Arc and Nothing, recent Awwwards Sites of the Day.

## Pictures (16 Nov 2026)

- **Music:** "Night Song" by Nusrat Fateh Ali Khan & Michael Brook (the user's own listening). Its real cover (Real World Records) is used on the **private canvas mockups only**. The public site needs an original cover or the label's permission; no lyrics anywhere.
- **Backgrounds are drawn in code,** after the four Cosmos wallpaper looks: grainy blurred gradients, fluted glass, a light beam on black, and aurora ribbons. They animate slowly, with reduced motion respected. No photo downloads.
- **Product pictures:** fresh, sharp captures of the real Shammy at website sizes, plus HTML fragments for anything interactive.

## Typography (decided 16 Nov 2026)

- **Funnel Display** (Google Fonts, SIL OFL) for headlines and the big light numbers, and **Funnel Sans**, its sister face, for text. Both are self-hosted and subset.
- **Geist Mono** for real code (the Claude command line).
- **Anek Devanagari** for Hindi.
- Earlier picks turned down:
  - Round one: Geist, Instrument Serif with Inter, Mona Sans. They were "too plain / generic".
  - Round two: the user asked for characterful display plus Apple-like refinement, and picked Funnel Display from eight faces.

## 3D (decided 16 Nov 2026)

- **three.js (0.170) for the island, matched to Shammy today (1.4.0):**
  - **Shape:** a slim extruded pill, about 7.4:1, in frosted, near-black smoked glass (transmission, roughness 0.34, dark attenuation, light clearcoat, no rainbow sheen).
  - **Inside:** purple and teal aurora light drifts inside it, with a thin rim of light along the bottom edge.
  - **Face:** the pet's eyes, a green-dot status and the bold time with a small PM, all in Schibsted Grotesk, the app's own face.
  - **Extras:** the handle tab sits under it, and it turns with the pointer.
- **Loading:** three.js loads after the first paint. The CSS 3D island is the fallback for no WebGL, slow devices and reduced motion.
- **Prototype:** https://claude.ai/artifact/BhNvAurSsz2U4sFhEwx2b5, also kept as `research/island3d-prototype.html`.
- **Later candidates:** GSAP ScrollTrigger for the scroll opening, and Rive for the pet's eyes. Neither is installed yet; each needs an OK before downloading.
