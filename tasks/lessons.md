# Lessons

- After a user correction about intended behavior, update the implementation to cover all affected variants, not just the first target path mentioned.
- When preserving a future projectile behavior in Unity, keep the hook or reference code, but verify whether the user wants it active now or only retained as a commented example.
- For Unity API compatibility fixes, prefer the most broadly supported checks on `Component`/`Behaviour` (`enabled`, `gameObject.activeInHierarchy`) instead of assuming convenience properties exist on every type in this project setup.
- When throttling gameplay VFX/SFX in Unity, verify whether the limit should be "per frame" or "per N frames" and apply the same rule to every call path of that effect, not just one branch.
