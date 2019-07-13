using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;

namespace PythonLoader
{
    internal static class ModuleInitializer
    {
        private static readonly string[] assembly_subdirs = new string[] {
            "Assemblies2/",
            "IronPython-2.7.7/Platforms/Net35/"
        };
        private static Dictionary<string, string> assembly_listing;

        private static Assembly AssemblyResolveHandler(object sender, ResolveEventArgs args)
        {
            try {
                var name = new AssemblyName(args.Name);
                return Assembly.LoadFrom(assembly_listing[name.FullName]);
            }
            catch {
                return null;
            }
        }

        private static string FindModDirectory()
        {
            string assemblyName = Assembly.GetExecutingAssembly().FullName;
            foreach (var mod in Verse.LoadedModManager.RunningMods)
            {
                FileInfo[] files;
                try {
                    files = new DirectoryInfo(mod.AssembliesFolder).GetFiles("*.*", SearchOption.AllDirectories);
                }
                catch (DirectoryNotFoundException) {
                    continue;
                }
                foreach (FileInfo f in files)
                    if (assemblyName == AssemblyName.GetAssemblyName(f.FullName).FullName)
                        return mod.RootDir;
            }
            throw new DirectoryNotFoundException("Unable to locate the directory containing this assembly");
        }

        internal static void Run()
        {
            // set up assembly_listing
            FileInfo[] files = assembly_subdirs.SelectMany(delegate (string sd) {
                string dir = Path.Combine(FindModDirectory(), sd);
                return new DirectoryInfo(dir).GetFiles("*.dll", SearchOption.TopDirectoryOnly);
            }).ToArray();
            assembly_listing = new Dictionary<string, string>();
            foreach (FileInfo f in files)
            {
                string path = f.FullName;
                string name = AssemblyName.GetAssemblyName(path).FullName;
                assembly_listing[name] = path;
            }

            // insert AssemblyResolve handler
            ResolveEventHandler AssemblyResolve = (ResolveEventHandler)
                (typeof(AppDomain).GetField("AssemblyResolve", BindingFlags.NonPublic | BindingFlags.Instance))
                .GetValue(AppDomain.CurrentDomain);
            Delegate[] delegates = AssemblyResolve.GetInvocationList();
            ResolveEventHandler rimworldsWeirdHandler = (ResolveEventHandler)delegates[delegates.Length - 1];
            AppDomain.CurrentDomain.AssemblyResolve -= rimworldsWeirdHandler;
            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(AssemblyResolveHandler);
            AppDomain.CurrentDomain.AssemblyResolve += rimworldsWeirdHandler;
        }
    }
}