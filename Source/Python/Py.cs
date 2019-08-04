using Microsoft.Scripting.Hosting;

namespace Python
{
    public static class Py
    {
        private static ScriptRuntime _runtime = null;
        public static ScriptRuntime Runtime {
            get {
                initAsNeeded();
                return _runtime;
            }
        }
        private static ScriptEngine _engine;
        public static ScriptEngine Engine {
            get {
                initAsNeeded();
                return _engine;
            }
        }

        public static ScriptScope CreateScope()
        {
            return Runtime.CreateScope("IronPython");
        }

        private static ScriptScope mainScope;

        private static void initAsNeeded()
        {
            if (_runtime != null)
                return;

            BugFixer.Run();

            string langQName = typeof(IronPython.Runtime.PythonContext).AssemblyQualifiedName;
            var langSetup = new LanguageSetup(langQName, "IronPython",
                new string[] { "IronPython", "Python", "py" }, new string[] { ".py" });
            var setup = new ScriptRuntimeSetup();
            langSetup.ExceptionDetail = true;
            // options can be found in ironpython2-ipy-2.7.7\Languages\IronPython\IronPython\Runtime\PythonOptions.cs
            langSetup.Options["Optimize"] = false;
            langSetup.Options["StripDocStrings"] = false;
            langSetup.Options["Frames"] = true;
            langSetup.Options["Tracing"] = true;
            //
            setup.LanguageSetups.Add(langSetup);
            //this is responsible for python being able to access private members on CLR objects:
            setup.PrivateBinding = true;

            _runtime = new ScriptRuntime(setup);
            _engine = _runtime.GetEngine("IronPython");

            //This works for the simple purpose of creating a __main__ module
            //inspect.stack() should work after doing this.
            //Not sure if there's a better way, still not entirely sure how modules work in IronPython
            //This solution is from:
            //https://stackoverflow.com/questions/8264596/how-do-i-set-name-to-main-when-using-ironpython-hosted
            var pco = (IronPython.Compiler.PythonCompilerOptions)_engine.GetCompilerOptions();
            pco.ModuleName = "__main__";
            pco.Module |= IronPython.Runtime.ModuleOptions.Initialize;
            var source = Engine.CreateScriptSourceFromString("import sys");
            CompiledCode compiled = source.Compile(pco);
            mainScope = CreateScope();
            compiled.Execute(mainScope);

            //more options
            string searchpath = System.IO.Path.Combine(Util.ModBasePath, "IronPython-2.7.7/Lib/").Replace(@"\", @"\\");
            _engine.SetSearchPaths(new string[] { searchpath });
            //var pco = (IronPython.Compiler.PythonCompilerOptions)_engine.GetCompilerOptions();
            //pco.ModuleName = "__main__";
            //pco.Module |= IronPython.Runtime.ModuleOptions.Initialize;
            _runtime.LoadAssembly(System.Reflection.Assembly.GetExecutingAssembly());
            _runtime.LoadAssembly(typeof(Verse.Game).Assembly);
            _runtime.LoadAssembly(typeof(Harmony.HarmonyInstance).Assembly);
        }
    }
}
