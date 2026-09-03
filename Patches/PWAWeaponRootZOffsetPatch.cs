using System.Reflection;
using EFT.Animations;
using EFT.CameraControl;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace PiPDisabler.Patches
{
    internal sealed class PWAWeaponRootZOffsetPatch : ModulePatch
    {
        private const int LogIntervalFrames = 60;
        private static int _nextLogFrame;
        private static string _lastLoggedState;

        private static readonly Vector2[] OffsetByFov =
        {
            new Vector2(0.602f, -0.007f),
            new Vector2(0.936f, -0.0037f),
            new Vector2(1.625f, -0.003286385f),
            new Vector2(2.0f, -0.00281690f),
            new Vector2(2.4f, -0.001877934f),
            new Vector2(4.375f, -0.001408451f),
            new Vector2(35f, 0f)
        };

        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(ProceduralWeaponAnimation),
                nameof(ProceduralWeaponAnimation.LateTransformations));

        [PatchPostfix]
        private static void Postfix(ProceduralWeaponAnimation __instance)
        {
            if (!ShouldApply(__instance, out string reason))
            {
                LogState($"skip: {reason}");
                return;
            }

            Transform weaponRoot = __instance.HandsContainer?.WeaponRootAnim;
            if (weaponRoot == null)
            {
                LogState("skip: WeaponRootAnim null");
                return;
            }

            if (!CameraClass.Exist)
            {
                LogState("skip: CameraClass missing");
                return;
            }

            float currentFov = CameraClass.Instance.Fov;
            float zOffset = GetZOffset(currentFov);

            if (Mathf.Approximately(zOffset, 0f))
            {
                LogState($"skip: zero offset fov={currentFov:F3}");
                return;
            }

            weaponRoot.localPosition += new Vector3(0f, 0f, zOffset);
            LogState(
                $"applied fov={currentFov:F3} z={zOffset:F9} root='{weaponRoot.name}' localPos={weaponRoot.localPosition}");
        }

        private static bool ShouldApply(ProceduralWeaponAnimation pwa, out string reason)
        {
            reason = null;

            if (pwa == null || !Settings.ModEnabled.Value)
            {
                reason = pwa == null ? "PWA null" : "mod disabled";
                return false;
            }

            if (!ScopeLifecycle.IsScoped || ScopeLifecycle.IsModBypassedForCurrentScope)
            {
                reason = !ScopeLifecycle.IsScoped ? "not scoped" : "scope bypassed";
                return false;
            }

            if (!pwa.IsAiming || pwa.Sprint)
            {
                reason = !pwa.IsAiming ? "not aiming" : "sprinting";
                return false;
            }

            if (pwa.ScopeAimTransforms == null || pwa.ScopeAimTransforms.Count < 1)
            {
                reason = "no scope aim transforms";
                return false;
            }

            if (!pwa.CurrentScope.IsOptic)
            {
                reason = "current scope is not optic";
                return false;
            }

            return true;
        }

        private static float GetZOffset(float fov)
        {
            if (fov <= OffsetByFov[0].x)
                return OffsetByFov[0].y;

            for (int i = 1; i < OffsetByFov.Length; i++)
            {
                Vector2 previous = OffsetByFov[i - 1];
                Vector2 current = OffsetByFov[i];
                if (fov <= current.x)
                {
                    float t = Mathf.InverseLerp(previous.x, current.x, fov);
                    return Mathf.Lerp(previous.y, current.y, t);
                }
            }

            return OffsetByFov[OffsetByFov.Length - 1].y;
        }

        private static void LogState(string state)
        {
            int frame = Time.frameCount;
            if (frame < _nextLogFrame && state == _lastLoggedState)
                return;

            _nextLogFrame = frame + LogIntervalFrames;
            _lastLoggedState = state;
            PiPDisablerPlugin.DebugLogInfo($"[PWAWeaponRootZOffset] frame={frame} {state}");
        }
    }
}
