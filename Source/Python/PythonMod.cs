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
    public abstract class PythonCodeBase
    {
        public readonly ComparablePath pythonDir;

        public PythonCodeBase(string pythonDir)
        {
            if (!Directory.Exists(pythonDir))
                throw new ArgumentException("The Python script directory does not exist");
            this.pythonDir = new ComparablePath(pythonDir);
        }
    }

    public interface IPythonModule : IConsoleTarget
    {
        string ModuleName { get; }
        ComparablePath ModulePath { get; }
        bool IsPackage { get; }
    }

    public class PythonMod : PythonCodeBase, IPythonModule
    {
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
            : base(rwmodInfo.PythonFolder())
        {
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

        private static bool _initialized = false;

        private void _Initialize()
        {
            string init_script = File.ReadAllText(Util.ResourcePath("PythonScripts/initialize_package_resolver.py"));
            Py.Engine.Execute(init_script);
            _initialized = true;
        }

        protected PythonCodebaseFoundation()
        {
            if (!_initialized)
                _Initialize();
        }

        private static Dictionary<Type, object> instances = new Dictionary<Type, object>();

        public static T GetInstanceOf<T>() where T : PythonCodebaseFoundation, new()
        {
            try
            {
                return (T)instances[typeof(T)];
            }
            catch (KeyNotFoundException) { }
            T o = new T();
            instances[typeof(T)] = o;
            return o;
        }

        public static bool HasInstanceOf<T>() where T : PythonCodebaseFoundation, new()
            => instances.ContainsKey(typeof(T));

        public virtual IEnumerable<IPythonModule> Content { get; }

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

        public override IEnumerable<IPythonModule> Content => ordered.Cast<IPythonModule>();

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
            PythonModManager inst = Instance;

            if (!inst.ordered.TrueForAll(m => m.rwmodInfo != rwmodInfo))
                throw new ArgumentException(
                    "The mod with that ModContentPack has already been added");

            //create and import package
            var pkg = IronPython.Modules.PythonImport.new_module(DefaultContext.Default, packageName);
            var pkg_dict = (PythonDictionary)typeof(PythonModule).InvokeMember("_dict",
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
            //todo: setup extension to do __contentpack__.PythonFolder() in script

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
                Verse.Log.Error("In '" + scriptPath + "': " + e.ToString());
                pkg_dict["__error__"] = e;
            }
        }
    }

    //public class PythonModuleManager : PythonCodebaseFoundation
    //{
        /*private List<PythonMod> list = new List<PythonMod>();
        //private Dictionary<string, PythonMod> byPackageName = new Dictionary<string, PythonMod>();

        private static PythonModList _instance = null;
        public static PythonModList GetInstance()
        {
            if (_instance == null)
            {
                _instance = new PythonModList();
            }
            return _instance;
        }

        public void Uh()
        {
            //resolve packageName
            if (scope.ContainsVariable("__packagename__"))
            {
                string raw_packageName = null;
                try
                {
                    raw_packageName = scope.GetVariable<string>("__packagename__");
                    if (ValidateModPackageName(raw_packageName))
                        packageName = raw_packageName;
                    else if (raw_packageName == null && ValidateModPackageName(defaultPackageName))
                        packageName = defaultPackageName;
                    else
                        Verse.Log.Error("Could not determine a valid python package name");
                }
                catch (Exception e)
                {
                    Verse.Log.Error("Error resolving __packagename__: " + e.ToString());
                }
            }
            else
            {
                //user deleted __packagename__; packageName shall remain null
            }
        }


        public void Add(PythonMod mod)
        {
            //if (mod.packageName != null)
            //    byPackageName.Add(mod.packageName, mod); // throws exception if duplicate
            list.Add(mod);
        }*/

        /*public static string FindRootPackagePath(string name)
        {
            return null;
            try
            {
                return _instance.byPackageName[name].pythonDir;
            }
            catch
            {
                // fail if _instance is null or key doesn't exist
                return null;
            }
        }*/

        //pep 8 recommends all lowercase, so make lowercase the default

        /*public static string FindPathOfModule(string fullname)
        {
            if (_instance == null)
                return null;
            var name_parts = fullname.Split('.');
            PythonMod mod = null;
            foreach (PythonMod m in _instance.list)
            {
                if (m.packageName == name_parts[0])
                {
                    mod = m;
                    break;
                }
            }
            if (mod == null)
                return null;

            string path = mod.pythonModDir;

            switch (name_parts.Count())
            {
                case 1:
                    return path;
                default:
                    var mid_parts = name_parts.Skip(1).Take(name_parts.Count() - 2);
                    foreach (string part in mid_parts)
                    {
                        path = System.IO.Path.Combine(path, part);
                        if (!System.IO.Directory.Exists(path))
                            return null;
                    }
                    break;
                case 2:
                    var last_part = name_parts.Last();
                    //etc
                    break;
            }
        }*/

        /*public static bool ValidateModPackageName(object name)
        {
            //this text was considered while designing the function:
            //https://www.python.org/dev/peps/pep-0008/#package-and-module-names
            string strname = name as string;
            if (strname == null)
                return false;
            return;
        }*/
    //}
}
