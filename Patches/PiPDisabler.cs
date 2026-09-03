using System;
using System.Reflection;
using System.Collections.Generic;
using EFT.CameraControl;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using System.Reflection.Emit;

namespace PiPDisabler
{
    internal static class PiPDisabler
    {
        // Track original camera state so toggle-off can restore.
        private struct CameraState
        {
            public Camera Cam;
            public bool Enabled;
            public int CullingMask;
            public RenderTexture TargetTexture;
        }

        private static readonly List<CameraState> _cams = new List<CameraState>(16);

        // Cached reflection field for OpticSight inside OpticComponentUpdater.
        // The field name is obfuscated and varies between EFT builds, so we
        // discover it by type instead of relying on a hard-coded name.
        private static FieldInfo _opticSightField;
        private static bool _opticSightFieldSearched;

        // BaseOpticCamera is the global PiP camera. Disabling per-scope OpticComponentUpdater cameras
        // alone can still leave BaseOpticCamera rendering and costing performance.
        //
        // IMPORTANT: we disable Camera components (enabled=false, cullingMask=0, targetTexture=null)
        // but keep the GameObject active so our OpticComponentUpdater LateUpdate prefix can still run
        // and keep scope lifecycle working.
        private static readonly List<Camera> _baseOpticCams =
            new List<Camera>(4);

        private static int _nextBaseScanFrame = -1;
        private static bool _loggedBase;

        /// <summary>
        /// The optic camera's Transform — synced to the scope's look direction
        /// every frame by OpticComponentUpdater.LateUpdate().
        /// Used by ReticleRenderer for camera alignment.
        /// </summary>
        internal static Transform OpticCameraTransform { get; private set; }
        internal static Transform Debug_LastOpticCameraTransform;
        internal static string Debug_LastOpticCameraSetBy;
        internal static int Debug_LastOpticCameraSetFrame;


        // Track OpticSight.enabled state so we can disable it while scoped and restore on un-scope.
        private static readonly Dictionary<OpticSight, bool> _opticOrigEnabled =
            new Dictionary<OpticSight, bool>(32);

        // Used to suppress our own OpticSight.OnDisable postfix (same-frame).
        private static readonly Dictionary<OpticSight, int> _ignoreOnDisableFrame =
            new Dictionary<OpticSight, int>(32);

        private static bool _allowForcedLensFade;

        internal static void TickBaseOpticCamera()
        {
            if (!ScopeLifecycle.ShouldSuppressVanillaPiPNow())
                return;

            // Any condition that should preserve vanilla PiP must restore and skip disabling.
            if (ShouldAllowVanillaPiP())
            {
                RestoreAllCameras();
                return;
            }

            // Resolve occasionally in case CameraClass rebuilds the optic camera.
            if (_baseOpticCams.Count == 0 || Time.frameCount >= _nextBaseScanFrame)
            {
                TryFindBaseOpticCameras();
                _nextBaseScanFrame = Time.frameCount + 60; // ~1s @ 60fps
            }

            // Re-apply disable every frame for the cached cameras (cheap).
            for (int i = 0; i < _baseOpticCams.Count; i++)
            {
                var cam = _baseOpticCams[i];
                if (cam == null) continue;
                ForceDisable(cam);
            }
        }


        internal static void SetOpticSightEnabled(OpticSight os, bool enabled)
        {
            if (os == null) return;

            try
            {
                if (!enabled)
                {
                    // Store baseline once, then force-disable.
                    if (!_opticOrigEnabled.ContainsKey(os))
                        _opticOrigEnabled[os] = os.enabled;

                    _ignoreOnDisableFrame[os] = Time.frameCount;
                    if (os.enabled) os.enabled = false;
                }
                else
                {
                    if (_opticOrigEnabled.TryGetValue(os, out var wasEnabled))
                    {
                        // Restore to baseline (do not force-enable if baseline was disabled).
                        if (wasEnabled && !os.enabled)
                            os.enabled = true;

                        _opticOrigEnabled.Remove(os);
                    }

                    _ignoreOnDisableFrame.Remove(os);
                }
            }
            catch { /* ignore */ }
        }

        internal static bool ShouldIgnoreOnDisable(OpticSight os)
        {
            if (os == null) return false;

            if (_ignoreOnDisableFrame.TryGetValue(os, out var f))
            {
                if (f == Time.frameCount)
                {
                    // One-shot suppression (only for the OnDisable we just triggered).
                    _ignoreOnDisableFrame.Remove(os);
                    return true;
                }

                // Stale entry (e.g. scene change) -> clear.
                if (Time.frameCount - f > 10)
                    _ignoreOnDisableFrame.Remove(os);
            }

            return false;
        }

