using System;
using System.IO;
using System.Collections.Generic;
using ModContentPack = Verse.ModContentPack;

namespace Python
{
    public class PythonMod
    {
        public readonly string pythonDir;
        public readonly string packageName = null;
        //reminder: readonly mutable types remain mutable
        public readonly ModContentPack rwmodInfo = null;
        public readonly Microsoft.Scripting.Hosting.ScriptScope scope;
        public readonly Microsoft.Scripting.Hosting.ScriptSource mainScriptSource = null;
        public readonly bool mainScriptFinished;

        public PythonMod(string pythonDir, string defaultPackageName = null, ModContentPack rwmodInfo = null)
        {
            if (!Directory.Exists(pythonDir))
                throw new ArgumentException("Python mod directory does not exist");
            this.pythonDir = pythonDir;
            this.rwmodInfo = rwmodInfo;

            //setup scope
            scope = Python.CreateScope();
            scope.SetVariable("__packagename__", defaultPackageName);
            scope.SetVariable("__contentpack__", rwmodInfo);

            //run main.py, if there is one
            string scriptPath = Path.Combine(pythonDir, "main.py");
            mainScriptFinished = false;
            if (File.Exists(scriptPath))
            {
                try
                {
                    mainScriptSource = Python.Engine.CreateScriptSourceFromFile(scriptPath);
                    mainScriptSource.Execute(scope);
                    mainScriptFinished = true;
                }
                catch (Exception e)
                {
                    Verse.Log.Error("In '" + scriptPath + "': " + e.ToString());
                }
            }

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

        public static bool ValidateModPackageName(object name)
        {
            string strname = name as string;
            if (strname == null)
                return false;
            return System.Text.RegularExpressions.Regex.IsMatch(strname, @"^[A-Za-z]\w{0,63}$");
        }
    }

    public class PythonModList
    {
        private List<PythonMod> list = new List<PythonMod>();
        private Dictionary<string, PythonMod> byPackageName = new Dictionary<string, PythonMod>();

        private static PythonModList _instance = null;
        public static PythonModList GetInstance()
        {
            if (_instance == null)
            {
                _instance = new PythonModList();
                Python.Engine.Execute(
                    "class ModPackageFinderLoader(object):"
                    + "\n\tdef __init__(self):"
                    + "\n\t\timport imp, Python"
                    + "\n\t\tself.imp = imp"
                    + "\n\t\tself.find_root_package_path = Python.PythonModList.FindRootPackagePath"
                    + "\n\t\tself.last_find = (None, None)"
                    + "\n"
                    + "\n\tdef find_module(self, fullname, path=None):"
                    + "\n\t\tpath = self.find_root_package_path(fullname)"
                    + "\n\t\tif path is None:"
                    + "\n\t\t\treturn None"
                    + "\n\t\tself.last_find = (fullname, path)"
                    + "\n\t\treturn self"
                    + "\n"
                    + "\n\tdef load_module(self, fullname):"
                    + "\n\t\tlast_fullname, last_path = self.last_find"
                    + "\n\t\tif fullname == last_fullname:"
                    + "\n\t\t\tpath = last_path"
                    + "\n\t\telse:"
                    + "\n\t\t\tpath = self.find_module(fullname)"
                    + "\n\t\tif path is None:"
                    + "\n\t\t\traise ImportError()"
                    + "\n\t\tdesc = (None, None, self.imp.PKG_DIRECTORY)"
                    + "\n\t\treturn self.imp.load_module(fullname, None, path, desc)"
                    + "\n"
                    + "\nimport sys"
                    + "\nsys.meta_path.append(ModPackageFinderLoader())"
                    );
            }
            return _instance;
        }

        private PythonModList()
        {
        }

        public void Add(PythonMod mod)
        {
            if (mod.packageName != null)
                byPackageName.Add(mod.packageName, mod); // throws exception if duplicate
            list.Add(mod);
        }

        public static string FindRootPackagePath(string name)
        {
            try
            {
                return _instance.byPackageName[name].pythonDir;
            }
            catch
            {
                // fail if _instance is null or key doesn't exist
                return null;
            }
        }

        public static void PopulateUsingModInfo(ModContentPack rwmodInfo)
        {
            var path = Path.Combine(rwmodInfo.RootDir, "Python/");
            if (!Directory.Exists(path))
                return;
            var inst = GetInstance();
            if (rwmodInfo != null && !inst.list.TrueForAll(m => m.rwmodInfo != rwmodInfo))
                throw new ArgumentException(
                    "The mod with that ModContentPack has already been added to PythonModList");
            var mod = new PythonMod(
                path,
                rwmodInfo.Identifier.ToLower(),
                rwmodInfo
                );
            inst.Add(mod);
        }

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
    }
}
