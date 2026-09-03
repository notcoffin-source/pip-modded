using EFT.CameraControl;
using HarmonyLib;
using SPT.Reflection.Patching;
using System.Reflection;
using UnityEngine;

namespace PiPDisabler.Patches
{
    internal static class MainCameraLodBiasOverride
    {
        private static bool _active;
        private static CameraLodBiasController _controller;

        public static bool IsActiveFor(CameraLodBiasController controller)
            => _active && controller != null && controller == GetMainController();

        public static void Activate()
        {
            _active = true;
            var controller = GetMainController();
            if (controller != null)
                controller.LodBiasFactor = 1f;
        }

        public static void Deactivate()
        {
            var controller = GetMainController();
            _active = false;

            if (controller == null)
                return;

            float fov = 0f;
            try
            {
                if (CameraClass.Exist && CameraClass.Instance != null)
                    fov = CameraClass.Instance.Fov;
            }
            catch { }

            if (fov <= 0.1f)
            {
                var cam = controller.GetComponent<Camera>();
                fov = cam != null ? cam.fieldOfView : 0f;
            }

            if (fov > 0.1f)
                controller.SetBiasByFov(fov);
        }

        private static CameraLodBiasController GetMainController()
        {
            try
            {
                if (CameraClass.Exist && CameraClass.Instance != null)
                    _controller = CameraClass.Instance.CameraLodBiasController_0;
            }
            catch { }

            if (_controller == null)
            {
                var cam = Helpers.GetMainCamera();
                _controller = cam != null ? cam.GetComponent<CameraLodBiasController>() : null;
            }

            return _controller;
        }
    }

    internal sealed class MainCameraLodBiasSetByFovPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(CameraLodBiasController), nameof(CameraLodBiasController.SetBiasByFov));

        [PatchPrefix]
        private static bool Prefix(CameraLodBiasController __instance)
        {
            if (!MainCameraLodBiasOverride.IsActiveFor(__instance))
                return true;

            __instance.LodBiasFactor = 1f;
            return false;
        }
    }
}