        internal static void CleanupVanillaOpticState(OpticSight opticSight)
        {
            try
            {
                if (CameraClass.Exist && CameraClass.Instance != null)
                {
                    var mgr = CameraClass.Instance.OpticCameraManager;
                    mgr.CurrentOpticSight = null;
                    mgr.OpticRetrice.SetOpticSight(null);
                    mgr.OpticRetrice.Clear();
                }
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[PiPDisabler] Vanilla optic cleanup failed: {ex.Message}");
            }

            if (opticSight != null)
            {
                try
                {
                    _allowForcedLensFade = true;
                    opticSight.LensFade(true);
                }
                catch { }
                finally { _allowForcedLensFade = false; }
            }
        }

        internal static bool ShouldAllowForcedLensFade()
            => _allowForcedLensFade;

        internal static void ForceLensFade(OpticSight opticSight, bool isHide)
        {
            if (opticSight == null)
                return;

            try
            {
                _allowForcedLensFade = true;
                opticSight.LensFade(isHide);
            }
            catch { }
            finally { _allowForcedLensFade = false; }
        }

        private static void TryFindBaseOpticCameras()
        {
            _baseOpticCams.Clear();

            try
            {
                if (!CameraClass.Exist || CameraClass.Instance == null)
                    return;

                var cam = CameraClass.Instance.OpticCameraManager?.Camera;
                if (cam == null)
                    return;

                var all = cam.GetComponentsInChildren<Camera>(true);
                for (int c = 0; c < all.Length; c++)
                {
                    var cc = all[c];
                    if (cc != null && !_baseOpticCams.Contains(cc))
                        _baseOpticCams.Add(cc);
                }

                if (!_baseOpticCams.Contains(cam))
                    _baseOpticCams.Add(cam);

                if (!_loggedBase)
                {
                    _loggedBase = true;
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[PiPDisabler] Found BaseOpticCamera via OpticCameraManager (cameras: {_baseOpticCams.Count})");
                }
            }
            catch { /* ignore */ }
        }

        private static bool ShouldSuppressPiPDisableForCurrentOptic(OpticComponentUpdater updater)
        {
            if (ScopeLifecycle.IsModBypassedForCurrentScope)
                return true;

            if (updater == null) return false;

            try
            {
                var field = GetOpticSightField();
                if (field == null) return false;

                var os = field.GetValue(updater) as OpticSight;
                if (os == null) return false;

                return ScopeLifecycle.ShouldBypassForCurrentOptic(os);
            }
            catch
            {
                return false;
            }
        }

