using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using EFT.Animations;
using EFT.CameraControl;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace PiPDisabler.Patches
{
    /// <summary>
    /// Rewrites ProceduralWeaponAnimation.method_23's SetFov call in-place so
    /// EFT only executes one FOV write per frame path.
    /// </summary>
    internal sealed class PWAMethod23Patch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(ProceduralWeaponAnimation),
                nameof(ProceduralWeaponAnimation.method_23));

        [PatchPrefix]
        private static void Prefix(ProceduralWeaponAnimation __instance)
        {
            if (__instance == null) return;
            FovOverrideContext.CurrentPwa = __instance;
        }

        [PatchPostfix]
        private static void Postfix()
        {
            FovOverrideContext.CurrentPwa = null;
        }

        [PatchTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var setFov = AccessTools.Method(typeof(CameraClass), nameof(CameraClass.SetFov));
            var replacement = AccessTools.Method(typeof(PWAMethod23Patch), nameof(SetFovWithOverride));

            foreach (var code in instructions)
            {
                if (code.opcode == OpCodes.Callvirt && Equals(code.operand, setFov))
                {
                    yield return new CodeInstruction(OpCodes.Call, replacement);
                    continue;
                }

                yield return code;
            }
        }

        private static void SetFovWithOverride(CameraClass cameraClass, float targetFov, float duration, bool force)
        {
            var pwa = FovOverrideContext.CurrentPwa;
            if (cameraClass == null)
                return;

            bool modZoomEnabled =
                Settings.ModEnabled.Value;

            bool isAdsOptic = false;
            if (pwa != null && pwa.IsAiming && !pwa.Sprint)
            {
                try { isAdsOptic = pwa.CurrentScope.IsOptic; }
                catch { isAdsOptic = false; }
            }

            if (pwa != null &&
                modZoomEnabled &&
                ScopeLifecycle.IsScoped &&
                !ScopeLifecycle.IsModBypassedForCurrentScope &&
                !FreelookTracker.IsFreelooking &&
                isAdsOptic)
            {
                float zoomBaseFov = FovController.MagnificationBaselineFov;
                float zoomedFov = FovController.ComputeZoomedFov();
                bool smoothScopeFov = FovController.IsSmoothScopeFovActive();

                if (zoomedFov >= 0.5f && (smoothScopeFov || zoomedFov <= zoomBaseFov))
                {
                    if (FovController.HasFovChanged(zoomedFov))
                    {
                        FovController.TrackAppliedFov(zoomedFov);
                        FreelookTracker.CacheAppliedFov(zoomedFov);
                        cameraClass.SetFov(zoomedFov, duration, false);
                    }
                    return;
                }
            }

            // Block EFT's method_23 FOV writes while ADS with an optic.
            // Pose changes (stand/crouch/prone) call method_23 and can stomp zoom.
            if (modZoomEnabled &&
                ScopeLifecycle.IsScoped &&
                !ScopeLifecycle.IsModBypassedForCurrentScope &&
                isAdsOptic)
                return;

            // After scope exit, RestoreFov protects the ADS transition FOV. Once ADS
            // ends, EFT's normal base-FOV write must be allowed through immediately.
            if (ScopeLifecycle.HasPostExitRestore)
            {
                if (pwa != null && !pwa.IsAiming)
                {
                    ScopeLifecycle.ClearPostExitRestore();
                }
                else
                {
                    float restoreFov = ScopeLifecycle.PostExitRestoreFov;
                    if (Mathf.Abs(targetFov - restoreFov) > FovController.FovChangeThreshold)
                        return;
                }
            }

            cameraClass.SetFov(targetFov, duration, force);
        }

        private static class FovOverrideContext
        {
            [System.ThreadStatic]
            internal static ProceduralWeaponAnimation CurrentPwa;
        }
    }
}
