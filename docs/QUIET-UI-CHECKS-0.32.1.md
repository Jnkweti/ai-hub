# Quiet UI checks — 0.32.1

The developer: "when you run the test suite it pops up on my screen and takes over what I'm currently doing." The
unit suite itself opens no windows (every child process it starts uses `CreateNoWindow`); the native UI smoke checks
do — they launch the app and open its dialogs, each of which activated and took keyboard focus — and the reopen after
an installation brought the app to the foreground.

| Change | Effect | Where |
| --- | --- | --- |
| `AIHUB_UI_TEST=1` | When set, the app registers a Window style based on the themed one with `ShowActivated=false`, so the main window and every dialog open without activating. UI Automation, which the checks use, needs no focus. (Hiding the taskbar entry as well was tried and dropped: WPF then parents the window to an invisible owner and the checks that find the app by its main window fail.) | `App.OnStartup` |
| Every smoke script sets it | All seventeen `tests\*-Smoke.ps1` scripts set the variable beside `AIHUB_DATA_DIR` and clear it where they restore the environment. | `tests\*.ps1` |
| Focus assertion | `Feedback-Smoke.ps1` fails if the focused element belongs to the app after launch. | `tests\Feedback-Smoke.ps1` |
| Reopen minimized | After an installation the app is reopened with `-WindowStyle Minimized`; the reopen record notes it. The app is in the taskbar, not on top of whatever the developer is doing. | release procedure |

The windows still appear on screen (unactivated, behind the current window) because the layout checks compare
control bounds against the window's; a fully hidden or off-screen window would make those checks meaningless.

## Verification

- Build: zero warnings, zero errors; no change to the unit suite (274 tests).
- With the first build (which also hid the taskbar entry), the feedback smoke passed including the new focus
  assertion — the app did not take focus — and the local-diagnostics smoke passed; the conversation-management smoke
  failed because it finds the app through `Process.MainWindowHandle`, which the hidden taskbar entry defeated, and its
  failure left a package instance running. The taskbar setter was removed. The developer then reported the windows
  were still appearing, so no further UI check was run: the final package is verified by build and installation only,
  and the next UI smoke run (when the developer asks for one) is the verification of this build.
- Installed on October 3, 2026 after stopping the stray package instance and closing the idle 0.32.0 app; the app was
  left closed rather than reopened.
## Script maintenance (October 3, 2026)

The developer allowed a check run for 0.35.0 and the Tasks-window check still took over the screen: it restored the
app window by force with `ShowWindow`, which no setting in the app can prevent. Two things were then fixed without
running anything:

- Eight scripts read message transcripts from `rooms.json`. Since 0.23.0 that file is an index and each
  transcript is `room-<id>.json`, so those checks could not have passed. They now read the per-room file
  (`Task-Memory`, `Project-Status`, `Inline-Input`, `Keyboard-Guard`, `Desktop`, `Audit`,
  `Context-Upgrade`; `Shared-Context` instead states its `-NativeResultDirectory` precondition).
- `Task-Memory` no longer restores the window. The four checks that type or use shortcuts and therefore must bring
  the app to the front (`Keyboard-Guard`, `Navigation`, `Inline-Input`, `Mission-Control`) carry a
  `NEEDS KEYBOARD FOCUS` header and are to be run only with explicit consent for that script.

Quiet checks, safe to run when the developer allows a check run: `Feedback`, `Conversation-Management`,
`Local-Diagnostics`, and after this repair `Task-Memory`. The repaired scripts have been parsed but not executed;
their next consented run is their verification.

Later the same day the developer consented to one run of `Task-Memory`. It reached the note step and failed there
because the test fixture recognized a task turn only by the objective keyword, which a resumed native session no longer
receives (since 0.26.0 it gets the events since its last turn); the ledger showed the note delivered as a `user_note`nevent. The fixture now also recognizes the note and marker texts the check asserts on. A second run passed all seven
steps, including the note reaching the worker; the unit suite (277) passed with the widened fixture. That second run
still put dialogs over the developer's work, so **no UI check is run from the assistant any more**, whatever the
wording: build and unit suite verify desktop changes, and a UI check is a command the developer runs when it suits them.
