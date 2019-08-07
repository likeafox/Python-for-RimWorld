using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Linq;

using ScriptScope = Microsoft.Scripting.Hosting.ScriptScope;
using IronPython.Runtime;
using Harmony;

namespace Python
{
    public static class Util
    {
        private static HarmonyInstance _harmony = null;
        public static HarmonyInstance Harmony
        {
            get
            {
                if (_harmony == null)
                {
                    _harmony = HarmonyInstance.Create("likeafox.rimworld.python");
                }
                return _harmony;
            }
        }

        private static string _modBasePath = null;
        public static string ModBasePath
        {
            get
            {
                if (_modBasePath == null)
                {
                    string assDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    _modBasePath = Directory.GetParent(assDir).FullName;
                }
                return _modBasePath;
            }
        }

        public static string BundledModulesDir => Path.Combine(ModBasePath, "PythonModules/");

        public static string ResourcePath(string filename = "")
        {
            return Path.Combine(Path.Combine(ModBasePath, "Resources/"), filename);
        }

        private static System.Random _random = null;
        public static System.Random Random {
            get
            {
                if (_random == null)
                    _random = new System.Random((int)DateTime.Now.Ticks ^ 78563851);
                return _random;
            }
        }
    }

    public struct IntVec2 : IEquatable<IntVec2>
    {
        public int x, y;

        public IntVec2(int x, int y) { this.x = x; this.y = y; }

        public bool Equals(IntVec2 other)
        {
            return other.x == x && other.y == y;
        }

        public override bool Equals(object obj)
        {
            if ((obj == null) || !this.GetType().Equals(obj.GetType()))
            {
                return false;
            }
            return Equals((IntVec2)obj);
        }

        public override int GetHashCode() => 10007 * y + x;
    }

    public struct ComparablePath : IEquatable<ComparablePath>, IComparable<ComparablePath>
    {
        public readonly string originalPath;
        public readonly System.Collections.ObjectModel.ReadOnlyCollection<string> dirParts;
        public readonly string filePart;
        public readonly string reconstructedPath;

        public ComparablePath(string path)
        {
            if (path == null)
                throw new ArgumentException("path cannot be null");
            originalPath = path;
            string[] parts = Regex.Split(path, @"[/\\]+", RegexOptions.Compiled);
            dirParts = Array.AsReadOnly(parts.Take(parts.Length - 1).ToArray());
            filePart = parts[parts.Length - 1];
            reconstructedPath = string.Join(Path.DirectorySeparatorChar.ToString(), parts);
        }

        public bool IsFile => filePart != "";
        public bool IsDir => filePart == "";

        private IEnumerable<string> _GetBranchParts(ComparablePath hasbranch)
        {
            if (!this.IsDir)
                throw new InvalidOperationException(
                    "This method was called on a file path (" + originalPath
                    + ") but may only be called on a directory path");
            if (this.dirParts.Count > hasbranch.dirParts.Count)
                throw new ArgumentException();
            for (int i = 0; i < this.dirParts.Count; i++)
                if (this.dirParts[i] != hasbranch.dirParts[i])
                    throw new ArgumentException();
            string sep = Path.DirectorySeparatorChar.ToString();
            foreach (string part in hasbranch.dirParts.Skip(this.dirParts.Count))
                yield return part + sep;
            if (hasbranch.IsFile)
                yield return filePart;
        }

        public bool IsSameOrParentDirOf(ComparablePath subpath)
        {
            try { _GetBranchParts(subpath).GetEnumerator().MoveNext(); }
            catch (ArgumentException) { return false; }
            return true;
        }

        public string[] GetSubpathDiff(ComparablePath subpath)
        {
            try { return _GetBranchParts(subpath).ToArray(); }
            catch (ArgumentException) { return null; }
        }

        private bool SameType(object obj) => (obj != null) && this.GetType().Equals(obj.GetType());
        public bool Equals(ComparablePath other) => reconstructedPath == other.reconstructedPath;
        public override bool Equals(object obj) => SameType(obj) && Equals((ComparablePath)obj);

        public int CompareTo(ComparablePath other)
        {
            int dirPartsToCompare = Math.Min(this.dirParts.Count, other.dirParts.Count);
            for (int i = 0; i < dirPartsToCompare; i++)
                if (this.dirParts[i] != other.dirParts[i])
                    return this.dirParts[i].CompareTo(other.dirParts[i]);
            int partCountCompare = this.dirParts.Count.CompareTo(other.dirParts.Count);
            if (partCountCompare != 0)
                return partCountCompare;
            return this.filePart.CompareTo(other.filePart);
        }

        public int CompareTo(object obj)
        {
            if (!SameType(obj))
                throw new ArgumentException("type mismatch");
            return CompareTo((ComparablePath)obj);
        }

        public override int GetHashCode() => reconstructedPath.GetHashCode();
        public override string ToString() => reconstructedPath;
    }

    public static class StringUtil
    {
        public static IEnumerable<string> SplitLines(this string text)
        {
            var reader = new StringReader(text);
            string line;
            while (true)
            {
                line = reader.ReadLine();
                if (line == null) yield break;
                yield return line;
            }
        }
    }

    public static class StandardScriptingExtensions
    {
        public static string PythonFolder(this Verse.ModContentPack mcp)
        {
            return Path.Combine(mcp.RootDir, "Python/");
        }

        public static ModuleContext GetModuleContext(this ScriptScope scriptScope)
        {
            var scope = (Microsoft.Scripting.Runtime.Scope)typeof(ScriptScope).InvokeMember("_scope",
                BindingFlags.GetField | BindingFlags.NonPublic | BindingFlags.Instance,
                null, scriptScope, null);
            DefaultContext.DefaultPythonContext.EnsureScopeExtension(scope);
            var ext = scope.GetExtension(DefaultContext.DefaultPythonContext.ContextId);
            if (ext == null)
                return null;
            Type PythonScopeExtension = typeof(PythonContext).Assembly.GetType("IronPython.Runtime.PythonScopeExtension");
            ModuleContext mod = (ModuleContext)PythonScopeExtension.GetProperty("ModuleContext").GetValue(ext, null);
            return mod;
        }

        public static void AddExtensionType(this ModuleContext mc, Type type)
        {
            System.Type ExtensionMethodSet = typeof(PythonContext).Assembly.GetType("IronPython.Runtime.ExtensionMethodSet");
            var extsetProp = typeof(ModuleContext).GetProperty("ExtensionMethods",
                BindingFlags.NonPublic | BindingFlags.Instance);
            object existingExt = extsetProp.GetValue(mc, null);
            var args = new object[] { mc.Context, existingExt, ClrModule.GetPythonType(type) };
            object newExt = ExtensionMethodSet.GetMethod("AddType").Invoke(null, args);
            extsetProp.SetValue(mc, newExt, null);
        }
    }

    public static partial class DebugUtil
    {
        public static void Trigger()
        {
        }
    }
}
