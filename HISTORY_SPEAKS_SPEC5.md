# History Speaks VR — Spec 5: XR & UX Polish (3 fixes)

Three fixes to the on-device experience before we replicate to more characters. **This is a diagnose-then-fix spec**: the XR rig, scene layout, and interaction system are not fully known to the spec author, so Claude Code must **investigate and report findings first (Step 0), then implement**. Do not guess at the rig structure — inspect it.

**Out of scope / do not touch:** the `server/` backend, `ProxyClient` request logic, the recording logic (`AudioRecorder` 12s/4s), `WavUtility`, the crisis/safety behavior, and metering. This is scene + client-UI + XR-rig work only.

---

## Step 0 — Investigate and report BEFORE any changes

Report the following so we choose the right mechanism (paste findings to the user, then proceed):
1. **XR rig type:** Is the project using Unity's **XR Interaction Toolkit** (an `XR Origin` component) or **Meta XR SDK** (`OVRCameraRig` / `OVRManager`), or both? Name the GameObject(s) and components that form the camera rig.
2. **Tracking origin mode:** What is the current tracking origin mode (Floor / Device / not set)? Where is it configured?
3. **Spawn logic:** Is there any existing code that positions/rotates the rig on scene load, or is the rig just placed statically in the scene? What direction does the user face at spawn relative to the auditorium (they report spawning facing the *audience*, with the interactive Main Board on a wall they are NOT initially facing)?
4. **Interaction method:** How are the UI buttons (Record, Submit, New Question, and the green "start" button on the Main Board) interacted with — raycast/ray-interactor pointer, direct poke, or gaze? What component drives it?
5. **Button world positions:** The green start button's world position/height on the Main Board, and the Main Board's position relative to the spawn point.
6. **The typing-mode UI:** Confirm `PromptUIController` (or equivalent) currently controls the Submit button's interactable state via the input field's `onValueChanged`, as established in prior work.

---

## Fix 1 — Enforce spawn height, position, and facing direction (the most important fix)

**Problem:** When the user removes the headset and puts it back on, their height changes (often spawning too high, with the virtual auditorium floor appearing above the real floor) and they face a different real-world direction than they started. This is a tracking-origin / recenter issue.

**Goal:** On scene load AND whenever the headset is re-mounted / input focus is regained, place the user at a fixed spawn point, facing a fixed direction (toward the audience/intro board as designed), with the virtual floor matching the real floor.

**Standard mechanism (adapt to the rig found in Step 0):**
- **Tracking origin = Floor.** Set the tracking origin mode to Floor (XR Origin: "Floor" origin mode; or via `XRInputSubsystem.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor)`). This aligns the virtual floor with the real floor and preserves the user's real standing height — directly addressing "virtual floor above real floor." Remove any manual fixed camera-height offset that conflicts with room-scale tracking.
- **Create a `SpawnPoint` transform** in the scene at the intended standing position, rotated to face the intended direction (toward the intro board).
- **Recenter routine:** compute the delta between the current camera pose (projected onto the floor plane — XZ position and yaw only, ignore pitch/roll and let Floor mode own Y) and the `SpawnPoint`, then apply that delta to the rig/XR Origin so the camera ends up at the spawn point facing the target direction. Do NOT force Y/height artificially — Floor mode handles it; the fix for height is using Floor mode correctly, not hardcoding a number.
- **Triggers:** run on scene start, and subscribe to the headset re-mount / focus event so "take headset off → put back on" recenters automatically:
  - Meta XR SDK: `OVRManager.HMDMounted` (and/or `InputFocusAcquired`).
  - XR Interaction Toolkit / general: `Application.onBeforeRender` is wrong for this — use `OnApplicationFocus(true)` and/or the XR device's recenter callback.

**Acceptance:** After spawning, taking the headset off, walking, and re-mounting, the user is returned to the same spawn position, facing the same direction, at a natural height with the virtual floor on the real floor.

> Note: report which trigger events are actually available in this project's SDK setup, and use those. If both XRITK and Meta SDK are present, prefer the Meta SDK mount events on Quest.

---

## Fix 2 — Make the green button reachable/visible ON the Main Board (KEEP the turn-to-enable behavior)

