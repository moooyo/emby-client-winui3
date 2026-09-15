# Authentication and account UI review

Date: 2026-09-15. Scope: read-only review of the current working tree and existing native screenshots. No production files were changed, no tests or builds were run, and this reviewer did not operate the native application.

## Evidence and limits

- Source: `src/EmbyClient.App/MainPage.xaml`, `MainPage.xaml.cs`, `MainPage.Accounts.cs`, `Services/ConnectionService.cs`, and `Services/AccountStore.cs`.
- Native images inspected: `artifacts/ui-implementation-verification/native-20260914/screens/01-saved-account.png` and `02-password-form.png` (1268 x 834 pixels).
- Follow-up native evidence supplied by the primary reviewer: `artifacts/ui-ux-review-20260915/native/17-session-expiry-return.png` and `.txt`. The review proxy returned one HTTP 401 while returning from the player triggered a library refresh; it did not revoke the synthetic server token. The app returned to sign-in with the correct account selected, the saved continuation action disabled and labeled Sign-in required, the password form expanded, and an expiry warning. This establishes the active-session expiry path, not failed initial saved-account restoration.
- The two account images are from the native verification cycle. They are not in the verification record's final 16-image regression set. Their appearance is useful evidence for the displayed states, not proof of every final-build account interaction.
- `docs/implementation/verification/ui-refinement-native-20260914.json` explicitly limits native and accessibility coverage. The aggregate 510-test result is not evidence of keyboard focus, Narrator announcements, error recovery layout, or all authentication states.
- “Confirmed” below means the described source path or displayed condition is established. It does not mean that every conditional failure was reproduced natively.

## Overall conclusion

Keep the native controls, the focused sign-in column, the saved-account-first primary action, and the distinction between switching, signing out, and removing a local account. The current wide-window sign-in screens are coherent and visually restrained. They do not need a decorative hero or additional background imagery.

The largest gaps are recovery semantics and focus continuity rather than visual styling. An unusable saved token remains an offered continuation path; settings failures have no recovery action; several asynchronous outcomes do not specify a focus destination. Modernization should simplify the first-run form and account management without weakening the existing native controls or session guards.

## Conclusions for every page and state

