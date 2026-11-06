using BepInEx;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Rewired;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security;
using System.Security.Permissions;
using UnityEngine;
using static Rewired.PlayerController;

#pragma warning disable CS0618

[module: UnverifiableCode]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]

namespace GetKeyDownFix;

[BepInPlugin("CaptainRedstone.GetKeyDownFix", "GetKeyDownFix", "1.0.0")]
/* TODO
 - GetKeyDown(String)
 - GetKeyDown(Keycode)
 - GetKeyUp(String)
 - GetKeyUp(Keycode)
 - Fix "Mouse ScrollWheel" string usage?
*/

/*
    TLDR on what this fixes and how it works...
    UnityEngine's GetKeyDown(), GetKeyUp(), GetAxis(), and probably some other stuff are all designed to run per-frame. For instance, GetKeyDown() returns true if the previous frame's key was up, and the current down.
    The issue is, Rain World's usage of these functions isn't called on frame, it's called on tick. If GetKeyDown() would return true on frame 1, the next tick may only call it after frame 2, where the result is now false.
    This means that every single instance where the game detects input using anything other than a simple GetKey() WILL fail to read inputs on EVERY frame without a tick. Which is 1/3rd of them at just 60fps.
    
    My fix for this is basically to log all of the relevant input events on frame, then when these functions are called, use the log to determine whether an input was ACTUALLY sent, then wipe the log before the next frame.
    On its own this will ironically break any frame-context function calls, but since tick isn't called in frame and vice versa, we can simply check if it's being run in tick, and if not revert to orig.
*/
public partial class GetKeyDownFix : BaseUnityPlugin
{
    public static GetKeyDownFix modInstance;
    public GetKeyDownFix() { }
    public static void LogDebug(object data) { modInstance.Logger.LogDebug(data); }
    public static void LogInfo(object data) { modInstance.Logger.LogInfo(data); }
    public static void LogMessage(object data) { modInstance.Logger.LogMessage(data); }
    public static void LogWarning(object data) { modInstance.Logger.LogWarning(data); }
    public static void LogError(object data) { modInstance.Logger.LogError(data); }
    public static void LogFatal(object data) { modInstance.Logger.LogFatal(data); }
    private void OnEnable()
    {
        modInstance = this;
        On.RainWorld.OnModsInit += On_RainWorld_OnModsInit;
    }

    private bool IsInit;
    public static Func<string, float> orig_GetAxis;
    public delegate bool orig_GetKeyDown(string name);
    private void On_RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);
        if (IsInit) return;

        try
        {
            IsInit = true;
        }
        catch (Exception ex) { LogFatal(ex); }

        On.ProcessManager.Update += On_ProcessManager_Update;
        IL.MainLoopProcess.RawUpdate += IL_MainLoopProcess_RawUpdate;

        try
        {
            NativeDetour getAxisDetour = new(Input.GetAxis, GetAxis_Hook); //Stupid native functions breaking Hook
            getAxisDetour.Apply();
            orig_GetAxis = getAxisDetour.GenerateTrampoline<Func<string, float>>();
        }
        catch (Exception ex) { LogFatal(ex); }
        try
        {
            //new Hook(
            //    typeof(Input).GetMethod("GetKeyDown", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public),
            //    new Func<orig_GetKeyDown, string, bool>(GetKeyDown_Hook)
            //);
        }
        catch (Exception ex) { LogFatal(ex); }
    }
    private static bool isWithinTick = false; //Self explainatory.
    private static bool tickOccured = false; //Used to determine when to flush the input buffer.
    private static bool isFirstTick = true; //Used to prevent lag (2 ticks 1 frame) from duplicating inputs.
    public static Dictionary<string, float> axisBuffer;
    private void On_ProcessManager_Update(On.ProcessManager.orig_Update orig, ProcessManager self, float deltaTime)
    {
        foreach (string axisName in axisBuffer.Keys)
        {
            axisBuffer.TryGetValue(axisName, out float axisValue);
            axisBuffer.Add(axisName, axisValue += orig_GetAxis(axisName));
        }
        orig(self, deltaTime);
        if (tickOccured)
        {
            tickOccured = false;
            foreach (string axisName in axisBuffer.Keys)
            {
                axisBuffer.Add(axisName, 0f);
            }
        }
    }
    private void IL_MainLoopProcess_RawUpdate(ILContext il)
    {
        try
        {
            ILCursor cursor = new(il);

            cursor.GotoNext(MoveType.Before,
                    x => x.MatchCallvirt(nameof(MainLoopProcess), nameof(MainLoopProcess.Update)));
            cursor.MoveAfterLabels();
            cursor.EmitDelegate(delegate()
            {
                isWithinTick = true;
                tickOccured = true;
            });

            cursor.GotoNext(MoveType.After,
                    x => x.MatchCallvirt(nameof(MainLoopProcess), nameof(MainLoopProcess.Update)));
            cursor.MoveAfterLabels();
            cursor.EmitDelegate(delegate()
            {
                isWithinTick = false;
                isFirstTick = false;
            });

            cursor.GotoNext(MoveType.Before,
                    x => x.MatchCallvirt(nameof(MainLoopProcess), nameof(MainLoopProcess.GrafUpdate)));
            cursor.MoveAfterLabels();
            cursor.EmitDelegate(delegate ()
            {
                isFirstTick = true;
            });
        }
        catch (Exception ex) { LogFatal(ex); }
    }
    static float GetAxis_Hook(string axisName)
    {
        if (!isWithinTick)
        {
            return orig_GetAxis(axisName);
        }
        else if (!isFirstTick)
        {
            return 0f;
        }
        else
        {
            if (!axisBuffer.ContainsKey(axisName))
            {
                LogDebug("New axis registered: " + axisName);
                axisBuffer.Add(axisName, 0f);
            }
            axisBuffer.TryGetValue(axisName, out float axisValue);
            return axisValue;
        }
    }
    private bool GetKeyDown_Hook(orig_GetKeyDown orig, string name)
    {
        Logger.LogDebug(name);
        return orig(name);
    }
}
