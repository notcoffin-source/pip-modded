using System;
using System.Reflection;
using EFT.CameraControl;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace PiPDisabler.Patches
{
    internal static class VanillaOpticSuppression
    {
        private static bool _allowSetResolution;

        public static bool ShouldSuppress(OpticSight opticSight)
        {
            if (!Settings.ModEnabled.Value)
                return false;

            return !ScopeLifecycle.ShouldBypassForCurrentOptic(opticSight);
        }

        public static void EnsureRenderTextureForVanilla(GClass3687 manager)
        {
            if (manager == null || manager.Camera == null)
                return;

            if (manager.RenderTexture_0 != null && manager.Camera.targetTexture != null)
                return;

            try
            {
                _allowSetResolution = true;
                manager.SetResolution(manager.OpticFinalResolution);
            }
            finally
            {
                _allowSetResolution = false;
            }
        }

        public static void RestoreVanillaOpticState(OpticSight opticSight)
        {
            if (opticSight == null || !CameraClass.Exist || CameraClass.Instance == null)
                return;

            var manager = CameraClass.Instance.OpticCameraManager;
            if (manager == null)
                return;

            try
            {
                manager.CurrentOpticSight = opticSight;

                if (opticSight.CameraData != null)
                {
                    manager.OpticRetrice?.SetOpticSight(opticSight);
                    manager.OpticComponentUpdater_0?.CopyComponentFromOptic(opticSight);
                }

                global::PiPDisabler.PiPDisabler.ForceLensFade(opticSight, false);

                if (manager.Camera != null)
                {
                    manager.Camera.enabled = true;
                    manager.Camera.gameObject.SetActive(true);
                }

                EnsureRenderTextureForVanilla(manager);
                CameraClass.Instance.method_10();
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[VanillaOpticSuppression] Restore vanilla optic state failed: {ex.Message}");
            }
        }

        public static void ReleaseRenderTexture(GClass3687 manager)
        {
            if (manager == null)
                return;

            try
            {
                if (manager.Camera != null)
                    manager.Camera.targetTexture = null;

                if (manager.RenderTexture_0 != null)
                {
                    manager.RenderTexture_0.Release();
                    UnityEngine.Object.Destroy(manager.RenderTexture_0);
                    manager.RenderTexture_0 = null;
                }

                Shader.SetGlobalTexture(GClass3687.Int_0, null);
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[VanillaOpticSuppression] ReleaseRenderTexture failed: {ex.Message}");
            }
        }

        public static bool ShouldKeepSetResolution()
            => _allowSetResolution || !Settings.ModEnabled.Value || ScopeLifecycle.IsCurrentOrPendingOpticBypassed();
    }

    internal sealed class OpticCameraManagerEnableOptic_NoPipPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(GClass3687), "method_2");

        [PatchPrefix]
        private static bool Prefix(GClass3687 __instance, OpticSight opticSight)
        {
            if (__instance == null)
                return true;

            if (!VanillaOpticSuppression.ShouldSuppress(opticSight))
            {
                VanillaOpticSuppression.EnsureRenderTextureForVanilla(__instance);
                return true;
            }

            try
            {
                __instance.CurrentOpticSight = null;
                __instance.OpticRetrice?.SetOpticSight(null);

                if (opticSight?.CameraData != null && __instance.OpticComponentUpdater_0 != null)
                    __instance.OpticComponentUpdater_0.CopyComponentFromOptic(opticSight);

                if (__instance.Camera != null)
                    __instance.Camera.gameObject.SetActive(true);

                VanillaOpticSuppression.ReleaseRenderTexture(__instance);

                PiPDisablerPlugin.DebugLogInfo(
                    $"[VanillaOpticSuppression] Skipped vanilla optic manager enable for '{opticSight?.name ?? "null"}' but kept updater sync active");
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[VanillaOpticSuppression] Manager enable suppression failed: {ex.Message}");
            }

            return false;
        }
    }

    internal sealed class OpticCameraManagerSetResolution_NoPipPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(GClass3687), nameof(GClass3687.SetResolution));

        [PatchPostfix]
        private static void Postfix(GClass3687 __instance)
        {
            if (VanillaOpticSuppression.ShouldKeepSetResolution())
                return;

            VanillaOpticSuppression.ReleaseRenderTexture(__instance);
        }
    }

    internal sealed class CameraClassOnOpticEnabled_NoPipPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(CameraClass), "method_10");

        [PatchPrefix]
        private static bool Prefix()
        {
            if (!Settings.ModEnabled.Value || ScopeLifecycle.IsCurrentOrPendingOpticBypassed())
                return true;

            var currentOptic = CameraClass.Instance?.OpticCameraManager?.CurrentOpticSight;
            if (currentOptic != null && ScopeLifecycle.ShouldBypassForCurrentOptic(currentOptic))
                return true;

            PiPDisablerPlugin.DebugLogInfo(
                "[VanillaOpticSuppression] Skipped CameraClass optic SSAA/lens enable path");
            return false;
        }
    }
}
