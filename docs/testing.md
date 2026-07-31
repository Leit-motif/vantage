# Testing the running shell

Most of this suite needs nothing from Windows. A handful of tests need everything from it: the
dashboard is an overlay whose whole job is described in Win32 terms — stay above other windows,
stay out of the taskbar, never take the keyboard, come back when the tray asks. A view model that
agreed to be topmost is not evidence that anything is, so those tests drive a real window with a
real `HWND` and read the answers back from the operating system.

That is worth keeping, and it is also how this suite once typed ten Notepad windows onto the
owner's desktop. This records how those tests run now, and — more importantly — what they no
longer prove.

## The hazard, and why checking first could not fix it

The tests delivered keystrokes with `keybd_event`. That call does not type at a window; it puts a
key into the session's input stream, and Windows routes it to whatever holds the foreground. The
tests knew this and checked that the dashboard held the foreground before pressing anything, but
the check cannot help: the owner can click somewhere between the check and the keystroke, and the
keystroke has no target to be wrong about. On 2026-07-30 a loop of runs did exactly that.

Classifying the damage afterwards — reporting a test inconclusive and naming the window that stole
the foreground — made the results honest, and left the hazard exactly where it was.

## What was measured

Two options were on the table: post messages to the dashboard's own window, or run the tests on a
private desktop. The second was preferred, on the grounds that it keeps the real Windows input path
that these tests exist to exercise. Measurement did not support that.

A low-level keyboard hook on the interactive desktop, calibrated against a control injection it
demonstrably does see, was used as a witness. Repeated three times:

| Question | Answer |
| --- | --- |
| Does `keybd_event` from a thread on a private desktop reach the interactive desktop? | No — the witness recorded nothing |
| Does the private desktop's own window receive it? | No — no `WM_HOTKEY`, no `WM_KEYDOWN` |
| Is there a foreground window on a private desktop? | No — `GetForegroundWindow` returns 0, `SetForegroundWindow` fails |
| Does `SetActiveWindow` work there? | Yes — a real `WM_ACTIVATE` is delivered |
| Does a posted `WM_KEYDOWN` work there? | Yes — it arrives on the window's queue |

So a private desktop does contain the hazard, and contains it by dropping the input on the floor. A
desktop that is not receiving input has no raw input thread to route a synthetic keystroke and no
foreground window for it to be routed to. Isolation alone does not preserve the real input path; it
removes it.

The two options are therefore not alternatives, and the suite uses both.

## How it runs now

**A desktop of its own.** `IsolatedDesktop` creates one per process and starts the thread that owns
every test window on it. Nothing the suite shows can appear on the owner's screen or take
activation from their work, and nothing they do can disturb a run.

Placing that thread is more awkward than it sounds, and the code says so at length because the
constraint is invisible otherwise. Windows refuses to move a thread that already owns a window; a
thread declared STA is inside its apartment — hidden OLE window included — before its own first
instruction runs; and the runtime will not change a thread's apartment after it has started. The
thread is therefore created by `CreateThread` rather than by the runtime, which is the only way to
get the two steps in the order that works: desktop first, then `CoInitializeEx`. The runtime reads
the apartment back off the operating system afterwards and agrees the thread is STA, which is all
WPF requires.

**Keystrokes with a target.** `TheKeyboard` posts `WM_KEYDOWN`/`WM_KEYUP` to one named window, and
refuses to post anything at all from a thread that is not isolated. A key on a window's own queue
can reach that window and nothing else.

**A window to compete with.** `TheOtherWindow` stands in for whatever the owner was doing. Every
claim about the overlay refusing focus is really a claim about two windows — that the one already
holding the keyboard keeps it — and a fresh desktop has no second window to make that claim
against.

**No way back to the old hazard.** `KeyboardSafetyTests` fails if any input-injection call is ever
linked into the test assembly again, and if the host thread is ever found sharing the input
desktop.

## What is no longer proven

This is the part worth being blunt about. The tests are safe now, and they prove less than they
did.

- **Windows delivering a registered hotkey.** The registration is still real — the shell claims
  `Ctrl+Shift+F9` against its live `HWND` and Windows accepts or refuses it. The press is not:
  Windows delivers a hotkey from the input desktop, which the suite deliberately is not on, so the
  `WM_HOTKEY` that a press would have produced is posted directly.
- **The raw input thread.** Scan codes becoming virtual keys, and the journey from hardware into a
  window's queue, are not exercised. Everything above the queue still is: WPF reads these messages
  through the same `HwndSource` hook, and its input manager, keyboard focus, tab navigation and
  command routing all run unchanged.
- **Windows granting the foreground.** The shell does ask for it, and a desktop with no input queue
  refuses, so the activation that would have followed is performed by the test. What the shell does
  in response to being activated is still entirely its own.

Against that, one thing is proven that was not before. The overlay used to ask Windows for
activation every time it was shown, and relied on being refused — which holds only while it is not
the foreground process. On a desktop where nothing else competed it took the keyboard. It is now
`ShowActivated="False"`, so it does not ask, and the test can see the difference.

## Interference

There is none left to classify. An earlier attempt at this problem reported a test inconclusive
when another window stole the foreground mid-run, and named the process that took it. That
distinction was right for tests running on the owner's desktop, and is meaningless on a desktop the
owner cannot reach: these tests are deterministic now, so a failure is a failure. They run in the
default `dotnet test` alongside everything else, and CI runs one suite rather than two.
