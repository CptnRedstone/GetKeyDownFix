using BepInEx;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Rewired;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Permissions;
using UnityEngine;
using static Rewired.PlayerController;
using static System.Runtime.CompilerServices.RuntimeHelpers;

#pragma warning disable CS0618

[module: UnverifiableCode]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]

namespace GetKeyDownFix;

[BepInPlugin("CaptainRedstone.GetKeyDownFix", "GetKeyDownFix", "1.0.0")]
/*
    Hello code reviewer! TLDR on what this fixes and how it works...
    UnityEngine's GetKeyDown(), GetKeyUp(), GetAxis(), and probably some other stuff are all designed to run per-frame. For instance, GetKeyDown() returns true if the previous frame's key was up, and the current down.
    The issue is, Rain World's usage of these functions isn't called on frame, it's called on tick. If GetKeyDown() would return true on frame 1, the next tick may only call it after frame 2, where the result is now false.
    This means that every single instance where the game detects input using anything other than a simple GetKey() WILL fail to read inputs on EVERY frame without a tick. Which is 1/3rd of them at just 60fps.
    
    My fix for this is basically to log all of the relevant input events on frame, then when these functions are called, use the log to determine whether an input was ACTUALLY sent, then wipe the log before the next frame.
    On its own this will ironically break any frame-context function calls, but since tick isn't called in frame and vice versa, we can simply check if it's being run in tick, and if not revert to orig.
    Due to hardcoded strings being used as keys everywhere, the list of inputs to track is generated based on realtime tick-based GetX() calls. Vanilla never uses GetXUp() but I've included it for posterity.
*/
public partial class GetKeyDownFix : BaseUnityPlugin
{
    public string HelloDataminerHeresMySourceCode = "https://github.com/CptnRedstone/GetKeyDownFix";
    public static GetKeyDownFix modInstance;
    public GetKeyDownFix() { }
    public static void LogDebug  (object data) { modInstance.Logger.LogDebug  (data); }
    public static void LogInfo   (object data) { modInstance.Logger.LogInfo   (data); }
    public static void LogMessage(object data) { modInstance.Logger.LogMessage(data); }
    public static void LogWarning(object data) { modInstance.Logger.LogWarning(data); }
    public static void LogError  (object data) { modInstance.Logger.LogError  (data); }
    public static void LogFatal  (object data) { modInstance.Logger.LogFatal  (data); }
    private void OnEnable()
    {
        modInstance = this;
        On.RainWorld.OnModsInit += On_RainWorld_OnModsInit;
    }

