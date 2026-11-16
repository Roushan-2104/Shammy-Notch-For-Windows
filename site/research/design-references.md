# Reference study for the Shammy site (16 Nov 2026)

A subagent studied Apple, Raycast, Linear, Arc, Nothing and Awwwards 2026 Sites of the Day for technique, not branding. WebFetch reads page text only, so motion details come from published write-ups or prior knowledge; check them in a browser before relying on them.

## Techniques worth taking

- **Apple:**
  - AirPods Pro: a scroll-scrubbed image sequence in a sticky canvas (CSS-Tricks write-up).
  - Vision Pro: "drawers" that play a short loop beside the text when opened.
  - Dynamic Island introduction: short loops play once on view, the app shrinking into the pill.
  - Play/pause on every motion element.
- **Raycast:** the hero is a keyboard built from real elements, which says "keyboard-first" without a sentence; product UI built in HTML; videos load only on click.
- **Linear:** the product shown in full in HTML, inhabited with real IDs and status changes; one text block plus one product panel per section.
- **Arc:** human touches (a signature wall); SVG and WebP throughout.
- **Nothing:** a type system alone carries the brand.
- **Awwwards 2026:**
  - Cyera AI Guardian (11 Nov): one mini-game per capability.
  - ZUI_OS (nominee): a plain HTML fake OS with a boot sequence and hidden commands.
  - Bulletproof (16 Nov): heavy motion with 7.8 for accessibility.
  - Where Worlds Take Shape: the lowest usability score; playfulness costs clarity.

## Ideas proposed for Shammy

1. **The pill becomes the page.** The visitor lands on black with only the island; scrolling about 150vh widens it, the aurora spills out and the camera pulls back to the playable desktop.
2. **Your hour's sky.** The page's aurora follows the visitor's clock; drag a time scrubber and an alarm rings in that hour's sky.
3. **Circle anything.** Lasso a PNR in an SMS screenshot, a Hindi word in a meme or a cricket score; the island answers.
4. **Hinglish to two scripts.** Hold to talk (or pick a recorded clip); text lands in a fake chat; flip between Roman and Devanagari.
5. **Two views of one call.** "You" see the teleprompter and the ring light warming your face; "they see" a clean feed. A divider compares them.
6. **Middle-click the website.** The cat wheel opens and becomes the site's menu (long-press on touch).
7. **OTP hop.** A fake phone buzzes; the island shows the code; Ctrl+V fills a fake login.
8. **The cat follows the cursor.** It dozes when idle, perks up during dictation and flinches at the alarm.
9. **Peek, don't shout.** One cricket wicket peeks into the island during the scroll.
10. **The download lives in the island.** The island morphs to "Shammy for Windows · free"; the cat carries a box.
11. **Your Shammy minute.** After three tries, a shareable card of what you circled, said and woke to.

## Budget (Astro on GitHub Pages)

- **First screen:** LCP under 2.0 s; first view under 350 KB.
- **Hero:** JS under 50 KB gzipped, with the desktop loaded `client:visible`.
- **CSS:** under 30 KB.
- **Fonts:** two subset WOFF2 files under 80 KB.
- **Responsiveness:** INP under 200 ms; CLS 0.
- **Effects:**
  - At most 3 live `backdrop-filter` layers, with pre-blurred images on low-end devices.
  - The aurora pauses off-screen.
  - Image sequences of 60 AVIF frames or fewer, loaded after first paint.

## Avoid

- **Hero video:** no autoplay hero MP4.
- **Scroll:** no scroll-jacking or smooth-scroll libraries; a "skip intro" link; reduced motion jumps to the final frame.
- **Keyboard:**
  - A keyboard path for every gesture.
  - Not Space for dictation (it scrolls the page).
  - No focus trap in the fake desktop.
- **Screen readers and contrast:** island messages in `aria-live`; 4.5:1 contrast on glass.
- **Sound and mic:** sound only after a click; the mic only on explicit opt-in (Web Speech sends audio to Google and is missing in Firefox).