| Page or state | Conclusion | Evidence and remaining limit |
| --- | --- | --- |
| Initial settings load | Improve feedback and recovery. All controls start disabled; this state has no dedicated progress presentation. | `MainPage.xaml.cs:45`, `70-87`, `201-218`. Slow local storage was not observed. |
| First run, no saved accounts | Keep the narrow native form and clear headers. Remove the redundant expandable section when this is the only available sign-in route. | `MainPage.xaml:17-52`; `MainPage.xaml.cs:101-103`. First-run-only screenshot is missing. |
| Saved account with usable token | Keep as the default route. One primary continuation action and a collapsed alternative are appropriate. Improve account identity disambiguation. | Native `01-saved-account.png`; `MainPage.xaml.cs:90-115`; findings A2 and A3. |
| Saved account without token | Needs a clearer state. A disabled button labeled “Sign-in required” expresses a status as an unavailable action, above a separately expanded password form. | `MainPage.Accounts.cs:16-20`; `MainPage.xaml.cs:113`, `133-134`. Prefer a small explanatory status and the password action. |
| Manual sign-in for an existing account | Keep native input headers, password entry, and the single accented Sign in button. The saved-account continuation action correctly loses accent when the form opens. | Native `02-password-form.png`; `MainPage.Accounts.cs:20`; `MainPage.xaml:37-49`. Error layout and short windows remain unobserved. |
| Manual sign-in for a new account | Functional source path is present. The extra expander and long introductory stack can be simplified for this route. | `MainPage.Accounts.cs:23-34`; `MainPage.xaml:17-52`. Preserve server and username labels. |
| Connecting | Keep the native small ProgressRing, status text, and explicit Cancel. Verify that all three remain visible after submission in a short, scrolled window. | `MainPage.xaml:31-35`; `MainPage.xaml.cs:155-159`, `201-223`; risk R2. |
| Connection cancelled | Cancellation reaches the request token and duplicate connections remain guarded. No success/error banner is needed for an intentional cancel. Add a predictable focus return and cancellation presentation if request teardown is slow. | `MainPage.xaml.cs:149`, `186`, `192-197`, `223`. Native focus destination is not established. |
| Invalid address or empty username | Retain inline errors, input HelpText, and focus on the first invalid input. This is one of the stronger recovery patterns in the current flow. | `MainPage.xaml.cs:225-239`; `MainPage.xaml:40-44`. Narrator live-region behavior still needs native acceptance. |
| Password rejected | The global message is understandable, but it does not distinguish a new password rejection from an expired saved token. Return focus to the password field and associate the error with the form. | `ConnectionService.cs:216-217`; `MainPage.xaml.cs:187-197`. Password is cleared on every failure. |
| Saved token expired or cannot be decrypted | Revise. The form opens, but the old Continue action remains enabled and can repeat the unusable-token path. | Finding A2. No invalid-token native screenshot exists. |
| Server unreachable, timeout, or protocol failure | Keep differentiated network/protocol messages and preserved server/username. Do not invalidate a saved token merely because connectivity failed. The automatic password clearing adds retry cost. | `ConnectionService.cs:220-225`; `MainPage.xaml.cs:187-197`. Preserve the distinction when fixing A2. |
| Disabled account or policy restriction | Parental-control messaging is explicit and can remain. Disabled-user handling loses its actionable reason and should be corrected. | Finding A4; `ConnectionService.cs:214-219`. |
| Settings unavailable or corrupt | Revise before calling recovery complete. Users cannot retry, locate the settings, or choose a documented recovery path from the application. | Finding A1. |
| Signed in with persistence warning | Keep allowing use of the authenticated session and showing a warning. Match account-menu availability to whether a removable account record actually exists. | `ConnectionService.cs:60-78`; `MainPage.xaml.cs:184`; finding A6. |
| Switch account | Keep token-preserving behavior and focus on the chooser. The current session closes immediately; cancelling the account-selection intention requires continuing with the previous account again. | `MainPage.xaml.cs:367-371`, `374-439`. Consider a reversible chooser in a later interaction redesign. |
| Add account | The form is cleared and the server field receives focus. This is internally consistent, but it also immediately leaves the existing session. | `MainPage.Accounts.cs:23-34`. A future account chooser could distinguish selection from committing the switch. |
| Sign-out confirmation, cancelled | Keep a native ContentDialog, explicit Cancel, and Cancel as the default button. Focus returns to Account and settings. | `MainPage.Accounts.cs:36-51`, `90-105`. Escape and Narrator were not observed. |
| Sign-out confirmation, accepted | Keep the explicit password consequence and separate local removal/server revocation. Improve partial-failure feedback before claiming the sign-in was removed. | `MainPage.xaml.cs:398-424`; finding A5. |
| Remove saved account, cancelled | Keep native confirmation and safe default. The account remains unchanged; focus goes to the chooser or account menu. | `MainPage.Accounts.cs:65-105`. Native dialog layout has not been captured. |
| Remove saved account, accepted | Keep local-only wording and atomic persistence before publishing the new list. The last removed entry correctly leads to an empty form and the server field. | `MainPage.Accounts.cs:74-87`; `ConnectionService.cs:161-178`. Unit coverage of storage is not dialog acceptance. |
| Remove the current account | Correctly disconnects before removal and explains that server media remains. Hide or disable the command when no local record exists. | `MainPage.Accounts.cs:59-74`; finding A6. |
| Session expired during library or player use | Retain the observed recovery behavior: returning from playback with a refresh HTTP 401 leads to sign-in, retains the correct account, disables Continue as Sign-in required, expands the form, and explains expiry. Password is empty. An explicit focus destination is still needed. | `MainPage.xaml.cs:374-452`; native `17-session-expiry-return.png/.txt`; risk R1. The controlled one-response HTTP 401 does not prove actual server-side token revocation. |
| Appearance menu in the account footer | Keep System/Light/Dark native radio items and the protected asynchronous save. No extra theme control is necessary in the first-run form unless pre-login customization becomes a requirement. | `MainPage.xaml:91-97`; `MainPage.xaml.cs:455-501`. Theme coverage for account dialogs is incomplete. |

