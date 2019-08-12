using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModContentPack = Verse.ModContentPack;
using Microsoft.Scripting.Hosting;
using System.Text.RegularExpressions;
using IronPython.Runtime;

namespace Python
{
    public interface IPythonModule
    {
        string ModuleName { get; }
        ComparablePath ModulePath { get; }
        bool IsPackage { get; }
    }

    public class PythonMod : IPythonModule, IConsoleTarget
    {
        public readonly ComparablePath pythonDir;
        //reminder: readonly mutable types remain mutable
        public readonly ModContentPack rwmodInfo;
        public readonly string packageName;
        public readonly ScriptScope scope;
        public readonly ScriptSource mainScriptSource = null;

        public string ConsoleTitle => "Python Mod: " + rwmodInfo.Name;
        public ScriptScope GetScope() => scope;
        public string ModuleName => packageName;
        public ComparablePath ModulePath => pythonDir;
        public bool IsPackage => true;

        public PythonMod(ModContentPack rwmodInfo, string packageName, ScriptScope scope, ScriptSource main)
        {
            var dir = rwmodInfo.PythonFolder();
            if (!Directory.Exists(dir))
                throw new ArgumentException("The Python script directory does not exist");
            this.pythonDir = new ComparablePath(dir);
            this.rwmodInfo = rwmodInfo;
            this.packageName = packageName;
            this.scope = scope;
            this.mainScriptSource = main;
        }

        public static string MakeHiddenPackageName(string name)
        {
            string prefix = "[PythonMod:";
            string suffix = "]";
            if (name.StartsWith(prefix) && name.EndsWith(suffix))
                name = name.Substring(prefix.Length, name.Length - (prefix.Length + suffix.Length));
            name = name.Replace(' ', '_');
            string[] wordcontent = Regex.Matches(name, @"\w+", RegexOptions.Compiled)
                .OfType<Match>().Select(m => m.Value).ToArray();
            name = string.Join("", wordcontent);
            if (!Regex.IsMatch(name, @"^[A-Za-z]\w{0,63}$", RegexOptions.Compiled))
                throw new ArgumentException("not valid for hidden package name");
            string result = prefix + name + suffix;
            return result;
        }
    }

    public abstract class PythonCodebaseFoundation
    {
        //this class pretty much exists to have a less bug-prone way to time the initialization of
        // the mod system properly
        //Yes, writing more code to have less bugs. WHAT OF IT?

        private static bool _initialized = false;
        private static Type _currentStartup = null;

        private void _Initialize()
        {
            string init_script = File.ReadAllText(Util.ResourcePath("PythonScripts/initialize_package_resolver.py"));
            Py.Engine.Execute(init_script);
            _initialized = true;
        }

        protected PythonCodebaseFoundation()
        {
            if (_currentStartup != this.GetType())
            {
                throw new InvalidOperationException(
                    "Only use GetInstanceOf to get an instance of a PythonCodebaseFoundation subclass");
            }
            if (!_initialized)
                _Initialize();
        }

        private static Dictionary<Type, PythonCodebaseFoundation> instances =
            new Dictionary<Type, PythonCodebaseFoundation>();

        public static T GetInstanceOf<T>() where T : PythonCodebaseFoundation, new()
        {
            try
            {
                return (T)instances[typeof(T)];
            }
            catch (KeyNotFoundException) { }

            if (_currentStartup != null)
            {
                throw new InvalidOperationException("Only one PythonCodebaseFoundation class may be initialized at a time.");
            }
            else
            {
                _currentStartup = typeof(T);
                try
                {
                    T o = new T();
                    instances[typeof(T)] = o;
                    return o;
                }
                finally
                {
                    _currentStartup = null;
                }
            }
        }

        public static bool HasInstanceOf<T>() where T : PythonCodebaseFoundation, new()
            => instances.ContainsKey(typeof(T));

        public static IEnumerable<T> GetAllContent<T>()
            => instances.Values.Select(o => o.GetContent<T>()).SelectMany(x => x);
        public static IEnumerable<T> GetContent<Manager, T>() where Manager : PythonCodebaseFoundation
        {
            PythonCodebaseFoundation inst;
            if (instances.TryGetValue(typeof(Manager), out inst))
                return inst.GetContent<T>();
            else
                return new T[] { };
        }
        protected virtual IEnumerable<T> GetContent<T>() => new T[] { };

        // convenience utils

        public static PythonDictionary SystemModules
        {
            get
            {
                return (PythonDictionary)typeof(PythonContext).InvokeMember("_modulesDict",
                    BindingFlags.GetField | BindingFlags.Instance | BindingFlags.NonPublic,
                    null, DefaultContext.DefaultPythonContext, null);
            }
        }
    }

