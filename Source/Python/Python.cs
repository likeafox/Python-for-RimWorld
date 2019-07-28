using Microsoft.Scripting.Hosting;

namespace Python
{
    public static class Python
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

        private static void initAsNeeded()
        {
            if (_runtime != null)
                return;

            BugFixer.Run();

            string langQName = typeof(IronPython.Runtime.PythonContext).AssemblyQualifiedName;
            var langSetup = new LanguageSetup(langQName, "IronPython",
                new string[] { "IronPython", "Python", "py" }, new string[] { ".py" });
            var setup = new ScriptRuntimeSetup();
            setup.LanguageSetups.Add(langSetup);
            setup.PrivateBinding = true;

            _runtime = new ScriptRuntime(setup);
            _engine = _runtime.GetEngine("IronPython");
            string searchpath = System.IO.Path.Combine(Util.ModBasePath, "IronPython-2.7.7/Lib/").Replace(@"\", @"\\");
            _engine.SetSearchPaths(new string[] { searchpath });

            _runtime.LoadAssembly(System.Reflection.Assembly.GetExecutingAssembly());
            _runtime.LoadAssembly(typeof(Verse.Game).Assembly);
            _runtime.LoadAssembly(typeof(Harmony.HarmonyInstance).Assembly);
        }
    }
}