        internal static FieldInfo GetOpticSightField()
        {
            if (!_opticSightFieldSearched)
            {
                _opticSightFieldSearched = true;
                // Search all instance fields (including private) for one of type OpticSight
                var fields = typeof(OpticComponentUpdater).GetFields(
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                foreach (var f in fields)
                {
                    if (f.FieldType == typeof(OpticSight))
                    {
                        _opticSightField = f;
                        PiPDisablerPlugin.DebugLogInfo(
                            $"[PiPDisabler] Found OpticSight field on OpticComponentUpdater: '{f.Name}'");
                        break;
                    }
                }
                if (_opticSightField == null)
                    PiPDisablerPlugin.DebugLogInfo(
                        "[PiPDisabler] Could not find any OpticSight field on OpticComponentUpdater!");
            }
            return _opticSightField;
        }

        public static void RestoreAllCameras()
        {
            for (int i = 0; i < _cams.Count; i++)
            {
                var st = _cams[i];
                if (st.Cam == null) continue;
                try
                {
                    st.Cam.enabled = st.Enabled;
                    st.Cam.cullingMask = st.CullingMask;
                    st.Cam.targetTexture = st.TargetTexture;
                }
                catch { /* ignore */ }
            }

            // Restore OpticSight.enabled states we changed.
            try
            {
                foreach (var kv in _opticOrigEnabled)
                {
                    var os = kv.Key;
                    if (os == null) continue;
                    if (kv.Value && !os.enabled)
                        os.enabled = true;
                }
            }
            catch { /* ignore */ }

            _opticOrigEnabled.Clear();
            _ignoreOnDisableFrame.Clear();

            _cams.Clear();

            _baseOpticCams.Clear();
            _loggedBase = false;
            _nextBaseScanFrame = -1;
            OpticCameraTransform = null;
            Debug_LastOpticCameraTransform = null;
            Debug_LastOpticCameraSetBy = null;
            Debug_LastOpticCameraSetFrame = 0;
        }

        private static void ForceDisable(Camera cam)
        {
            if (cam == null) return;

            // Store only once
            for (int i = 0; i < _cams.Count; i++)
                if (_cams[i].Cam == cam) return;

            var st = new CameraState
            {
                Cam = cam,
                Enabled = cam.enabled,
                CullingMask = cam.cullingMask,
                TargetTexture = cam.targetTexture as RenderTexture
            };
            _cams.Add(st);

            try { cam.enabled = false; } catch { }

            try
            {
                if (cam.targetTexture != null)
                    cam.targetTexture = null;
            }
            catch { /* ignore */ }

            try { cam.cullingMask = 0; } catch { }
        }


        private static bool ShouldAllowVanillaPiP()
        {
            return !Settings.ModEnabled.Value
                || ScopeLifecycle.IsCurrentOrPendingOpticBypassed()
                || ScopeLifecycle.IsLastOpticNameBypassed();
        }

        internal sealed class OpticComponentUpdaterCopyComponentFromOptic_DisablePiP : ModulePatch
        {
            protected override MethodBase GetTargetMethod()
                => AccessTools.Method(typeof(OpticComponentUpdater), nameof(OpticComponentUpdater.CopyComponentFromOptic));

            [PatchPostfix]
            private static void Postfix(OpticComponentUpdater __instance)
            {
                if (!Settings.ModEnabled.Value) return;
                if (__instance == null) return;
                if (ShouldSuppressPiPDisableForCurrentOptic(__instance)) return;

                // Cache the optic camera transform for ReticleRenderer camera alignment
                OpticCameraTransform = __instance.transform;
                Debug_LastOpticCameraTransform = OpticCameraTransform;
                Debug_LastOpticCameraSetBy = __instance.name;
                Debug_LastOpticCameraSetFrame = Time.frameCount;

                var cam = __instance.GetComponent<Camera>();
                ForceDisable(cam);
            }
        }

        internal sealed class OpticComponentUpdaterLateUpdate_DisablePiP : ModulePatch
        {
            protected override MethodBase GetTargetMethod()
                => AccessTools.Method(typeof(OpticComponentUpdater), "LateUpdate");

            [PatchPrefix]
            private static bool Prefix(OpticComponentUpdater __instance)
            {
                if (!Settings.ModEnabled.Value) return true;
                if (__instance == null) return true;
                if (!ScopeLifecycle.ShouldSuppressVanillaPiPNow()) return true;

                OpticCameraTransform = __instance.transform;
                Debug_LastOpticCameraTransform = OpticCameraTransform;
                Debug_LastOpticCameraSetBy = __instance.name;
                Debug_LastOpticCameraSetFrame = Time.frameCount;

                if (!ShouldSuppressPiPDisableForCurrentOptic(__instance))
                {
                    var cam = __instance.GetComponent<Camera>();
                    ForceDisable(cam);
                }

                return true;
            }

            [PatchPostfix]
            private static void Postfix()
            {
                if (!Settings.ModEnabled.Value) return;
                if (!ScopeLifecycle.IsScoped) return;
                if (ScopeLifecycle.IsModBypassedForCurrentScope) return;
                if (FreelookTracker.IsFreelooking) return;

                ScopeLifecycle.ReapplyFov();
            }

            [PatchTranspiler]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var render = AccessTools.Method(typeof(Camera), nameof(Camera.Render));
                var replacement = AccessTools.Method(
                    typeof(OpticComponentUpdaterLateUpdate_DisablePiP),
                    nameof(MaybeRender));

                foreach (var code in instructions)
                {
                    if (code.opcode == OpCodes.Callvirt && Equals(code.operand, render))
                    {
                        yield return new CodeInstruction(OpCodes.Call, replacement);
                        continue;
                    }

                    yield return code;
                }
            }

            private static void MaybeRender(Camera cam)
            {
                if (cam == null) return;

                if (ShouldAllowVanillaPiP())
                {
                    cam.Render();
                }
            }
        }

        internal sealed class OpticSightLensFade_NoPipPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod()
                => AccessTools.Method(typeof(OpticSight), nameof(OpticSight.LensFade));

            [PatchPrefix]
            private static bool Prefix(OpticSight __instance)
            {
                if (ShouldAllowVanillaPiP() || PiPDisabler.ShouldAllowForcedLensFade()) return true;

                // In No-PiP mode, block LensFade to avoid material state issues.
                // Lens hiding is handled by SSAA signal only — do NOT call
                // LensTransparency here (fires per-frame, causes thrashing).
                return false;
            }
        }
    }
}
