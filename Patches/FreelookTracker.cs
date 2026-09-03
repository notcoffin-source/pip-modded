using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using EFT;
using EFT.CameraControl;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace PiPDisabler
{
    /// <summary>
    /// Tracks the freelook (middle-mouse look-around) state while ADS and
    /// intercepts EFT's FOV stomp when freelook ends.
    ///
    /// ── THE PROBLEM ──────────────────────────────────────────────────────
    /// Player.Look() detects the _mouseLookControl transition and calls
    /// CameraClass.SetFov(35, 1, true) for optics.  This hardcoded 35°
    /// stomps whatever zoomed FOV the mod had active (e.g. 6x on a VUDU).
    /// Because Look() runs in EFT's LateUpdate pipeline AFTER our BepInEx
    /// Update(), we cannot reliably race it with a post-hoc SetFov call.
    ///
    /// ── THE FIX ──────────────────────────────────────────────────────────
    /// On freelook enter the actual camera FOV is snapshotted into
    /// _fovBeforeFreelook.  On freelook exit two complementary mechanisms
    /// restore it:
    ///   1. OnFreelookExit() calls CameraClass.SetFov directly (Update path).
    ///   2. The Player.Look transpiler (LookSetFovInterceptor) intercepts
    ///      EFT's SetFov(35) call in LateUpdate and substitutes the cached
    ///      value, so whichever runs last still lands the correct FOV.
    ///
    /// _lastAppliedScopedFov is kept as a fallback for the interceptor in
    /// case OnFreelookEnter fires before CameraClass is valid.
    /// </summary>
    internal static class FreelookTracker
    {
        private static bool _isFreelooking;
        private static bool _wasFreelooking;

        /// <summary>
        /// Camera FOV snapshotted at the moment freelook begins.
        /// This is the value we restore when freelook ends.
        /// </summary>
        private static float _fovBeforeFreelook;

        /// <summary>
        /// The last FOV value the mod successfully applied while scoped.
        /// Updated by ScopeLifecycle.ApplyFov and PWAMethod23Patch via
        /// CacheAppliedFov().  Stays frozen during freelook because all
        /// mod FOV writes are gated on !IsFreelooking.
        /// Used as fallback when _fovBeforeFreelook is unavailable.
        /// </summary>
        private static float _lastAppliedScopedFov;

        // Direct accessor — MouseLookControl is a public property
        private static readonly Func<Player, bool> _getMouseLookControl = p => p.MouseLookControl;

        /// <summary>True while the player is freelooking during ADS.</summary>
        public static bool IsFreelooking => _isFreelooking;

        /// <summary>
        /// Called by ApplyFov / SetFovWithOverride every time the mod writes
        /// a zoomed FOV to the camera.
        /// </summary>
        public static void CacheAppliedFov(float fov)
        {
            if (fov > 0.5f)
                _lastAppliedScopedFov = fov;
        }

        /// <summary>
        /// Called once from plugin Awake.
        /// </summary>
        public static void Init()
        {
            PiPDisablerPlugin.DebugLogInfo("[FreelookTracker] Init: MouseLookControl is public — direct access.");
        }

        /// <summary>
        /// Per-frame poll.  Called from ScopeLifecycle.Tick() (only while scoped + mod active).
        /// Returns true on the FRAME where freelook ENDS.
        /// FOV is handled by the Player.Look transpiler — caller does not need to re-apply.
        /// </summary>
        public static bool Tick()
        {
            _wasFreelooking = _isFreelooking;
            _isFreelooking = ReadMouseLookControl();

            if (_isFreelooking && !_wasFreelooking)
            {
                OnFreelookEnter();
            }
            else if (!_isFreelooking && _wasFreelooking)
            {
                OnFreelookExit();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reset state when leaving scope or toggling mod.
        /// </summary>
        public static void Reset()
        {
            _isFreelooking = false;
            _wasFreelooking = false;
            _lastAppliedScopedFov = 0f;
            _fovBeforeFreelook = 0f;
        }

        // ===== Transitions =====

        private static void OnFreelookEnter()
        {
            // Snapshot the current camera FOV so we can restore it precisely on exit.
            try
            {
                if (CameraClass.Exist && CameraClass.Instance != null)
                    _fovBeforeFreelook = CameraClass.Instance.Fov;
                else if (_lastAppliedScopedFov > 0.5f)
                    _fovBeforeFreelook = _lastAppliedScopedFov;
            }
            catch
            {
                _fovBeforeFreelook = _lastAppliedScopedFov;
            }

            PiPDisablerPlugin.DebugLogInfo(
                $"[FreelookTracker] Freelook START — cached FOV={_fovBeforeFreelook:F1}°, " +
                "restoring scope meshes, hiding reticle");

            Patches.WeaponScalingPatch.RestoreScaleForFreelook();
            var os = ScopeLifecycle.ActiveOptic;
            if (os != null)
            {
                MeshSurgeryManager.RestoreForScope(os.transform);
                LensTransparency.EnsureHidden();
            }
            ReticleRenderer.Hide();
            ScopeEffectsRenderer.Hide();
        }

        private static void OnFreelookExit()
        {
            float fovToRestore = _fovBeforeFreelook > 0.5f ? _fovBeforeFreelook : _lastAppliedScopedFov;

            PiPDisablerPlugin.DebugLogInfo(
                $"[FreelookTracker] Freelook END — restoring FOV={fovToRestore:F1}°, showing reticle");

            // Directly restore the cached FOV (Update path).
            // The Player.Look transpiler also runs in LateUpdate as a safety net.
            if (fovToRestore > 0.5f)
            {
                try
                {
                    if (CameraClass.Exist && CameraClass.Instance != null)
                        CameraClass.Instance.SetFov(fovToRestore,
                            Settings.FovAnimationDuration.Value, false);
                }
                catch { }
            }

            var os = ScopeLifecycle.ActiveOptic;
            if (os != null)
            {
                LensTransparency.HideAllLensSurfaces(os);
                ReticleRenderer.SetLensMaskEntries(LensTransparency.CollectLensMaskEntries(os));

                var occluderRenderers = LensTransparency.CollectHousingRenderers(os);
                ReticleRenderer.SetOccluderMaskRenderers(occluderRenderers);

                MeshSurgeryManager.ApplyForOptic(os);
                float mag = FovController.GetVisualMagnification();
                ReticleRenderer.Show(os, mag);
                ScopeEffectsRenderer.Show();
            }
        }

        // ===== Player.Look SetFov Interceptor =====

        /// <summary>
        /// Drop-in replacement for CameraClass.SetFov called from Player.Look.
        /// When the mod is active and the player is exiting freelook while scoped
        /// with an optic, we replace EFT's target FOV with our cached value.
        ///
        /// The signature matches CameraClass.SetFov(float, float, bool) exactly
        /// so the transpiler can do a simple callvirt→call swap.
        /// </summary>
        public static void LookSetFovInterceptor(CameraClass cameraClass, float targetFov, float duration, bool force)
        {
            if (cameraClass == null)
                return;

            // Only intervene when:
            //  - Mod is on and zoom is enabled
            //  - We're scoped and not bypassed
            //  - We have a valid cached FOV
            //  - The game is trying to set a non-freelook FOV (i.e. freelook just ended
            //    and EFT wants to set 35° — we know because targetFov < settingsFov)
            // Prefer the FOV snapshotted at freelook-enter; fall back to last mod-applied value.
            float fovToRestore = _fovBeforeFreelook > 0.5f ? _fovBeforeFreelook : _lastAppliedScopedFov;

            if (Settings.ModEnabled.Value &&
                ScopeLifecycle.IsScoped &&
                !ScopeLifecycle.IsModBypassedForCurrentScope &&
                fovToRestore > 0.5f)
            {
                // EFT sets targetFov to 35 when exiting freelook with an optic,
                // and to settingsFov when entering freelook.
                // We only intercept the "exit" case (low FOV for optic).
                // Quick heuristic: if targetFov <= 35 and we have a cached value, use ours.
                if (targetFov <= 35.5f)
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[FreelookTracker] Look interceptor: replacing FOV {targetFov:F1}° → " +
                        $"{fovToRestore:F1}° (pre-freelook snapshot)");

                    targetFov = fovToRestore;
                }
            }

            cameraClass.SetFov(targetFov, duration, force);
        }

        // ===== Reader =====

        private static bool ReadMouseLookControl()
        {
            try
            {
                var player = Helpers.GetLocalPlayer();
                if (player == null) return false;
                return _getMouseLookControl(player);
            }
            catch
            {
                return false;
            }
        }
    }

    // =====================================================================
    //  Harmony patch: transpile Player.Look to intercept SetFov
    // =====================================================================

    namespace Patches
    {
        /// <summary>
        /// Transpiles Player.Look to replace CameraClass.SetFov with
        /// FreelookTracker.LookSetFovInterceptor.  This lets us substitute
        /// the FOV value at the exact callsite where EFT stomps our zoom.
        /// </summary>
        internal sealed class PlayerLookPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod()
                => AccessTools.Method(typeof(Player), nameof(Player.Look));

            [PatchTranspiler]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var setFov = AccessTools.Method(typeof(CameraClass), nameof(CameraClass.SetFov));
                var replacement = AccessTools.Method(typeof(FreelookTracker),
                    nameof(FreelookTracker.LookSetFovInterceptor));

                int replaced = 0;
                foreach (var code in instructions)
                {
                    if (code.opcode == OpCodes.Callvirt && Equals(code.operand, setFov))
                    {
                        yield return new CodeInstruction(OpCodes.Call, replacement);
                        replaced++;
                        continue;
                    }

                    yield return code;
                }

                PiPDisablerPlugin.DebugLogInfo(
                    $"[PlayerLookPatch] Transpiler: replaced {replaced} SetFov callsite(s)");
            }
        }
    }
}