    private bool IsInit;
    public delegate bool orig_GetKeyDown_KeyCode(KeyCode key);
    public delegate bool orig_GetKeyDown_string (string name);
    public delegate bool orig_GetKeyUp_KeyCode  (KeyCode key);
    public delegate bool orig_GetKeyUp_string   (string name);
    public static Func<string, float> orig_GetAxis;
    private void On_RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);
        if (IsInit) return;
        IsInit = true;

        try
        {
            On.ProcessManager.Update     += On_ProcessManager_Update;
            IL.MainLoopProcess.RawUpdate += IL_MainLoopProcess_RawUpdate;
        }
        catch (Exception ex) { LogFatal(ex); }
        try
        {
            NativeDetour getAxisDetour = new(Input.GetAxis, GetAxis_Hook); //GetAxis is a "native function", which Hook outright doesn't support. So, NativeDetour it is.
            getAxisDetour.Apply();
            orig_GetAxis = getAxisDetour.GenerateTrampoline<Func<string, float>>();
        }
        catch (Exception ex) { LogFatal(ex); }
        try
        {
            new Hook(
                typeof(Input).GetMethod("GetKeyDown", [typeof(KeyCode)]),
                new Func<orig_GetKeyDown_KeyCode, KeyCode, bool>(GetKeyDown_KeyCode_Hook)
            );
            new Hook(
                typeof(Input).GetMethod("GetKeyDown", [typeof(string)]),
                new Func<orig_GetKeyDown_string, string, bool>(GetKeyDown_string_Hook)
            );
            new Hook(
                typeof(Input).GetMethod("GetKeyUp", [typeof(KeyCode)]),
                new Func<orig_GetKeyUp_KeyCode, KeyCode, bool>(GetKeyUp_KeyCode_Hook)
            );
            new Hook(
                typeof(Input).GetMethod("GetKeyUp", [typeof(string)]),
                new Func<orig_GetKeyUp_string, string, bool>(GetKeyUp_string_Hook)
            );
        }
        catch (Exception ex) { LogFatal(ex); }
    }
    private static bool isWithinTick = false; //Self explainatory.
    private static bool tickOccured = false; //Used to determine when to flush the input buffer.
    private static bool isFirstTick = true; //Used to prevent lag (2 ticks 1 frame) from duplicating inputs.
    public static Dictionary<KeyCode, bool>  keyDownBufferKeyCode = [];
    public static Dictionary<string,  bool>  keyDownBufferString  = [];
    public static Dictionary<KeyCode, bool>  keyUpBufferKeyCode   = []; //I could store the ups and downs together but this is a bit more straightforward, and, only other mods will ever have up calls.
    public static Dictionary<string,  bool>  keyUpBufferString    = [];
    public static Dictionary<string,  float> axisBuffer           = [];
    private void On_ProcessManager_Update(On.ProcessManager.orig_Update orig, ProcessManager self, float deltaTime)
    {
        foreach (KeyCode keyCode  in keyDownBufferKeyCode.Keys.ToList()) { keyDownBufferKeyCode[keyCode] |= Input.GetKey(keyCode);  }
        foreach (string  keyCode  in keyDownBufferString .Keys.ToList()) { keyDownBufferString [keyCode] |= Input.GetKey(keyCode);  }
        foreach (KeyCode keyCode  in keyUpBufferKeyCode  .Keys.ToList()) { keyUpBufferKeyCode  [keyCode] |= Input.GetKey(keyCode);  }
        foreach (string  keyCode  in keyUpBufferString   .Keys.ToList()) { keyUpBufferString   [keyCode] |= Input.GetKey(keyCode);  }
        foreach (string  axisName in axisBuffer          .Keys.ToList()) { axisBuffer         [axisName] += orig_GetAxis(axisName); }
        orig(self, deltaTime);
        if (tickOccured)
        {
            tickOccured = false;
            foreach (KeyCode keyCode  in keyDownBufferKeyCode.Keys.ToList()) { keyDownBufferKeyCode[keyCode] = false; }
            foreach (string  keyCode  in keyDownBufferString .Keys.ToList()) { keyDownBufferString [keyCode] = false; }
            foreach (KeyCode keyCode  in keyUpBufferKeyCode  .Keys.ToList()) { keyUpBufferKeyCode  [keyCode] = false; }
            foreach (string  keyCode  in keyUpBufferString   .Keys.ToList()) { keyUpBufferString   [keyCode] = false; }
            foreach (string  axisName in axisBuffer          .Keys.ToList()) { axisBuffer         [axisName] = 0f;    }
        }
    }
    private void IL_MainLoopProcess_RawUpdate(ILContext il) //Aka the "where the heck even are we" hook.
    {
        try
        {
            ILCursor cursor = new(il);

            cursor.GotoNext(MoveType.Before, x => x.MatchCallvirt(nameof(MainLoopProcess), nameof(MainLoopProcess.Update)));
            cursor.MoveAfterLabels();
            cursor.EmitDelegate(delegate()
            {
                isWithinTick = true;
                tickOccured = true;
            });
            cursor.GotoNext(MoveType.After, x => x.MatchCallvirt(nameof(MainLoopProcess), nameof(MainLoopProcess.Update)));
            cursor.MoveAfterLabels();
            cursor.EmitDelegate(delegate()
            {
                isWithinTick = false;
                isFirstTick = false;
            });
            cursor.GotoNext(MoveType.Before, x => x.MatchCallvirt(nameof(MainLoopProcess), nameof(MainLoopProcess.GrafUpdate)));
            cursor.MoveAfterLabels();
            cursor.EmitDelegate(delegate ()
            {
                isFirstTick = true;
            });
        }
        catch (Exception ex) { LogFatal(ex); }
    }
    //I'm sure there's some fancy way to compact this with a function but eh this works even if it's not the prettiest.
    private bool GetKeyDown_KeyCode_Hook(orig_GetKeyDown_KeyCode orig, KeyCode key)
    {
        if (!isWithinTick) { return orig(key); }
        else if (!isFirstTick) { return false; }
        else
        {
            if (!keyDownBufferKeyCode.ContainsKey(key))
            {
                LogDebug("Detected new bugged KeyDown: " + key);
                keyDownBufferKeyCode.Add(key, false);
            }
            keyDownBufferKeyCode.TryGetValue(key, out bool keyValue);
            return keyValue;
        }
    }
    private bool GetKeyDown_string_Hook(orig_GetKeyDown_string orig, string name)
    {
        if (!isWithinTick) { return orig(name); }
        else if (!isFirstTick) { return false; }
        else
        {
            if (!keyDownBufferString.ContainsKey(name))
            {
                LogDebug("Detected new bugged KeyDown: \"" + name + "\"");
                keyDownBufferString.Add(name, false);
            }
            keyDownBufferString.TryGetValue(name, out bool keyValue);
            return keyValue;
        }
    }
    private bool GetKeyUp_KeyCode_Hook(orig_GetKeyUp_KeyCode orig, KeyCode key)
    {
        if (!isWithinTick) { return orig(key); }
        else if (!isFirstTick) { return false; }
        else
        {
            if (!keyUpBufferKeyCode.ContainsKey(key))
            {
                LogWarning("Detected new bugged KeyUp: " + key + " - This IS an external mod bug, please isolate the mod and tell the author to use GetKey() instead.");
                keyUpBufferKeyCode.Add(key, false);
            }
            keyUpBufferKeyCode.TryGetValue(key, out bool keyValue);
            return keyValue;
        }
    }
    private bool GetKeyUp_string_Hook(orig_GetKeyUp_string orig, string name)
    {
        if (!isWithinTick) { return orig(name); }
        else if (!isFirstTick) { return false; }
        else
        {
            if (!keyUpBufferString.ContainsKey(name))
            {
                LogWarning("Detected new bugged KeyUp: \"" + name + " - This IS an external mod bug, please isolate the mod and tell the author to use GetKey() instead.");
                keyUpBufferString.Add(name, false);
            }
            keyUpBufferString.TryGetValue(name, out bool keyValue);
            return keyValue;
        }
    }
    static float GetAxis_Hook(string axisName)
    {
        if (!isWithinTick) { return orig_GetAxis(axisName); }
        else if (!isFirstTick) { return 0f; }
        else
        {
            if (!axisBuffer.ContainsKey(axisName))
            {
                LogDebug("Detected new bugged Axis: \"" + axisName + "\"");
                axisBuffer.Add(axisName, 0f);
            }
            axisBuffer.TryGetValue(axisName, out float axisValue);
            return axisValue;
        }
    }
}