    public class PythonModManager : PythonCodebaseFoundation
    {
        private List<PythonMod> ordered = new List<PythonMod>();

        protected override IEnumerable<T> GetContent<T>()
        {
            if (typeof(T) == typeof(PythonMod))
                return (IEnumerable<T>)ordered;
            else if (typeof(T).IsAssignableFrom(typeof(PythonMod)))
                return ordered.Cast<T>();
            else
                return base.GetContent<T>();
        }

        public static PythonModManager Instance => GetInstanceOf<PythonModManager>();

        public static PythonMod FindModOfFilesystemObject(string path)
        {
            if (!HasInstanceOf<PythonModManager>())
                return null;
            var compPath = new ComparablePath(path);
            foreach (var mod in Instance.ordered)
            {
                if (mod.pythonDir.IsSameOrParentDirOf(compPath))
                    return mod;
            }
            return null;
        }

        public static void PopulateWithNewMod(ModContentPack rwmodInfo)
        {
            var path = new ComparablePath(rwmodInfo.PythonFolder()); //will throw ex if rwmodInfo is null, which is fine
            string scriptPath = Path.Combine(path.reconstructedPath, "main.py");
            if (!Directory.Exists(path.ToString()) || !File.Exists(scriptPath))
                return;

            ScriptSource mainScriptSource = Py.Engine.CreateScriptSourceFromFile(scriptPath);
            string packageName = PythonMod.MakeHiddenPackageName(rwmodInfo.Identifier);

            PythonModManager inst = Instance; //getting this after several potential points of failure, to avoid pointless instantiation

            if (!inst.ordered.TrueForAll(m => m.rwmodInfo != rwmodInfo))
                throw new ArgumentException(
                    "The mod with that ModContentPack has already been added");

            //create and import package
            var pkg = IronPython.Modules.PythonImport.new_module(DefaultContext.Default, packageName);
            var pkg_dict = (PythonDictionary)typeof(IronPython.Runtime.PythonModule).InvokeMember("_dict",
                BindingFlags.GetField | BindingFlags.NonPublic | BindingFlags.Instance,
                null, pkg, null);
            {
                var __path__ = new IronPython.Runtime.List();
                __path__.Add(path.reconstructedPath);
                pkg_dict["__path__"] = __path__;
                pkg_dict["__file__"] = scriptPath;
                SystemModules[packageName] = pkg;
            }

            //setup scope
            ScriptScope scope = Py.CreateScope();
            scope.SetVariable("__contentpack__", rwmodInfo);
            scope.GetModuleContext().AddExtensionType(typeof(StandardScriptingExtensions));

            // MAKE MOD OBJECT
            var mod = new PythonMod(rwmodInfo, packageName, scope, mainScriptSource);
            inst.ordered.Add(mod);

            //run main.py
            try
            {
                mainScriptSource.Execute(scope);
            }
            catch (Exception e)
            {
                string msg = "Exception while loading " + scriptPath + ": " + e.ToString() + "\n" + Py.GetFullErrorMessage(e);
                Verse.Log.Error(msg);
                pkg_dict["__error__"] = e;
            }
        }
    }

    /*public class BundledModuleManager : PythonCodebaseFoundation
    {
        private struct BundledModuleInfo : IPythonModule
        {
            public string name;
            public ComparablePath path;
            public string ModuleName => name;
            public ComparablePath ModulePath => path;
            public bool IsPackage => path.IsDir;
        }
        private Dictionary<string, BundledModuleInfo> availableModules = new Dictionary<string, BundledModuleInfo>();

        public static string BundledModulesDir => Path.Combine(ModBasePath, "PythonModules/");

        public BundledModuleManager()
        {
            string[] files = Directory.GetFiles(BundledModulesDir);
            foreach (var path in files)
            {
                var cpath = new ComparablePath(path);
                var fn = cpath.filePart;
                if (fn.Length > 3 && fn.ToLower().EndsWith(".py"))
                    availableModules[fn] = new BundledModuleInfo() { name = fn, path = cpath };
            }

            string[] dirs = Directory.GetDirectories(BundledModulesDir);
            var sep = Path.DirectorySeparatorChar.ToString();
            foreach (var path in dirs)
            {
                var cpath = new ComparablePath(path + (path.EndsWith(sep) ? "" : sep));
                var name = cpath.dirParts.Last();
                availableModules[name] = new BundledModuleInfo() { name = name, path = cpath };
            }
        }

        protected override IEnumerable<IPythonModule> GetContent<IPythonModule>()
            => availableModules.Values.ToArray().Cast<IPythonModule>();

        public static IPythonModule Lookup(string name)
        {
            var inst = GetInstanceOf<BundledModuleManager>();
            try {
                return inst.availableModules[name];
            }
            catch {
                return null;
            }
        }
    }*/

}
