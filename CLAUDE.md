# History Speaks VR

AI-powered VR app (Meta Quest 3, Unity 2022.3.32f1, C#) where students aged 13–19 hold voice conversations with historical figures (currently Albert Einstein). Sold to schools; safety and cost-per-session are first-class concerns. Specs for major work arrive as `HISTORY_SPEAKS_*.md` files in the repo root — when one is referenced, read it fully before coding.

## Layout
- Unity project at repo root (`Assets/`, scene: MainScene).
- `server/` — Node 20 + Express backend proxy on Cloud Run + Firestore (Spec 3). Independent from Unity; has its own package.json.

## Protected — do not modify unless the task explicitly says so
- `AudioRecorder.cs` and `WavUtility` — recording logic (12s cap, 4s silence auto-stop, 16kHz) is tuned and tested on-device.
- The request-lock (`isProcessing`) pattern in AIResponseControllerV2.
- Client-side `SafetyFilter` behavior and the crisis-message wording (pending legal/school review — flag, never reword).

## Hard rules
- No API keys or secrets in source, logs, or comments — server keys live in Cloud Run env vars; the device holds only proxyBaseUrl + a revocable deviceToken.
- Student audio is never persisted or logged anywhere. Conversation text only where Spec 3 §4.8 allows (sessions w/ TTL; flagged snippets in events).
- Character/persona content lives server-side (`characters/` in Firestore); don't reintroduce persona or provider calls into the Unity client.
- LLM replies: ≤40 words, plain text (no markdown — output is spoken via TTS).
- Quest 3 is Android: environment variables don't exist at runtime; runtime-added components have empty SerializeFields (scene components only for Inspector-set values).

## Verification
- Unity compile (Editor must be CLOSED first):
  `"/c/Program Files/Unity/Hub/Editor/2022.3.32f1/Editor/Unity.exe" -batchmode -quit -projectPath "C:/Users/danie/Documents/GitHub/LearnHistoryThroughAI-VR" -logFile compile.log`
  Pass = zero `error CS` and "Exiting batchmode successfully".
- Server: `npm install` clean + smoke test passing (`server/scripts/smoke.js`).

## Workflow
- Show the plan per numbered spec step before implementing it; show each file edit before moving on.
- On-device behavior (mic, keyboard, permissions) differs from the Editor — treat Editor success as provisional until Quest-tested.
