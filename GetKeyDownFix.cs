using BepInEx;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Permissions;
using UnityEngine;

#pragma warning disable CS0618

[module: UnverifiableCode]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]

namespace GetKeyDownFix;

[BepInPlugin("CaptainRedstone.GetKeyDownFix", "GetKeyDownFix", "1.0.0")]
/*
TODO
- Icon
- Remember to remove this todo
*/

/*
    Hello code reviewer! TLDR on what this fixes and how it works...
    UnityEngine's GetKeyDown(), GetKeyUp(), GetAxis(), and some other similar functions are all designed to run per-frame. For instance, GetKeyDown() returns true if the previous frame's key was up, and the current down.
    The issue is, Rain World's usage of these functions isn't called on frame, it's called on tick. If GetKeyDown() would return true on frame 1, the next tick may only call it after frame 2, where the result is now false.
    This means that every single instance where the game detects input using anything other than a simple GetKey() WILL fail to read inputs on EVERY frame without a tick. Which is 1/3rd of them at a mere 60fps.
    
    My fix for this is basically to log all of the relevant input events on frame, then when these functions are called, use the log to determine whether an input was ACTUALLY sent, then wipe the log before the next frame.
    On its own this will ironically break any frame-context function calls, but since tick isn't called in frame and vice versa, we can simply check if it's being run in tick, and if not revert to orig.
    Due to hardcoded strings being used as keys everywhere, the list of inputs to track is generated based on realtime tick-based GetX() calls. Vanilla never uses GetXUp() but I've included it for posterity.
*/
public class GetKeyDownFix : BaseUnityPlugin
{
    public string HelloDataminerHeresMySourceCode = "https://github.com/CptnRedstone/GetKeyDownFix";
    public static GetKeyDownFix modInstance;
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
    public class InputHistory //May as well name your tuple components.
    {
        public bool shouldInputFire = false;
        public bool lastInputState = false;
    }
    public static class KnownBuggedInputs //This is the main reason I don't try to convert the keycode into a string and use one function for both; better chance of detecting mod bugs.
    {
        public static List<KeyCode> KeyDownKeyCode = [KeyCode.F6, KeyCode.Comma, KeyCode.J, KeyCode.Q, KeyCode.U];
        public static List<string>  KeyDownString  = ["-", "0", "4", "5", "6", "7", "8", "9", "=", "l"];
        public static List<KeyCode> KeyUpKeyCode   = [];
        public static List<string>  KeyUpString    = [];
        public static List<string>  Axis           = ["Mouse ScrollWheel"];
    }

