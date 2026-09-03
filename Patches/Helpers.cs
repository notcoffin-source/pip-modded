using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using UnityEngine;

namespace PiPDisabler
{
    internal static class Helpers
    {
        internal static Camera GetMainCamera()
        {
            try
            {
                if (CameraClass.Exist)
                {
                    var cam = CameraClass.Instance.Camera;
                    if (cam != null) return cam;
                }
            }
            catch { }
            return Camera.main;
        }

        /// <summary>
        /// Returns the local player via GameWorld singleton.
        /// </summary>
        internal static Player GetLocalPlayer()
        {
            try
            {
                var gw = Singleton<GameWorld>.Instance;
                return gw?.MainPlayer;
            }
            catch { return null; }
        }

        /// <summary>
        /// Returns the final backbuffer viewport in pixels.
        /// </summary>
        internal static Rect GetDisplayViewport(Camera cam)
        {
            return new Rect(
                0f,
                0f,
                Mathf.Max(1f, Screen.width),
                Mathf.Max(1f, Screen.height));
        }

        /// <summary>
        /// Check if two transforms share the same mode_XXX ancestor.
        /// </summary>
        internal static bool IsOnSameMode(Transform a, Transform b)
        {
            var mA = FindModeAncestor(a);
            var mB = FindModeAncestor(b);
            return mA == mB;
        }

        private static Transform FindModeAncestor(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
                if (p.name != null && p.name.StartsWith("mode_", StringComparison.OrdinalIgnoreCase))
                    return p;
            return null;
        }
    }
    internal static class InputProxy
    {
        private static System.Type _inputType;
        private static System.Reflection.MethodInfo _getKeyDown;
        private static System.Reflection.MethodInfo _getKey;
        private static System.Reflection.PropertyInfo _mouseScrollDelta;

        static InputProxy()
        {
            _inputType = System.Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule")
                      ?? System.Type.GetType("UnityEngine.Input, UnityEngine");
            if (_inputType != null)
            {
                _getKeyDown = _inputType.GetMethod("GetKeyDown", new[] { typeof(KeyCode) });
                _getKey = _inputType.GetMethod("GetKey", new[] { typeof(KeyCode) });
                _mouseScrollDelta = _inputType.GetProperty("mouseScrollDelta",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            }
        }

        public static bool GetKeyDown(KeyCode key)
        {
            try
            {
                if (_getKeyDown == null) return false;
                return (bool)_getKeyDown.Invoke(null, new object[] { key });
            }
            catch { return false; }
        }

        public static bool GetKey(KeyCode key)
        {
            try
            {
                if (_getKey == null) return false;
                return (bool)_getKey.Invoke(null, new object[] { key });
            }
            catch { return false; }
        }
    }
}