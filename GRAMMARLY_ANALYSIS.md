# Grammarly — Product & Technical Analysis

Studied 2026-09-28 from the public surfaces (grammarly.com/grammar-check, /paraphrasing-tool, /docs).
`app.grammarly.com` redirects to the marketing site when logged out, so the signed-in editor
was studied through its product pages, not first-hand.

## 1. Product surfaces

| Surface | What it is |
|---|---|
| **Docs** (app.grammarly.com) | Full document editor + AI Chat + "agents" sidebar. Replaced the classic editor. |
| **Go** | Assistant that follows you across apps/tabs (browser extension + desktop app). |
| **Free tools** (no login) | Grammar Check, Plagiarism Checker, Paraphrasing Tool, AI Detector, AI Humanizer, AI Chat, plus ~15 SEO generator pages. |
| **Parent company** | Grammarly is now part of **Superhuman**. |

### Agents inside Docs
Proofreader · Paraphraser (6 preset styles + custom "my voice", 6 languages) · Reader Reactions
(predicts how an audience reacts) · AI Grader (rubric → estimated grade) · Citation Finder ·
Humanizer · AI Detector · Plagiarism Checker.

### Pricing
- **Free:** $0, basic correctness, tone detection, 100 AI prompts/mo
- **Pro:** $12/member/mo yearly ($30 monthly), full-sentence rewrites, tone adjustment, brand style, 2,000 prompts
- **Enterprise:** unlimited prompts, confidential mode, DLP, roles/permissions

## 2. Core checking UX (observed on /grammar-check)

Test input had 10 planted errors; all 10 were flagged within about 1 s of typing stopping.

- **Red wavy underline** under each issue. One issue can cover several words
  (e.g. "Me and him has" is one span, "should of went" another).
- **Hover card** (not click): small popover showing
  - category label (e.g. *"Verb problem"*)
  - the replacement in **bold blue** (click to apply)
  - **Dismiss** (trash icon)
  - footer CTA
- **Right sidebar:** a badge with the count (`10`) + "Suggestions" header and onboarding steps.
  In the full app this lists each suggestion as an expandable card.
- The editor has native `spellcheck=false` so the browser's squiggles don't clash.
- The classic editor grouped suggestions into **Correctness / Clarity / Engagement / Delivery**,
  showed an overall score, and let you set **Goals** (audience, formality, domain, intent).
  That's from prior knowledge and wasn't verified live.

## 3. Front-end architecture (observed)

```
<div class="ql-editor" contenteditable>      ← Quill editor (plain text + Delta ops)
<grammarly-inkwell>  #shadow-root            ← Grammarly "Inkwell" Text Editor SDK v2.12.5
   ├─ iframe "text-decoration-for-doc-…"     ← transparent overlay, pointer-events:none,
   │                                            positioned over editor; draws underlines
   └─ iframe "inline-card-for-capi-…"        ← hover-card popover (position:fixed, z-index max)
<iframe background.html style=display:none>  ← hidden worker: auth + checking connection
```

Key design decisions worth copying:
1. **Underlines are never injected into the user's text DOM.** They are drawn in an overlay
   positioned by measuring text-range rectangles, so the editor's content and undo stack stay clean.
2. **Everything is a reactive stream** (RxJS: `debounceTime`, `switchMap`, `distinctUntilChanged`,
   `throttleTime`). Typing → debounce → send a *delta*, not the full text. Suggestions come back
   as ranges that get **rebased** as the text keeps changing (`TextRevisionSynchronizedQueue`,
   `TextRangeUpdateSemantics`, `Delta`).
3. **Anonymous auth:** `GET /v1/mediator/token` → `auth.grammarly.com/v4/api/oauth2/token`
   issues a guest token, so the free checker works without sign-up.
4. **CAPI** ("check API") is the suggestion backend, reached from the hidden background iframe
   (a persistent connection, not per-request REST).
5. UI popovers use **Floating UI**. The marketing site is **Next.js**.
6. Server-driven UI (`SDUICollectionChanges`): the backend decides what the cards contain.

## 4. Blueprint for our own checker

### MVP
- Editor with debounced checking (~400–600 ms after typing stops)
- Overlay underlines colored by category (red = correctness, blue = clarity, green = engagement, purple = tone)
- Hover card: category, explanation, one-click replacement, dismiss
- Sidebar list with count + "accept all"
- Word/character count, readability score

### Suggested stack
| Layer | Choice | Why |
|---|---|---|
| Editor | **Quill** / **Tiptap (ProseMirror)** / **Lexical** | Quill matches Grammarly; ProseMirror has decorations built in |
| Rule-based checks | **LanguageTool** (open source, self-host via Docker) | spelling + grammar, 30+ languages, returns offsets |
| AI rewrites / tone / clarity | **LLM API** (e.g. Claude) with JSON output of `{start, end, replacement, category, reason}` | sentence rewrites, tone, paraphrase modes |
| Transport | WebSocket, or REST + `AbortController` to cancel stale checks | |
| Range mapping | Keep suggestions as offsets; on every edit, shift/invalidate them through the change delta | the hard part, and it's what Inkwell's revision queue does |

### Hard problems to plan for
- Keeping suggestion offsets valid while the user types (rebase through deltas)
- Merging overlapping suggestions from two engines (rule + LLM)
- Positioning the overlay across line wraps, scroll, and resize (`Range.getClientRects()`)
- Cost/latency of LLM calls: run the cheap rule engine live and the LLM per paragraph on idle