    public void On_RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);
        if (IsInit) return;
        IsInit = true;

        try
        {
            On.ProcessManager.Update      += On_ProcessManager_Update;
            //On.RainWorld.RunRewiredUpdate += On_RainWorld_RunRewiredUpdate;
            IL.MainLoopProcess.RawUpdate  += IL_MainLoopProcess_RawUpdate;
        }
        catch (Exception ex) { LogFatal(ex); }
        try
        {
            NativeDetour getAxisDetour = new(Input.GetAxis, GetAxis_Hook); //GetAxis is a "native function", which Hook outright doesn't support since it's an empty method. So, NativeDetour it is.
            getAxisDetour.Apply();
            orig_GetAxis = getAxisDetour.GenerateTrampoline<Func<string, float>>(); //We still need access to the original GetAxis, both to log the axis ourselves and to revert to if it's called in frame context.
        }
        catch (Exception ex) { LogFatal(ex); }
        try
        {
            new Hook(typeof(Input).GetMethod("GetKeyDown", [typeof(KeyCode)]), new Func<orig_GetKeyDown_KeyCode, KeyCode, bool>(GetKeyDown_KeyCode_Hook));
            new Hook(typeof(Input).GetMethod("GetKeyDown", [typeof(string )]), new Func<orig_GetKeyDown_string , string , bool>(GetKeyDown_string_Hook ));
            new Hook(typeof(Input).GetMethod("GetKeyUp",   [typeof(KeyCode)]), new Func<orig_GetKeyUp_KeyCode  , KeyCode, bool>(GetKeyUp_KeyCode_Hook  ));
            new Hook(typeof(Input).GetMethod("GetKeyUp",   [typeof(string )]), new Func<orig_GetKeyUp_string   , string , bool>(GetKeyUp_string_Hook   ));
        }
        catch (Exception ex) { LogFatal(ex); }
    }
    public static bool isWithinTick = false; //Self explainatory.
    public static bool tickOccured = false; //Used to determine when to flush the input buffer.
    public static bool isFirstTick = true; //Used to prevent lag (2 ticks 1 frame) from duplicating inputs.
    public static Dictionary<KeyCode, InputHistory> keyDownBufferKeyCode = [];
    public static Dictionary<string,  InputHistory> keyDownBufferString  = [];
    public static Dictionary<KeyCode, InputHistory> keyUpBufferKeyCode   = []; //I could store the ups and downs together but this is a bit more straightforward, and makes it easier to detect and log bugged mod inputs.
    public static Dictionary<string,  InputHistory> keyUpBufferString    = [];
    public static Dictionary<string,  float>        axisBuffer           = [];

    public void WriteToInputBuffers()
    {
        foreach (KeyCode keyCode in keyDownBufferKeyCode.Keys.ToList())
        {
            keyDownBufferKeyCode[keyCode].shouldInputFire |= Input.GetKey(keyCode) && !keyDownBufferKeyCode[keyCode].lastInputState;
            keyDownBufferKeyCode[keyCode].lastInputState = Input.GetKey(keyCode);
        }
        foreach (string name in keyDownBufferString.Keys.ToList())
        {
            keyDownBufferString[name].shouldInputFire |= Input.GetKey(name) && !keyDownBufferString[name].lastInputState;
            keyDownBufferString[name].lastInputState = Input.GetKey(name);
        }
        foreach (KeyCode keyCode in keyUpBufferKeyCode.Keys.ToList())
        {
            keyUpBufferKeyCode[keyCode].shouldInputFire |= Input.GetKey(keyCode) && !keyUpBufferKeyCode[keyCode].lastInputState;
            keyUpBufferKeyCode[keyCode].lastInputState = Input.GetKey(keyCode);
        }
        foreach (string name in keyUpBufferString.Keys.ToList())
        {
            keyUpBufferString[name].shouldInputFire |= Input.GetKey(name) && !keyUpBufferString[name].lastInputState;
            keyUpBufferString[name].lastInputState = Input.GetKey(name);
        }
        foreach (string axisName in axisBuffer.Keys.ToList()) { axisBuffer[axisName] += orig_GetAxis(axisName); }
    }
    public void MaybeClearInputBuffers()
    {
        if (tickOccured)
        {
            tickOccured = false;
            foreach (KeyCode keyCode  in keyDownBufferKeyCode.Keys.ToList()) { keyDownBufferKeyCode[keyCode].shouldInputFire = false; }
            foreach (string  name     in keyDownBufferString .Keys.ToList()) { keyDownBufferString [name]   .shouldInputFire = false; }
            foreach (KeyCode keyCode  in keyUpBufferKeyCode  .Keys.ToList()) { keyUpBufferKeyCode  [keyCode].shouldInputFire = false; }
            foreach (string  name     in keyUpBufferString   .Keys.ToList()) { keyUpBufferString   [name]   .shouldInputFire = false; }
            foreach (string  axisName in axisBuffer          .Keys.ToList()) { axisBuffer          [axisName]                = 0f   ; }
        }
    }
    public void On_ProcessManager_Update(On.ProcessManager.orig_Update orig, ProcessManager self, float deltaTime)
    {
        WriteToInputBuffers();
        orig(self, deltaTime);
        MaybeClearInputBuffers();
    }
    public void On_RainWorld_RunRewiredUpdate(On.RainWorld.orig_RunRewiredUpdate orig, RainWorld self)
    {
        Logger.LogDebug("OLD: " + Input.GetKey("5"));
        orig(self); //I swear this function doesn't do anything? I thought it's there to pull tick-based inputs even without frames but it certainly doesn't seem to... Hook is unused, at least for now.
        Logger.LogDebug("NEW: " + Input.GetKey("5"));
        MaybeClearInputBuffers();
        WriteToInputBuffers();
    }
    public void IL_MainLoopProcess_RawUpdate(ILContext il) //Aka the "where the heck even are we" hook.
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

    public static string buggedInputDetectedBeginning = "Tracking newly detected bugged ";
    public static string modBugWarningEnding = " is NOT known to be from basegame. Please track down which mod is causing this, and let the author know they should fix it.";
    public bool GetKeyDown_KeyCode_Hook(orig_GetKeyDown_KeyCode orig, KeyCode key) //I'm sure there's some fancy way to compact these with a function but eh this works even if it's not the prettiest.
    {
        if (!isWithinTick) { return orig(key); }
        else if (!isFirstTick) { return false; }
        else
        {
            if (!keyDownBufferKeyCode.ContainsKey(key))
            {
                LogDebug(buggedInputDetectedBeginning + "KeyDown: [" + key + "]");
                keyDownBufferKeyCode.Add(key, new());
                if (!KnownBuggedInputs.KeyDownKeyCode.Contains(key)) { LogWarning("Bugged KeyDown [" + key + "]" + modBugWarningEnding); }
            }
            keyDownBufferKeyCode.TryGetValue(key, out InputHistory keyValue);
            return keyValue.shouldInputFire;
        }
    }
    public bool GetKeyDown_string_Hook(orig_GetKeyDown_string orig, string name)
    {
        if (!isWithinTick) { return orig(name); }
        else if (!isFirstTick) { return false; }
        else
        {
            if (!keyDownBufferString.ContainsKey(name))
            {
                LogDebug(buggedInputDetectedBeginning + "KeyDown: \"" + name + "\"");
                keyDownBufferString.Add(name, new());
                if (!KnownBuggedInputs.KeyDownString.Contains(name)) { LogWarning("Bugged KeyDown \"" + name + "\"" + modBugWarningEnding); }
            }
            keyDownBufferString.TryGetValue(name, out InputHistory keyValue);
            return keyValue.shouldInputFire;
        }
    }
    public bool GetKeyUp_KeyCode_Hook(orig_GetKeyUp_KeyCode orig, KeyCode key)
    {
        if (!isWithinTick) { return orig(key); }
        else if (!isFirstTick) { return false; }
        else
        {
            if (!keyUpBufferKeyCode.ContainsKey(key))
            {
                LogWarning(buggedInputDetectedBeginning + "KeyUp: [" + key + "]");
                keyUpBufferKeyCode.Add(key, new());
                if (!KnownBuggedInputs.KeyUpKeyCode.Contains(key)) { LogWarning("Bugged KeyUp [" + key + "]" + modBugWarningEnding); }
            }
            keyUpBufferKeyCode.TryGetValue(key, out InputHistory keyValue);
            return keyValue.shouldInputFire;
        }
    }
    public bool GetKeyUp_string_Hook(orig_GetKeyUp_string orig, string name)
    {
        if (!isWithinTick) { return orig(name); }
        else if (!isFirstTick) { return false; }
        else
        {
            if (!keyUpBufferString.ContainsKey(name))
            {
                LogWarning(buggedInputDetectedBeginning + "KeyUp: \"" + name + "\"");
                keyUpBufferString.Add(name, new());
                if (!KnownBuggedInputs.KeyUpString.Contains(name)) { LogWarning("Bugged KeyUp \"" + name + "\"" + modBugWarningEnding); }
            }
            keyUpBufferString.TryGetValue(name, out InputHistory keyValue);
            return keyValue.shouldInputFire;
        }
    }
    public static float GetAxis_Hook(string axisName)
    {
        if (!isWithinTick) { return orig_GetAxis(axisName); }
        else if (!isFirstTick) { return 0f; }
        else
        {
            if (!axisBuffer.ContainsKey(axisName))
            {
                LogDebug(buggedInputDetectedBeginning + "Axis: \"" + axisName + "\"");
                axisBuffer.Add(axisName, 0f);
                if (!KnownBuggedInputs.Axis.Contains(axisName)) { LogWarning("Bugged Axis \"" + axisName + "\"" + modBugWarningEnding); }
            }
            axisBuffer.TryGetValue(axisName, out float axisValue);
            return axisValue;
        }
    }
}