## Confirmed findings

### A1 — P2: Settings failure leaves an unrecoverable sign-in screen

- Trigger: account settings are corrupt, unreadable, or temporarily inaccessible during `OnLoaded`.
- Evidence: `MainPage.xaml.cs:72-85` sets `_initialized = true` before loading and leaves `_settingsReady = false` after failure. `201-210` disables all account selection and manual sign-in controls. `MainPage.xaml:14` supplies only a closable InfoBar. `Services/AccountStore.cs:291-295` asks users to restore/reset credentials without a corresponding UI action.
- Impact: even a transient problem requires leaving the application; a nontechnical user is given no concrete way to recover. Dismissing the InfoBar leaves a disabled form with no visible explanation.
- Recommendation: provide an explicit retry for temporary storage errors and an intentional repair/help path for corrupt settings. Preserve the damaged file; do not silently overwrite it or pretend an in-memory session is safely persisted.
- Verification: source-confirmed dead end; no settings mutation or failure injection was performed in this review.

### A2 — P2: Failed saved credentials remain an offered continuation path

- Trigger: restore returns an authentication failure or Windows cannot decrypt the saved token.
- Evidence: `MainPage.xaml.cs:187-197` opens the manual form after failure but does not record the selected token as unusable. `133-134` enables Continue solely from the presence of a protected-token string. `MainPage.Accounts.cs:16-20` therefore still labels it Continue. `ConnectionService.cs:98-114` does not invalidate the failing credential.
- Impact: the primary recovery screen continues to offer an action that repeats the same known failure, while requiring users to infer that password entry is now necessary.
- Recommendation: distinguish authentication/decryption failure from network failure. Mark the selected account as requiring password authentication for the former, show a short explanation, and focus Password after the layout opens. Keep Continue available for a transient network error.
- Verification: source-confirmed transition and gating; native initial-restore failure/focus outcome remains unobserved. The later native active-session expiry check follows `LeaveSessionAsync(..., sessionExpired: true)` and successfully disables Continue; it is a different path and does not establish A2 natively.

### A3 — P2: Same-name saved accounts cannot be distinguished

- Trigger: two different servers use the same display name and contain accounts with the same username.
- Evidence: `MainPage.xaml.cs:95-99` uses only `UserName + ServerName` for ComboBox item content. Neither the option nor a nearby selected-account summary exposes the address. `MainPage.xaml:22` contains no richer item template.
- Impact: the user cannot reliably choose the intended server before continuing or opening the remove-account confirmation, whose text uses the same names.
- Recommendation: include a readable host/address as secondary account identity and in removal confirmation. Preserve a compact selected state; full URLs can be available in a tooltip or details line.
- Verification: source-confirmed ambiguity for valid duplicate names, not a native duplicate-account scenario.

### A4 — P2: A disabled-account failure is converted to “Try again”

- Trigger: server user policy returns `IsDisabled = true` after authentication or restore.
- Evidence: `ConnectionService.cs:194-199` throws `InvalidOperationException("This Emby account is disabled.")`, but `UiErrors.Describe` at `212-227` has no matching case and returns a generic retry message. The separate parental-control case is correctly handled.
- Impact: the UI invites repeating a request that cannot fix an administrative account restriction.
- Recommendation: use a typed account-policy outcome with a safe, actionable explanation, such as selecting another account or contacting the server administrator. Do not expose arbitrary exception text globally.
- Verification: source-confirmed message mapping; not reproduced against a real account.

### A5 — P2: Combined sign-out failures hide an important remaining condition

- Trigger: local token removal cannot be saved and server token revocation also fails.
- Evidence: `ConnectionService.cs:152-158` clears the in-memory token before saving; a failed atomic save retains the previous file. `MainPage.xaml.cs:413-422` suppresses the revocation warning whenever `localSignOutError` exists and then shows only that local error. `AccountStore.cs:208-237` preserves the destination on failure.
- Impact: the visible UI has left the session, but a reusable token may remain on disk and on the server. A generic storage warning does not clearly communicate this combined result or how to resolve it.
- Recommendation: present one explicit partial-sign-out result with a retry/remediation action. Keep safe in-memory disconnection, but do not imply persisted credentials were removed until that operation succeeds.
- Verification: source-confirmed conditional failure behavior. No settings write failures or logout failures were injected.

