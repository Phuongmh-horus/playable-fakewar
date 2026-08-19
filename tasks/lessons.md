# Lessons

- After a user correction about intended behavior, update the implementation to cover all affected variants, not just the first target path mentioned.
- When preserving a future projectile behavior in Unity, keep the hook or reference code, but verify whether the user wants it active now or only retained as a commented example.
- For Unity API compatibility fixes, prefer the most broadly supported checks on `Component`/`Behaviour` (`enabled`, `gameObject.activeInHierarchy`) instead of assuming convenience properties exist on every type in this project setup.
- When throttling gameplay VFX/SFX in Unity, verify whether the limit should be "per frame" or "per N frames" and apply the same rule to every call path of that effect, not just one branch.
- For Luna/WebGL render fixes, avoid `Renderer.GetPropertyBlock` on paths already known to warn in Web builds; prefer clearing a reused `MaterialPropertyBlock` and writing only the properties needed for that renderer.
- When pooled VAT characters initialize, treat Idle as a pre-game state only; after gameplay starts, every spawn/reinitialize path must explicitly restore the army's current runtime animation.
- When reusing a VAT gameplay adapter across unit types, verify every shared `AnimationType` intent used by managers (especially `Move`) is mapped to a configurable VAT state and that the VAT asset default matches the required pre-game state.
- When a previous optimization is reverted, re-read the live source and validate each spawn, activation, and modifier path before reapplying a narrower replacement.
- For scene-seeded pooled armies, do not let a serialized unit list bypass runtime invariants: assign valid formation indices and attack timing to every scene unit, and prewarm the shared prefab pool independently of the scene-only seed flag.
