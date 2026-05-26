using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GiantessLLMMod.Patches
{
    internal static class UIInputBlocker
    {
        public static bool IsOverlayVisible;
        public static bool InputCaptured;
        private static bool _saved;
        private static CursorLockMode _previousLockState;
        private static bool _previousCursorVisible;

        public static void CaptureInput()
        {
            InputCaptured = true;
            ApplyCursorState();
        }

        public static void ReleaseInput()
        {
            InputCaptured = false;
            ApplyCursorState();
        }

        public static void ApplyCursorState()
        {
            if (IsOverlayVisible && InputCaptured)
            {
                if (!_saved)
                {
                    _previousLockState = Cursor.lockState;
                    _previousCursorVisible = Cursor.visible;
                    _saved = true;
                    Input.ResetInputAxes();
                }

                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                return;
            }

            if (_saved)
            {
                Cursor.lockState = _previousLockState;
                Cursor.visible = _previousCursorVisible;
                Input.ResetInputAxes();
                _saved = false;
            }
        }
    }

    [HarmonyPatch]
    internal static class InputHelpersAllowInGameControlsPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("GameInput.InputHelpers");
            return AccessTools.PropertyGetter(type, "AllowInGameControls");
        }

        private static void Postfix(ref bool __result)
        {
            if (UIInputBlocker.InputCaptured)
                __result = false;
        }
    }

    [HarmonyPatch]
    internal static class FirstPersonAIOUpdatePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("FirstPersonAIO");
            if (type == null) yield break;

            string[] names = { "TickUpdate" };
            foreach (var name in names)
            {
                var method = AccessTools.Method(type, name);
                if (method != null)
                    yield return method;
            }
        }

        private static bool Prefix()
        {
            return !UIInputBlocker.InputCaptured;
        }
    }

    [HarmonyPatch]
    internal static class FirstPersonAIOCameraMovePatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("FirstPersonAIO");
            return AccessTools.Method(type, "GetCameraMove");
        }

        private static bool Prefix(ref Vector4 __result)
        {
            if (!UIInputBlocker.InputCaptured)
                return true;

            __result = Vector4.zero;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class FPSBehaviourProcessControlsPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("FPSBehaviour");
            return AccessTools.Method(type, "ProcessControls");
        }

        private static bool Prefix()
        {
            return !UIInputBlocker.InputCaptured;
        }
    }

    [HarmonyPatch]
    internal static class FPSBehaviourBoolControlsPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("FPSBehaviour");
            if (type == null) yield break;

            string[] names = { "CommonControl_UI", "CommonControl_Tablet", "CommonControl_GrabObject", "CommonControl_Punch", "CommonControl_ToolGun" };
            foreach (var name in names)
            {
                var method = AccessTools.Method(type, name);
                if (method != null)
                    yield return method;
            }
        }

        private static bool Prefix(ref bool __result)
        {
            if (!UIInputBlocker.InputCaptured)
                return true;

            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class FPSBehaviourPausePatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("FPSBehaviour");
            return AccessTools.Method(type, "CommonControl_Pause");
        }

        private static bool Prefix(ref bool __result)
        {
            if (!UIInputBlocker.IsOverlayVisible)
                return true;

            if (Input.GetKeyDown(KeyCode.Escape))
                UIInputBlocker.CaptureInput();

            __result = true;
            return false;
        }
    }
}