### A6 — P3: Removing an unsaved current account silently does nothing

- Trigger: token protection fails before a new account record is added, but sign-in is allowed with a persistence warning; the user later chooses Remove account from this device.
- Evidence: `ConnectionService.cs:63-77` can return an authenticated session without adding a new record. `MainPage.xaml:113` always includes the command. `MainPage.Accounts.cs:59-62` returns without any feedback when no matching record exists.
- Impact: an enabled command appears broken, especially to a user already dealing with a persistence warning.
- Recommendation: derive the command's visibility or enabled state from the existence of a saved record, or provide a concise explanation that this account was not saved. Sign out remains the active-session action.
- Verification: source-confirmed branch; native persistence-failure scenario not exercised.

## Design opportunities, distinct from defects

1. **P3 — Simplify first-run sign-in.** `MainPage.xaml:17-52` uses the same expander composition even without saved accounts. Show a direct form for first run and reserve disclosure for the genuinely alternative route. Keep the native 14-DIP control typography and existing title style; no bespoke form styling is required.
2. **P3 — Reduce account-management prominence in the sign-in action row.** The trash icon is adjacent to the primary Continue button in `MainPage.xaml:25-28` and in the saved-account screenshot. Move this low-frequency command to a named account-management action or menu while retaining the confirmation. This is a hierarchy choice, not a claim that the current layout is unsafe.
3. **P2 — Keep authentication error and recovery visually connected.** Field validation is already inline. Network/authentication errors use a full-window InfoBar (`MainPage.xaml:14`, `MainPage.xaml.cs:511-515`) far from the centered form. A form-level error area with an explicit recovery action would reduce search distance; application-wide notices can remain global.
4. **P3 — Avoid repeated password entry on transient failures.** `MainPage.xaml.cs:194` clears Password on cancellation and all failures. Consider retaining it only during an active manual retry after a transport failure, while still clearing it on successful sign-in, account change, explicit sign-out, or dismissal. This needs a product decision, not an automatic change.
5. **P3 — Make account selection reversible.** Switch and Add currently leave the active session before the user chooses another account. A future chooser could let users inspect/add accounts and cancel back to the current library. This changes interaction structure and should be reviewed separately from cosmetic changes.

## Native acceptance risks still open

- **R1 — P2, focus continuity:** `ConnectAsync` has no focus restoration after cancellation, password rejection, or restore failure (`MainPage.xaml.cs:186-197`); `SessionExpired` has no focus call after the form reopens (`449-452`). In the follow-up active-session expiry screenshot, Password has no visible focus indication and the supplied state dump reports no focused element. This supports the missing-focus concern but does not establish Narrator failure or a keyboard trap. Initial settings load also does not set an initial focus target. In contrast, Switch, Add, Sign out, and Remove explicitly restore focus. Complete keyboard-only acceptance for the other outcomes.
- **R2 — P2, short-window busy feedback:** the status/Cancel row is above the full manual form inside the same ScrollViewer (`MainPage.xaml:16-49`). When Sign in is pressed near the bottom in a 640 x 600-DIP window or with large text, the status may be above the viewport. Confirm visibility and reachability during a deliberately delayed request.
- **R3 — P2, assistive technology:** inline error LiveSetting/HelpText and the connection status live region are present. Narrator announcement order, ContentDialog naming, default/escape actions, and focus after dialog dismissal have not been accepted natively.
- **R4 — P2, long identity and text scaling:** use a long server URL, long account/server names, 200% text, both themes, and high contrast. Verify ComboBox identity readability, button wrapping, expanded form scroll reachability, and confirmation dialog content. Default-size light-theme screenshots are insufficient evidence.

## Suggested review order before implementation

First confirm the asynchronous and error states with the native application, especially invalid saved credentials, cancellation, and session expiry. Resolve A1-A5 as behavior and messaging decisions. Then simplify the no-saved-account layout and management hierarchy. Preserve the currently good native form controls, inline field validation, guarded account transitions, safe confirmation default, and local-only removal semantics.