**CRITICAL — do NOT change this behavior:** The intended flow is that the user spawns facing the audience, and the green button on the Main Board **only enables when the user physically turns around to face the board**. This turn-to-enable logic is deliberate and must be preserved exactly as-is. Do not move the button/panel into the spawn-facing view, do not make it enable at spawn, and do not alter the facing-detection logic that gates it.

**Problem (the ONLY thing to fix here):** Once the user has turned to face the Main Board and the button is enabled, the green button sits so low that the hand/controller must drop below the field of view to press it — uncomfortable and hard to hit.

**Goal:** Keep the button exactly where it is in the flow (on the Main Board, enabled only after turning to face it), but make it comfortably reachable and visible once faced — i.e. adjust its **height/placement on the board**, not its facing gate.

**Approach:**
- **First, evaluate shortening the panel's intro text (preferred if the layout allows).** If the button's vertical position is driven by the amount of text above it (e.g. the button flows below the text in a vertical layout group, rather than being anchored to a fixed point/bottom edge), then **reducing the intro copy will naturally raise the button** — the cleanest fix, no layout surgery. Determine from the Step 0 layout inspection whether this is the case. If it is, tighten the intro text to a short, punchy VR-appropriate version (a couple of concise lines, not a paragraph — people don't read long text comfortably in a headset), which both raises the button and improves readability. Propose the shortened copy to the user before finalizing.
- **If the button is anchored to a fixed position** (so text length doesn't move it), raise the green button's vertical position on the Main Board directly to roughly chest/eye height, within natural reach and field of view when facing the board.
- Either way: do NOT change the enable condition, the facing gate, or the board's location.
- Interaction with Fix 1: if the height fix corrects an over-high spawn, the button will already sit lower in the user's view. So implement Fix 1 first, verify reachability on-device, THEN apply the text-shortening and/or button-height adjustment only as much as still needed.

**Acceptance:** The turn-to-enable behavior is unchanged (button still activates only when the user turns to face the Main Board). Once faced, the user can see and press the green button without dropping the hand below the field of view or crouching.

---

## Fix 3 — Don't leave both Record and Submit enabled while typing

**Problem:** When the user types a question, both the Record (mic) and Submit buttons are active, which is confusing — they are two different input modes.

**Goal:** One clear input mode at a time.
- When the input field **contains text** → the user is typing: **disable the Record button** (and keep Submit enabled).
- When the input field is **empty** → **disable Submit** (already implemented) and **enable Record**.
- Preserve all existing locking: while recording or while a request is processing, both stay disabled per the current `SetButtonsInteractable` / `isProcessing` logic. This new rule only governs the idle state based on field contents.

**Mechanism:** extend the existing `PromptUIController` `onValueChanged` handler that already toggles Submit — add the inverse toggle for Record. Do not duplicate logic across `VoiceRecognitionController` and `PromptUIController`; keep one source of truth for the empty/non-empty rule. Ensure the voice flow's re-enable path respects it (after a voice turn, Record re-enables only because the field is empty).

**Acceptance:** Typing text greys out Record; clearing the field re-enables Record and greys Submit; recording/processing still disables both.

---

## Constraints (unchanged)
No changes to `server/`, `ProxyClient` request logic, `AudioRecorder`/recording logic, `WavUtility`, crisis/safety behavior, or metering. Scene, XR rig, and client UI only.

## Verification
- Unity batch compile passes (Editor closed): zero `error CS`, "Exiting batchmode successfully".
- Final validation is on-device (and Editor-via-Link for the UI-mode fix). Note in the report which items can only be verified on the Quest (Fix 1 and Fix 2 are on-device; Fix 3 is verifiable in the Editor).

## Kickoff instruction (paste into Claude Code)

> Read HISTORY_SPEAKS_SPEC5.md in the repo root. FIRST do Step 0 — investigate the XR rig, tracking origin, spawn logic, interaction method, and button positions, and report your findings to me before changing anything. THEN implement the three fixes, adapting the mechanisms to what you actually found, and show me each change before moving on. Respect the constraints (do not touch server/, ProxyClient request logic, AudioRecorder/recording logic, WavUtility, or safety/metering). Finish with a passing Unity batch compile and tell me which fixes need on-device verification.
