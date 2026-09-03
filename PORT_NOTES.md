# PiP-Disabler — SPT 4.1.3 port notes

Ported from Fiodorwellfme's [PiP-Disabler](https://github.com/Fiodorwellfme/PiP-Disabler) (MIT
licensed, original 4.0.13 build v1.5.0). Original license preserved in `LICENSE`.

**This has not been compiled or tested against a real 4.1.3 client.** I don't have your
`Assembly-CSharp.dll` to check names against, so everything below is source-level work: the
build retargeting and the two concrete bug fixes are things I'm confident in; everything else
is the original author's logic carried forward unchanged, flagged by risk so you know where to
actually spend dnSpy time instead of re-checking all ~2,900 lines blind.

## What actually changed

1. **`PiPDisabler.csproj`** — retargeted from the original's `..\..\` relative-path assumption
   to an absolute `SptRoot` property (default `E:\EFT\Tarkov`), matching the pattern from the
   ConsistentReticle port. Also updated the `PostBuild` copy target the same way.
2. **`Patches/ReticleRenderer.cs` — `GetActiveAspect()`** — the 4:3-stretched-aspect bug you
   flagged. Was deriving aspect from `cam.pixelWidth/pixelHeight` or `Screen.width/height`,
   neither of which reflects EFT's in-game "Aspect Ratio" setting (that setting overrides
   `Camera.aspect` directly to skew the projection matrix, without changing the actual
   backbuffer size — so those two properties never see it). Now reads `cam.aspect` directly.
3. **`Patches/ScopeEffectsRenderer.cs` — `ApplyOutsideBlurRadialGate()`** — same blind spot,
   one level over: the vignette/blur radial gate's roundness correction had the identical bug.
   Fixed the same way. Left the rest of that file's `viewport` Rect handling untouched, since
   that's genuine pixel-buffer sizing for the blur render targets, not an aspect calculation.
4. **Plugin version bumped** to `1.5.0-4.1.3port` so it's distinguishable from the original in
   logs and the BepInEx plugin list.

Everything else — all mesh-cutting, FOV substitution, reticle rendering logic, compat shims —
is the original source, unmodified.

## Why partial breakage won't be silent

`Patcher.cs` wraps every patch in `SafeEnable<T>`, which try/catches and logs
`[Patcher] Failed to enable <PatchName>: <message>` rather than crashing the whole plugin if one
patch's target can't be found. **Start here when you test:** launch, check the BepInEx log for
any `Failed to enable` lines, and that gives you a direct, specific list of exactly which
patches broke — not a mystery crash to debug from scratch.

## Verification checklist, by risk

### High priority — obfuscated-style identifiers (`GClassNNNN` / `method_N`)

These are the ones actually likely to have moved. SPT 4.1.0's deobfuscation pass restored real
names across most of the client; anything still referenced by an auto-generated name in this
4.0.13-era codebase is exactly what that pass would have renamed. Find the real name for each
in dnSpy and update the `AccessTools` call.

| Identifier | Where | Used for | Notes |
|---|---|---|---|
| `GClass3687` | `VanillaOpticSuppressionPatches.cs` (8 refs) | Suppressing vanilla PiP camera setup | Given the patch names (`OpticCameraManagerEnableOptic_NoPipPatch`, `OpticCameraManagerSetResolution_NoPipPatch`) and our earlier reticle work, this is almost certainly `OpticCameraManager` itself — the same class `CameraManager.Instance.OpticCameraManager` we found for the reticle mod. High confidence, but confirm before assuming. |
| `CameraClass.method_10` | `VanillaOpticSuppressionPatches.cs` (2 refs) | Suppressing optic-enabled camera behavior | `CameraClass` itself kept its name; only this specific method is still obfuscated. |
| `ProceduralWeaponAnimation.method_23` | `ScopeLifecycle.cs` (2 refs), `PWAMethod23Patch.cs` (2 refs) | Something in the weapon-animation lifecycle tied to scope state | Referenced from two separate files — this is a central hook, worth prioritizing. |
| `TacticalRangeFinderController.method_0` | `ScopeDetectionPatches.cs` | Range-finder scope detection | |
| `GClass1673` | `ScopeDetectionPatches.cs` | Calls `SetMonospaceText` — likely a UI text-display helper | |
| `NewRotationRecoilProcess.method_3` | `RecoilReturnToZeroPatch.cs` | Recoil return-to-zero | |

### Medium priority — string-literal method names on real (named) classes

The containing type is a real, named class (low risk of the type itself moving), but the method
name is passed as a bare string rather than `nameof(...)`, so it's not compile-checked — a
mismatch here surfaces as a `SafeEnable` log line at runtime, not a build error.

- `OpticSight`: `"OnEnable"`, `"OnDisable"`
- `TacticalRangeFinderController`: `"OnEnable"`, plus fields `_distanceOutputFormat`,
  `_noDistanceText`, `_textOnDisplay`, `_boneToCastRay`, `_rayStartOffset`, `_maxCastDistance`,
  `_mask` (these already look like real, human-written names rather than generated ones —
  lower actual risk than the table above, but still string-based, so still worth a quick check)
- `Player`: `"SetInventoryOpened"`, `"OnSetInHands"`
- `Player.FirearmController`: `"SetScopeMode"` (with an explicit overload signature —
  `FirearmScopeStateStruct[]` — double-check that parameter type still matches too),
  `"ChangeAimingMode"`
- `OpticComponentUpdater`: `"LateUpdate"`
- `ProceduralWeaponAnimation`: field `_tacticalReload` (`ScopeLifecycle.cs`)

### Low priority — self-verifying (nameof-based, or defensively coded)

These will either fail to *compile* if broken (immediate, unambiguous signal) or were already
written defensively by the original author:

- All `nameof(...)`-based `AccessTools.Method` calls (`Spring.AddAcceleration`,
  `CameraLodBiasController.SetBiasByFov`, `OpticComponentUpdater.CopyComponentFromOptic`,
  `OpticSight.LensFade`, `Camera.Render`, `GPUInstancerManager.Update`,
  `FirearmsAnimator.SetFireMode`/`ModToggleTrigger`, `BetterSpring.ApplyVelocity`, `Player.Look`)
  — if any of these classes/methods were renamed, the project won't build, full stop.
- `FovController.cs`'s `DiscoverSightModVisualControllers()` already tries multiple candidate
  type-name strings in sequence and falls back to a property-based search rather than trusting
  one hardcoded name — this was already written to survive exactly this kind of rename, so I'd
  deprioritize it.
- `FOVFixCompat.cs` / `FikaCompat.cs` / `DERPCompat.cs` only activate if those *other* mods are
  installed, and target those mods' own class names, not EFT internals — irrelevant to this
  port unless one of those specific mods is in your list.

## Suggested test order

1. Build against `E:\EFT\Tarkov`'s 4.1.3 assemblies (or your actual install path if it's since
   changed).
2. Fix whatever `CS0246`/`CS1061` compile errors come up first — that's the low-priority list
   resolving itself.
3. Launch, grep the BepInEx log for `Failed to enable` — that's your worklist against the
   high/medium tables above.
4. In-raid: re-test the 4:3 stretched-aspect scenario specifically, since that's the one fix I
   made without being able to see it render.
