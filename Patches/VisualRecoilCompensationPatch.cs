using EFT;
using EFT.Animations;
using GPUInstancer;
using HarmonyLib;
using SPT.Reflection.Patching;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace PiPDisabler.Patches
{
    internal static class VisualRecoilCompensationPatch
    {
        private const int LogIntervalFrames = 60;
        private static bool _hooksEnabled;
        private static bool _cameraTransformApplied;
        private static Camera _appliedCamera;
        private static Vector3 _savedCameraPosition;
        private static Quaternion _savedCameraRotation;
        private static int _nextLogFrame;
        private static float _lastLoggedStrength = float.NaN;

        internal static void Enable()
        {
            if (_hooksEnabled)
                return;

            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
            _hooksEnabled = true;
        }

        internal static void Disable()
        {
            if (!_hooksEnabled)
                return;

            Camera.onPreCull -= OnPreCull;
            Camera.onPostRender -= OnPostRender;
            RestoreTransform();
            _hooksEnabled = false;
        }

        private static void OnPreCull(Camera camera)
        {
            RestoreTransform();

            if (camera == null || camera != Helpers.GetMainCamera())
                return;

            Player player;
            ProceduralWeaponAnimation pwa;
            if (!ShouldApply(out player, out pwa))
                return;

            Transform opticCamera = PiPDisabler.OpticCameraTransform;
            if (opticCamera == null)
                return;

            float strength = Mathf.Clamp(PerScopeMeshSurgerySettings.GetVisualRecoilCompensation(), -2f, 2f);
            if (Mathf.Abs(strength) <= 0.0001f)
                return;

            ApplyScreenPlaneCameraOffset(camera, opticCamera, strength);
        }

        private static void OnPostRender(Camera camera)
        {
            if (camera == Helpers.GetMainCamera())
                RestoreTransform();
        }

        private static bool ShouldApply(out Player player, out ProceduralWeaponAnimation pwa)
        {
            player = null;
            pwa = null;

            if (!Settings.ModEnabled.Value ||
                !ScopeLifecycle.IsScoped ||
                ScopeLifecycle.IsModBypassedForCurrentScope ||
                FreelookTracker.IsFreelooking)
            {
                return false;
            }

            player = Helpers.GetLocalPlayer();
            pwa = player?.ProceduralWeaponAnimation;
            if (pwa == null || !pwa.IsAiming || pwa.Sprint)
                return false;

            try
            {
                if (!pwa.CurrentScope.IsOptic)
                    return false;
            }
            catch
            {
                return false;
            }

            return pwa.HandsContainer?.WeaponRootAnim != null;
        }

        private static void ApplyScreenPlaneCameraOffset(Camera camera, Transform opticCamera, float strength)
        {
            Transform cameraTransform = camera.transform;

            _appliedCamera = camera;
            _savedCameraPosition = cameraTransform.position;
            _savedCameraRotation = cameraTransform.rotation;
            _cameraTransformApplied = true;

            Vector3 toOptic = opticCamera.position - cameraTransform.position;
            Vector3 screenPlaneOffset = toOptic - Vector3.Project(toOptic, cameraTransform.forward);
            cameraTransform.position += screenPlaneOffset * strength;

            LogStrength(strength);
        }

        private static void RestoreTransform()
        {
            if (!_cameraTransformApplied)
                return;

            if (_appliedCamera != null)
            {
                Transform cameraTransform = _appliedCamera.transform;
                cameraTransform.position = _savedCameraPosition;
                cameraTransform.rotation = _savedCameraRotation;
            }

            _cameraTransformApplied = false;
            _appliedCamera = null;
        }

        private static void LogStrength(float strength)
        {
            if (!Settings.DebugLogging.Value)
                return;

            int frame = Time.frameCount;
            if (frame < _nextLogFrame && Mathf.Abs(strength - _lastLoggedStrength) < 0.01f)
                return;

            _nextLogFrame = frame + LogIntervalFrames;
            _lastLoggedStrength = strength;
            PiPDisablerPlugin.DebugLogInfo($"[VisualRecoilCompensation] scope='{ScopeLifecycle.GetActiveScopeWhitelistKey()}' screenPlane={strength:F3}");
        }

        internal static bool ShouldDisableGrassMotionVectors(GPUInstancerManager manager)
        {
            if (!(manager is GPUInstancerDetailManager))
                return false;

            var camera = manager.Camera;
            if (camera == null || camera != Helpers.GetMainCamera())
                return false;

            Player player;
            ProceduralWeaponAnimation pwa;
            if (!ShouldApply(out player, out pwa))
                return false;

            if (PiPDisabler.OpticCameraTransform == null)
                return false;

            return Mathf.Abs(PerScopeMeshSurgerySettings.GetVisualRecoilCompensation()) > 0.0001f;
        }
    }

    internal sealed class GrassMotionVectorSuppressionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(GPUInstancerManager), nameof(GPUInstancerManager.Update));

        [PatchTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getter = AccessTools.PropertyGetter(
                typeof(GPUInstancerManager),
                nameof(GPUInstancerManager.bGenerateMotionVectors));
            var replacement = AccessTools.Method(
                typeof(GrassMotionVectorSuppressionPatch),
                nameof(ShouldGenerateMotionVectors));

            foreach (var code in instructions)
            {
                if (code.opcode == OpCodes.Call && Equals(code.operand, getter))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, replacement);
                    continue;
                }

                yield return code;
            }
        }

        private static bool ShouldGenerateMotionVectors(GPUInstancerManager manager)
        {
            return !VisualRecoilCompensationPatch.ShouldDisableGrassMotionVectors(manager) &&
                   GPUInstancerManager.bGenerateMotionVectors;
        }
    }
}
