using System.Reflection;

namespace PythonLoader
{
    public class Mod : Verse.Mod
    {
        public Mod(Verse.ModContentPack content) : base(content)
        {
            string path = System.IO.Path.Combine(Content.RootDir, "Assemblies2/Python.dll");
            Assembly py = Assembly.LoadFrom(path);
            var buttonInstaller =
                py.GetType("Python.ConsoleButton").GetMethod("Install", BindingFlags.Public | BindingFlags.Static);
            buttonInstaller.Invoke(null, new object[] { });
        }
    }
}
