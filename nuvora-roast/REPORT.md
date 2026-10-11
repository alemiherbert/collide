# Nuvora roast 🔥

Target: <https://nuvora-ug.netlify.app> (Flutter web, CanvasKit) · API: `web-production-454a5.up.railway.app`
Reviewed 2026-10-11, with the owner's permission, **passively** (no brute force, no load, no authenticated API calls beyond my own test account).

## The verdict

Nuvora has a surprisingly grown-up *front half*: plain-language terms, a refund promise, a delete-account screen that tells you exactly what happens, and a clean bundle with no leaked keys. The *back half* is a different story. On a slow-3G Android it shows a white-ish nothing for **58 seconds** before the login appears. Once there, a Netlify advert ("Build your own site… a shop for my handmade surfboards") floats over the buttons of a children's-money app and **eats the taps** on *I accept* and two of the three nav tabs. "Forgot PIN?" is a decorative question mark. Sign-up never checks that you own the phone number you typed, so the account (and, per the app's own copy, the child behind it) belongs to whoever types the number first. The production API proudly publishes its full 80-endpoint map at `/docs`. None of this is hard to fix, and almost all of it is a one-line or one-config fix, listed below.

> **Beta context:** payments are not live yet (per the owner), so top-up was reviewed as UI only and I stopped there. Add-child/link-card could not be reached (see [What I could not test](#what-i-could-not-test)).

Severity: 🔥 critical · ⚠️ major · 💅 polish. Screenshot names are in `screenshots/`.

---

## Measurements (the evidence base)

**Slow-3G mobile run**: Chromium via Playwright, 360×740 @2x, Android Chrome UA, CDP throttle 400 kbps / 400 ms latency, cache disabled (`work/measure.js` → `work/perf-3g.json`).

| Metric | Result |
|---|---|
| First paint (navy splash from `index.html`) | **0.78 s** (FCP 0.82 s) ✅ |
| Flutter canvas appears | **55.3 s** |
| **First real UI** (login; splash removed) | **57.7 s** 🔥 |
| Total bytes transferred | **2,674,550 B (2.55 MiB)**, 20 requests |

Top-10 requests by transfer size (Brotli on the wire):

| # | Bytes | Resource |
|---|---:|---|
| 1 | 1,653,084 | `www.gstatic.com/flutter-canvaskit/…/canvaskit.wasm` (**third-party origin**) |
| 2 | 902,606 | `/main.dart.js` |
| 3 | 64,014 | `fonts.gstatic.com/…/roboto/…woff2` |
| 4 | 24,812 | `canvaskit.js` (gstatic) |
| 5 | 10,215 | `/.netlify/scripts/hud?variant=public` (the badge) |
| 6 | 7,048 | `assets/fonts/MaterialIcons-Regular.otf` |
| 7 | 3,799 | `/flutter_bootstrap.js` |
| 8 | 3,784 | `/` (index.html) |
| 9 | 1,110 | `CupertinoIcons.ttf` |
| 10 | 1,097 | `/favicon.png` |

Six more font files from `fonts.gstatic.com` (Noto emoji + fallbacks, ~60–95 KB each by HEAD) were still *in flight* when the UI appeared, so the 2.55 MiB is a floor.

**Lighthouse 13.5 (mobile, default simulated throttling)**: Performance **35** · Accessibility **93** · Best Practices **81** · SEO **100**. FCP 1.4 s, LCP 17.5 s, Speed Index 11.9 s, TBT 12.6 s, TTI 24.3 s, CLS 0.003. *Caveat:* this ran on a slow shared container CPU, so TBT/TTI are inflated. Treat them as directional; the byte counts and the 3G timings above are the solid numbers. Full report: `lighthouse-mobile.report.html`.

---

## Performance

### 🔥 58 seconds to the login screen on 3G
- **Evidence:** table above; `02-mobile-first-ui-3g.png` is what the 3G user finally sees. CanvasKit alone is 1.65 MB, 62% of the payload.
- **Roast:** the splash is lovely. It will be admired for nearly a minute.
- **Fix:**
  1. Self-host CanvasKit (build with `--no-web-resources-cdn`) so it rides the already-open Netlify HTTP/2 connection instead of paying DNS+TLS to a second origin at 400 ms RTT, and set `Cache-Control: public, max-age=31536000, immutable` on `/canvaskit/*` and `/assets/*` via a `_headers` file.
  2. Shrink `main.dart.js` (903 KB): `flutter build web --release --tree-shake-icons`, and move rarely used screens (delete-account, change-PIN, terms viewer) behind `deferred as` imports.
  3. Strategically: for parents on prepaid 3G, ship the same code as an installable Android app/PWA so the 2.5 MB is paid once, and add an honest "Loading Nuvora… (first time only)" progress line under the splash so waiting doesn't look like a crash.

### ⚠️ A stray emoji drags in fallback fonts and renders as tofu
- **Evidence:** the login button reads `❓ Forgot PIN?` (semantics label). On 3G the ❓ rendered as a **tofu box** (`02-mobile-first-ui-3g.png`) because CanvasKit had to fetch Noto Color Emoji + five other fallback fonts from `fonts.gstatic.com` (see network list). The API also sends emoji in messages (`"Welcome Roast Tester 🎉"`).
- **Fix:** replace the emoji with `Icon(Icons.help_outline)` (already bundled in MaterialIcons) and strip emoji from UI strings and API messages. Bundle the text font(s) you actually use as assets so Roboto isn't fetched at runtime.

### 💅 Revalidate-every-time caching
- **Evidence:** `main.dart.js`, `flutter_bootstrap.js`, `manifest.json` all return `cache-control: public,max-age=0,must-revalidate`; Lighthouse flags cache lifetimes. I did not measure a repeat visit.
- **Fix:** `_headers`: `/canvaskit/*` and `/assets/*` → `immutable`, one year; keep HTML/bootstrap at `max-age=0`.

---

## UI/UX

### 🔥 The Netlify badge blocks taps and shows an ad inside a money app
- **Evidence:** an `<iframe id="nl-badge-frame">` sits bottom-right over the app. `document.elementFromPoint` at the middle of the **Transactions** tab (180,700) and **Profile** tab (300,700) returns the iframe, and at the middle of the **I accept** button (180,680) as well. Real clicks there do nothing (terms screen stayed put; tab didn't switch); clicking the left edge worked. Tapping the badge opens "Build your own site with Netlify" (`27-netlify-badge-ad-over-app.png`). It covers the footer text and "Create an account" (`05-mobile-login-wrong-pin.png`, `07-mobile-signup.png`), and truncates nav labels to "Transa…" (`13-mobile-dashboard-empty.png`). The script is `/.netlify/scripts/hud?variant=public`, which its own header calls "the badge and owner toolbar Netlify injects on published sites".
- **Roast:** the primary CTA of your terms screen has a rival advertiser.
- **Fix:** this is injected by the hosting platform, not the app. Remove it in the Netlify site/team settings or by moving to a plan/host without the badge. I couldn't see the dashboard, so I can't name the exact toggle. A custom domain on a paid plan is the usual route. Until removed, give bottom-anchored controls ≥ 64 px bottom padding so the badge can't sit on them.

### 🔥 "Forgot PIN?" does nothing
- **Evidence:** tapping it: URL unchanged (`#/login`), no network request, no dialog or snackbar in 1.5 s (`06-mobile-forgot-pin.png` shows only the hover state). With a 4-digit PIN, a 5-wrong-tries lockout (Terms §2) and no SMS verification, a parent who forgets their PIN has no way back in.
- **Fix:** implement PIN reset by SMS OTP (Yo Uganda already sends your SMS per the Terms). Until then, make the button open a sheet with the WhatsApp support link and the support number rather than silently doing nothing.

### ⚠️ A server validation error deletes the Create Account button
- **Evidence:** sign-up with phone `12345` and matching PINs → `POST /auth/register` returns **422** (`"Phone must be 256XXXXXXXXX format"`). The UI shows **no error and the Create Account button is gone**, still gone 13 s later (`11-mobile-signup-invalid-phone.png`). Only reload recovers. *Hypothesis (not verified):* the client expects a string `detail`, but FastAPI's 422 returns a list, so parsing throws and the loading state never resets.
- **Fix:** validate the phone client-side (`^7\d{8}$` after the +256 prefix; the placeholder already implies it), parse `detail[0].msg` for list-shaped errors, and reset `isLoading` in a `finally`.

### ⚠️ Validation: one error at a time, in the wrong place, and it moves the button
- **Evidence:** empty sign-up shows only "Enter your name." under the last field (`09-mobile-signup-empty-submit.png`); empty login flags only the PIN and ignores the phone (`04-mobile-login-empty-submit.png`). The message pushes the button down ~36 px (buttons went y=633→669 in the semantics tree). My second tap landed on empty space.
- **Fix:** per-field inline errors under each field with reserved height (no layout jump); validate phone before PIN; clear each error as the field changes.

### ⚠️ Login auto-submits on the 4th digit, even with a malformed phone
- **Evidence:** typing phone `12345` + PIN `1234` fired `POST /auth/login {"phone":"25612345",…}` immediately → `401 "Incorrect PIN. 4 attempt(s) remaining before lockout."` (`05-mobile-login-wrong-pin.png`). A typo in the phone number burns a lockout attempt and the screen gives no loading state.
- **Fix:** don't send until the phone validates; don't count attempts for numbers that fail format checks; add a loading state; consider an explicit submit instead of auto-submit.

### 💅 Desktop is the phone layout, stretched
- **Evidence:** at 1440×900 the form is 1,350 px wide, the phone placeholder floats centered in a giant box, and the PIN boxes hug the left (`20-desktop-login.png`, `22-desktop-signup.png`, `23-desktop-dashboard.png`).
- **Fix:** wrap screens in `Center(child: ConstrainedBox(maxWidth: 480))`.

### 💅 Raw values on screen
- **Evidence:** Profile shows `256700000099` unformatted and the role as raw `parent` (`15-mobile-profile.png`).
- **Fix:** format as `+256 700 000 099`; label "Parent".

### 💅 Navigation: tabs and sub-screens never change the URL
- **Evidence:** Home, Transactions, Profile and Change PIN all stayed at `#/dashboard`.
- **Fix:** give each tab/sub-screen its own route so refresh, back and deep links land where the user was.

### 💅 Path-style deep links 404 (hash links are fine)
- **Evidence:** `GET /register`, `/login`, `/dashboard` → **404** Netlify "Page not found" (`24-deeplink-path-404.png`). The app uses hash routing, and `/#/register` loads (`25-deeplink-hash-register.png`), `/#/dashboard` logged-out redirects to `#/login`, and reload while logged-in stays on the dashboard (`26-after-reload-logged-in.png`). So **hash deep links work; any path link (e.g. from an SMS or WhatsApp message) hits a dead end.**
- **Fix:** add `_redirects` with `/* /index.html 200` and call `usePathUrlStrategy()` so shareable links are clean paths.

### 💅 Delete account is hidden under the nav
- **Evidence:** at a 740 px viewport the button sits at y≈710 under the 80 px tab bar; it only becomes tappable after scrolling (y→585). "Log Out" is a 22 px-high button (`15b-mobile-profile-scrolled.png`).
- **Fix:** add bottom padding equal to the nav height; make Log Out a full-size button.

### ⚠️ New-parent dead end, and copy that contradicts the Terms
- **Evidence:** after sign-up, Home says "Ask the school to add your number" with a manual **Check again** button (`13-mobile-dashboard-empty.png`). But Terms §3 says "You link your child to your account using the card number." The API has `/family/send-code` and `/family/claim`, so a code flow exists, but nothing in the UI offered it to me.
- **Fix:** one story. If linking is by school-added phone, say so in the Terms. If by card number, add the "Link a card" button to the empty state. Add an SMS to the parent when the school links them, so nobody has to keep pressing Check again.

### ✅ Credit where due
Plain-language terms with fees and refund timelines up front (`07-mobile-signup.png`), the lock screen (`18-mobile-app-lock.png`), and the delete-account screen explaining a 72-hour grace period and what's retained (`17-mobile-delete-account-dialog.png`) are better than many shipped fintech apps.

---

## Accessibility

### ⚠️ Pinch-zoom is disabled
- **Evidence:** runtime viewport meta is `width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no`; Lighthouse `meta-viewport` fails.
- **Fix:** after boot, `document.querySelector('meta[name=viewport]').content='width=device-width, initial-scale=1'`, and make sure text respects the system font scale. Parents with weak eyesight are the target audience.

### ⚠️ Primary buttons fail WCAG contrast
- **Evidence:** pixel-sampled from `04-mobile-login-empty-submit.png`: button text `#102B5C` on `#168FF5` = **4.13:1** (needs 4.5:1). This is on every primary button: Continue to Wallet, Create Account, I accept, Check again, Update PIN. Subtitle `#64748B` on `#F4F7FA` = **4.43:1** (just under). Other greys measured 4.76:1 (pass).
- **Fix:** white text on `#0B6FD0` (5.00:1) or `#0A66C2` (5.69:1); or keep navy text on a lighter blue like `#6CC0FF` (6.98:1). Nudge the subtitle grey to `#5B6B80` (5.06:1).

### ⚠️ Screen readers get a mostly empty page until accessibility is enabled
- **Evidence:** Flutter's semantics tree is off by default; the only accessible node at load is a 1×1 px `flt-semantics-placeholder` ("Enable accessibility") at (-1,-1). Once enabled, buttons and tabs are well labelled (good), but the phone field and PIN row appear as unlabeled nodes in the tree (login nodes 9 and 11), and the ❓ is read as part of the button name. I didn't test with TalkBack/NVDA, so this is based on the semantics tree only.
- **Fix:** call `SemanticsBinding.instance.ensureSemantics()` at start-up so the tree is always on; give each `TextField` and each PIN group a `Semantics(label: …)`; drop the emoji from labels.

### 💅 Tap targets are small
- **Evidence (semantics tree, CSS px):** primary buttons 40 px high; "Forgot PIN?" 32; "Create an account" 32; "Check again" 32; "Not now" 32; "Email us" 32; back button 40×40; **Log Out 22**; sign-up "Already have an account? Log in" **18** (fails even WCAG 2.2's 24 px minimum). Android's guideline is 48 dp.
- **Fix:** `MaterialTapTargetSize.padded` / `minimumSize: Size(48,48)` on all buttons and text buttons.

### 💅 Lighthouse's 93 is flattering
- **Evidence:** `color-contrast` and `target-size` are "not applicable" in the report because the app is a canvas. The 93 and SEO 100 reflect a near-empty DOM, not the UI above.
- **Fix:** don't cite the score as proof of accessibility; test with TalkBack on a real phone.

---

## Security

All of the below were observed passively: public pages, the API's public OpenAPI spec, and calls the app itself makes. I made **no** authenticated calls beyond my own test account.

### 🔥 Sign-up never verifies that you own the phone number
- **Evidence:** `POST /auth/register` with a name, phone and PIN returned **200 and a session token immediately**, with no OTP step (`12-mobile-after-signup.png`). The dashboard then says "Your child shows here once the school has your phone number (…) on their records." So whoever registers a parent's number *first* is the one shown that parent's children. *Impact path is inferred from the app's own copy and the Terms ("Your account is tied to your phone number"); I could not test it against a real child record.*
- **Fix:** send an SMS OTP and only issue a token after it's confirmed, then set the PIN. The Yo SMS plumbing already exists. Require an OTP again for PIN reset and for a new device.

### 🔥 Production API publishes its whole map, plus a diagnostics endpoint
- **Evidence:** `GET /docs` (Swagger UI), `/redoc` and `/openapi.json` (56 KB, **80 operations**) are public on the production host. That includes the sensitive-sounding `/reports/school/{id}/payout`, `/reports/settlements/auto`, `/users/` (POST), `/students/{id}/assign-nfc`, `/payments/nfc` and `/payments/sync`. Unauthenticated `GET /diagnostics/yo` returns payment-gateway config (`test_mode`, `yo_live`, `yo_live_explicit`, `yo_api_host`; names only, no values recorded). `GET /` returns `"docs":"http://localhost:8000/docs"`, a dev leftover. Public HTML pages also exist at `/issue/` ("Card Admin — School Wallet"), `/tuckshop/` and `/account/delete`. I loaded none beyond the title, and did not test what their actions require. `GET /schools/` is public (5 schools: id, name, location, badge_url).
- **Roast:** the API ships its own treasure map, helpfully pointing at localhost.
- **Fix:** `FastAPI(docs_url=None, redoc_url=None, openapi_url=None)` in production (or put them behind auth). Remove `/diagnostics/*` or require an admin token. Serve `/issue/` and `/tuckshop/` only from an authenticated admin origin. Fix the root message.

### ⚠️ Missing security headers on the site
- **Evidence:** the only security header is HSTS (`max-age=31536000; includeSubDomains; preload` ✅). No `Content-Security-Policy`, no `X-Frame-Options`/`frame-ancestors` (so a money app can be framed → clickjacking), no `X-Content-Type-Options`, `Referrer-Policy` or `Permissions-Policy` (`work/headers-root.txt`).
- **Fix:** add a Netlify `_headers` file. Start with the low-risk ones:
  ```
  /*
    X-Frame-Options: DENY
    Content-Security-Policy: frame-ancestors 'none'
    X-Content-Type-Options: nosniff
    Referrer-Policy: strict-origin-when-cross-origin
    Permissions-Policy: camera=(), microphone=(), geolocation=()
  ```
  Then roll out a full CSP in `Content-Security-Policy-Report-Only` first (Flutter CanvasKit needs `'wasm-unsafe-eval'` and the gstatic origins; `index.html` has inline scripts, so use hashes).

### ⚠️ Session lives in `localStorage` next to its own "encryption" key
- **Evidence:** after login, `localStorage` holds `FlutterSecureStorage` (44 chars: the key), `…jwt_token`, `…user_json`, `…last_active_at`. On web, `flutter_secure_storage` keeps the key beside the ciphertext, so it only stops casual shoulder-surfing, not XSS (and you currently have no CSP). The JWT is HS256 with claims `sub, user_id, role, exp` and a ~24 h lifetime (matches the Privacy Policy).
- **Fix:** ship the CSP above. Prefer an `HttpOnly; Secure; SameSite=Strict` cookie for the session; keep the 24 h expiry; add server-side revocation (logout, PIN change, account deletion).

### ⚠️ A 4-digit PIN is the only factor
- **Evidence:** the UI, Terms §2 and the error message ("4 attempt(s) remaining before lockout") show a 5-strike / 15-minute lockout, plus no phone verification (above). I did not try to lock any account or test weak PINs.
- **Fix:** confirm lockout is also rate-limited per IP/device (and that locking one parent can't be done by anyone typing their number), reject trivial PINs (0000, 1234, repeating), and add the OTP step for new devices.

### ✅ What's solid
HTTPS everywhere with HSTS preload. CORS is an exact-origin allowlist (preflight from `https://evil.example` → 400 with no `Access-Control-Allow-Origin`). `main.dart.js` (3.27 MB) contains **no** API keys, JWTs, secrets, TODO/FIXME, debug flags or source map; the only backend host is the Railway URL and the only email is `support@nuvora.ug`. The two error responses the app produced (401, 422) contained **no stack traces** (the 422 echoes the submitted `input`, which is fine). Backend endpoints the app calls: `/auth/{login,register,terms,change-pin,unlock}`, `/account/closure[/preview]`, `/cards/orders…`, `/family/{claimable,send-code,claim}`, `/students/…`, `/wallets/…`, `/topup/`.

---

## Trust & Business

### ⚠️ It looks like a hobby deploy
- **Evidence:** the front end is on `nuvora-ug.netlify.app`, the API on `web-production-454a5.up.railway.app`, and the Netlify advert is on screen.
- **Fix:** `nuvora.ug` for the app, `api.nuvora.ug` for the API, and remove the badge. Parents are asked to put a child's lunch money behind this URL; the domain *is* the brand.

### ⚠️ Two support emails, and the legal contact is a personal Gmail
- **Evidence:** the Terms and Privacy Policy list `markmiiro77@gmail.com` and `0760 945 424`; the in-app "Email us" opens `mailto:support@nuvora.ug`. The "Get support on WhatsApp" link pre-fills the parent's account number into the message (a nice touch) but goes to the same personal mobile number.
- **Fix:** one monitored `support@nuvora.ug` everywhere. Show a registered company name, registration number, physical address (the Privacy Policy has Kireka, so repeat it in-app), and support hours.

### ⚠️ Terms/Privacy are only reachable during sign-up
- **Evidence:** the login screen and the Profile menu (Lock app, Change PIN, Support, Log Out, Delete account) have **no link** to Terms or Privacy. You can only read them by starting a registration.
- **Fix:** footer links on the login screen and a "Terms & Privacy" row in Profile (the Terms themselves promise to show new versions, so users must be able to re-read them).

### ⚠️ Money-holding claims need a named, licensed backer
- **Evidence:** Terms §4: "We hold the money on your child's behalf, keep it separate from our own money." The wallet is "not a bank deposit". Nothing in the app names the licensed bank/payment provider, any regulator licence, or a data-protection registration number (the policy refers to the PDPO but gives no registration number).
- **Fix:** I can't judge the legal position; this is a question for his lawyer. Whatever the answer, say it plainly on the sign-up screen, e.g. "Funds are held by <licensed partner>", and show the licence and PDPO registration numbers.

### 💅 "Secure/securely/safely" appears three times on the login; the proof is missing
- **Evidence:** the login says "SECURE LOGIN", "Secure 4-Digit PIN", "safely and securely" and "Encrypted in transit. Your PIN is never stored in readable form", while there's no phone verification and no PIN reset.
- **Fix:** replace adjectives with proof (what's verified, what's locked, where to get help) and fix the two gaps above.

### 💅 Beta honesty on the payments path
- **Evidence:** payments aren't live, yet Home shows "Make your first top-up" as the final step of onboarding, and the Terms already promise 7-working-day refunds.
- **Fix:** while in beta, label the step "Coming soon" (disabled) and show a one-line beta banner so nobody tries to pay.

---

## What I could not test

- **Add child / link card, top-up, transaction history, daily limit:** my account has no linked child (the school has to add the number), and payments are not live. I saw the empty states only (`13-…`, `14-mobile-transactions.png`). I did not go near any payment confirmation.
- **Webhooks (`/webhook/yo`, `/webhook/yo/failure`, `/ussd/yo`):** unauthenticated by design in the spec. Please confirm signature or IP checks and idempotency, because these credit wallets. I didn't call them.
- **Real screen-reader testing, real-device 3G, repeat-visit caching.**
- **Mobile-UA caveat:** with the Android user agent, headless Chromium wouldn't activate Flutter's touch-based semantics switch, so the interactive captures use the same 360×740 @2x viewport with a desktop UA. The 3G performance run used the mobile UA.

## Test footprint (so you can clean up)

On the live beta I created **one account**: name "Roast Tester", phone `+256 700 000 099`, PIN in `work/creds.txt` (I chose a number I don't own; please remove it). I also caused 1 failed login (fake number `25612345`, 401) and 2 rejected registrations (422). Remove the account via Profile → Delete account (72-hour grace per the app). Nothing was deleted, paid, or sent to anyone.

---

## Fix these first (top 5)

1. 🔥 **Verify phone numbers with an SMS OTP at sign-up** (and for PIN reset). Today, whoever registers a number first owns it.
2. 🔥 **Turn off `/docs`, `/redoc`, `/openapi.json` and `/diagnostics/yo` in production** and fix the `localhost` root message.
3. 🔥 **Remove the Netlify badge** (it blocks *I accept* and two nav tabs and shows an ad), and move to a `nuvora.ug` domain.
4. 🔥 **Make Forgot PIN work**, and fix the sign-up bug where a 422 deletes the Create Account button (validate the phone client-side).
5. 🔥 **Cut the first-load cost** (self-host CanvasKit with immutable caching, defer rarely used screens, drop the emoji font fetches). 58 s on 3G is the whole product for half your market. Then add the missing security headers (`_headers`) in the same deploy.

*Artifacts:* `screenshots/` (27 images) · `lighthouse-mobile.report.html` · `work/` (scripts, `perf-3g.json`, `openapi.json`, terms text, header dumps).
