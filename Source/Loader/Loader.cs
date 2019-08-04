using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using Verse;

namespace Python.Loader
{
    public class PythonLoaderMod : Verse.Mod
    {
        public PythonLoaderMod(ModContentPack content) : base(content)
        {
            Loader.InstallConsoleButton();
            Loader.InstallPythonModBootstrapper();
        }
    }

    public static class Loader
    {
        private static Dictionary<string, Assembly> _loaded_assemblies = new Dictionary<string, Assembly>();
        public static Assembly PythonAssembly => GetAssembly("Assemblies2/Python.dll");
        public static Assembly HarmonyAssembly => GetAssembly("Assemblies2/0Harmony.dll");
        private static object _harmony = null;
        internal static object Harmony
        {
            get
            {
                if (_harmony == null)
                {
                    _harmony = HarmonyAssembly.GetType("Harmony.HarmonyInstance").InvokeMember("Create",
                        BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Static, null, null,
                        new object[] { "likeafox.rimworld.python.loader" });
                }
                return _harmony;
            }
        }
        private static ModClassCreationTracker tracker = null;

        private static Assembly GetAssembly(string path)
        {
            if (!_loaded_assemblies.ContainsKey(path))
            {
                string full_path = Path.Combine(ModuleInitializer.modDirectory, path);
                _loaded_assemblies[path] = Assembly.LoadFrom(full_path);
            }
            return _loaded_assemblies[path];
        }

        internal static void InstallConsoleButton()
        {
            PythonAssembly.GetType("Python.ConsoleButton").GetMethod("Install",
                BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { });
        }

        internal static void InstallPythonModBootstrapper()
        {
            tracker = ModClassCreationTracker.Create(typeof(PythonLoaderMod));
            tracker.ModDoneCreating +=
                delegate (object sender, ModClassCreationTracker.ModDoneCreatingEventArgs e)
            {
                //Verse.Log.Message("trigger to load python script for mod: " + e.mod.Name);
                PythonAssembly.GetType("Python.PythonModManager").GetMethod("PopulateWithNewMod",
                    BindingFlags.Public | BindingFlags.Static)
                    .Invoke(null, new object[] { e.mod });
            };
        }
    }

    public sealed class ModClassCreationTracker
    {
        //static data

        private static bool didStaticSetup = false;
        private static Dictionary<Type, Verse.Mod> runningModClasses = null; //direct reference to LoadedModManager.runningModClasses
        private static List<WeakReference<ModClassCreationTracker>> instances =
            new List<WeakReference<ModClassCreationTracker>>();
        public static IEnumerable<ModClassCreationTracker> Instances
        {
            get
            {
                foreach (var inst in instances)
                    if (inst.IsAlive)
                        yield return inst.Target;
            }
        }
        private static bool blockEnterCycleUpdate = false;

        public static ModClassCreationTracker Create(Type detectType = null)
        {
            if (!didStaticSetup)
            {
                //static setup
                runningModClasses = (Dictionary<Type,Verse.Mod>)typeof(LoadedModManager).InvokeMember("runningModClasses",
                    BindingFlags.GetField | BindingFlags.NonPublic | BindingFlags.Static, null, null, null);
                DoHarmonyPatching();
                didStaticSetup = true;
            }

            var inst = new ModClassCreationTracker(detectType);
            instances.Add(new WeakReference<ModClassCreationTracker>(inst));
            return inst;
        }

        //instance data

        private class ModClassInfo
        {
            public int index;
            public Type modClass;
            public ModContentPack mod;
            public bool trackedLoadState = false;
            public int? loadedOnCycle;
        }
        ModClassInfo[] infoByOrder;
        Dictionary<Type, ModClassInfo> infoByClass = new Dictionary<Type, ModClassInfo>();
        Dictionary<ModContentPack, List<ModClassInfo>> infosByMod = new Dictionary<ModContentPack, List<ModClassInfo>>();
        int? cycle = null;
        int nextEmit = 0;

        public class ModDoneCreatingEventArgs : EventArgs
        {
            public ModContentPack mod;
        }

        public event System.EventHandler<ModDoneCreatingEventArgs> ModDoneCreating;

        //functions

        private ModClassCreationTracker(Type detectType = null)
        {
            //prep
            Type[] modClasses = typeof(Mod).InstantiableDescendantsAndSelf().ToArray();
            var modByAssem = new Dictionary<Assembly, ModContentPack>();
            foreach (var mod in LoadedModManager.RunningMods)
            {
                foreach (var assem in mod.assemblies.loadedAssemblies)
                    modByAssem[assem] = mod;
                infosByMod[mod] = new List<ModClassInfo>();
            }
            infoByOrder = new ModClassInfo[modClasses.Length];

            //fill data
            for (var i = 0; i < modClasses.Length; i++)
            {
                var cls = modClasses[i];
                var mod = modByAssem[cls.Assembly];
                var info = new ModClassInfo { index = i, modClass = cls, mod = mod };
                infoByOrder[i] = info;
                infoByClass[cls] = info;
                infosByMod[mod].Add(info);
            }

            //set initial state
            ProbeLoadState();
            cycle = 0;
            try { infoByClass[detectType].trackedLoadState = false; }
            catch { throw new ArgumentException("detectType did not match any loaded type."); }
        }

        private void ProbeLoadState()
        {
            foreach (var cls in runningModClasses.Keys)
            {
                var info = infoByClass[cls];
                if (info.trackedLoadState == false)
                {
                    info.trackedLoadState = true;
                    info.loadedOnCycle = cycle;
                }
            }
        }

        private int EstimateProgress()
        {
            //Tries to return index of ModContentPack of next mod to be loaded
            //ex: if estimated progress = 3, that means mods 0, 1, and 2 have finished loading,
            //and mod 3 may or may not have begun loading.
            //Progress index can also be thought of as the total number of mods loaded.
            ModClassInfo[] loadedClasses = infoByOrder.Where(n => n.trackedLoadState).ToArray();
            if (loadedClasses.Length == 0)
            {
                if (infoByOrder.Length == 0)
                    return LoadedModManager.RunningModsListForReading.Count;
                return infoByOrder[0].mod.loadOrder;
            }

            var lastLoadedMod = loadedClasses[loadedClasses.Length - 1].mod;
            List<ModClassInfo> lastLoadedModInfos = infosByMod[lastLoadedMod];
            ModClassInfo[] knownLoadCycleInfos = lastLoadedModInfos.Where(n => n.loadedOnCycle != null).ToArray();

            if (knownLoadCycleInfos.Length == 0)
                return lastLoadedMod.loadOrder;

            int firstVerifiedCycleOfMod = knownLoadCycleInfos.Min(n => n.loadedOnCycle.Value);
            int lookaheadCycles =
                (cycle.Value - firstVerifiedCycleOfMod) // cycles passed since first verified cycle class was loaded
                + 1 // +1 offset for the first verified cycle class itself
                + (lastLoadedModInfos.Count - knownLoadCycleInfos.Length); // classes loaded during unverified cycles

            var nextMods = LoadedModManager.RunningModsListForReading.Skip(lastLoadedMod.loadOrder).GetEnumerator();
            while (nextMods.MoveNext())
            {
                lookaheadCycles -= infosByMod[nextMods.Current].Count;
                if (lookaheadCycles < 0)
                    return nextMods.Current.loadOrder;
            }
            return LoadedModManager.RunningModsListForReading.Count;
        }

        private void CycleUpdate()
        {
            ProbeLoadState();
            int curMod = EstimateProgress();
            for (; nextEmit < curMod; nextEmit++)
            {
                var args = new ModDoneCreatingEventArgs { mod = LoadedModManager.RunningModsListForReading[nextEmit] };
                try
                {
                    ModDoneCreating?.Invoke(this, args);
                }
                catch (Exception e)
                {
                    Log.Error(e.ToString());
                }
            }
            cycle++;
        }

        private static void Harmony_Prefix_DeepProfiler_End()
        {
            if (!blockEnterCycleUpdate) // stop reentry to avoid infinite recursion by user event handlers
            {
                blockEnterCycleUpdate = true;
                foreach (var inst in Instances)
                {
                    try
                    {
                        inst.CycleUpdate();
                    }
                    catch (Exception e)
                    {
                        Log.Error(e.ToString());
                    }
                }
                blockEnterCycleUpdate = false;
            }
        }

        private static void DoHarmonyPatching()
        {
            // Harmony.HarmonyMethod..ctor
            var _harmonyMethodType = Loader.HarmonyAssembly.GetType("Harmony.HarmonyMethod").GetConstructor(new Type[] {
                typeof(MethodInfo)
            });
            // harmony.Patch(Verse.DeepProfiler.End, HarmonyMethod(Harmony_Prefix_DeepProfiler_End))
            Loader.Harmony.GetType().GetMethod("Patch").Invoke(Loader.Harmony,
                new object[] {
                typeof(Verse.DeepProfiler).GetMethod("End", BindingFlags.Public | BindingFlags.Static),
                _harmonyMethodType.Invoke(new object[] {
                    typeof(ModClassCreationTracker).GetMethod("Harmony_Prefix_DeepProfiler_End",
                        BindingFlags.Static | BindingFlags.NonPublic)
                }),
                null, null
                });
        }

        private static bool InsideCreateModClassesMethod()
        {
            var st = new StackTrace();
            bool inside = false;
            try
            {
                var names = Enumerable.Range(0, st.FrameCount)
                    .Select(i => st.GetFrame(i).GetMethod().Name).GetEnumerator();
                while (names.MoveNext())
                {
                    if (names.Current.StartsWith("End"))
                    {
                        for (int i = 0; i < 2; i++)
                            if (names.MoveNext() && names.Current.StartsWith("CreateModClasses"))
                                inside = true;
                        break;
                    }
                }
            }
            catch {}
            return inside;
        }
    }
}
