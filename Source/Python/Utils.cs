using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Linq;

using ScriptScope = Microsoft.Scripting.Hosting.ScriptScope;
using BuiltinFunction = IronPython.Runtime.Types.BuiltinFunction;
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

        //TODO: Extensions don't seem to work on certain builtin types. Try make a patch so it works
        /*public static BuiltinFunction Template(this IronPython.Runtime.Types.BuiltinMethodDescriptor bmd)
        {
            return (BuiltinFunction)typeof(IronPython.Runtime.Types.BuiltinMethodDescriptor).InvokeMember("_template",
                BindingFlags.GetField | BindingFlags.NonPublic | BindingFlags.Instance,
                null, bmd, null);
        }*/
    }

    public static class AddressedStorage
    {
        // General purpose object storage
        //
        // DESPITE THE USE OF A CRYPTO CIPHER, THIS CLASS PERFORMS ABSOLUTELY NO SECURITY FUNCTION.
        // DO NOT REUSE ANY OF THE CODE IN THIS CLASS FOR CRYPTO PURPOSES.
        // That said, the DES cipher is chosen because it has several properties that are desirable for key generation:
        // 1) values appear "unpredictable". This means: if client code is buggy and accidentally uses a key it got elsewhere
        //    (for example a commonly stored value such as the number 1), it will not by chance use someone else's key
        //    (which might hide the bug rather than throwing an exception).
        // 2) all new generated keys will be unique for the life of the program. This, somewhat relatedly to the last point,
        //    prevents bugs where a reuse of a key of a deleted item accidentally retrieves another item. Keyspace is
        //    sufficiently large enough that key collisions by buggy code remains negligible unless many thousands
        //    of keys are created every second (and even then the OS would likely run out of memory before any collision).
        // 3) the key is represented by a primative data type, which makes it passable in more contexts than it otherwise
        //    would be (for example to unmanaged code)
        private static bool _initialized = false;
        private static Dictionary<UInt64, object> contents = new Dictionary<UInt64, object>();
        private static UInt64 ctr = 0;
        private static System.Security.Cryptography.DES alg;
        private static System.Security.Cryptography.ICryptoTransform encryptor;

        private static void Initialize()
        {
            alg = System.Security.Cryptography.DES.Create();
            alg.Mode = System.Security.Cryptography.CipherMode.ECB;
            byte[] key = new byte[8];
            Util.Random.NextBytes(key);
            alg.Key = key;
            alg.IV = new byte[8];
            encryptor = alg.CreateEncryptor();
            _initialized = true;
        }

        private static UInt64 GenKey()
        {
            if (!_initialized)
                Initialize();
            byte[] _in = BitConverter.GetBytes(ctr);
            byte[] _out = new byte[8];
            encryptor.TransformBlock(_in, 0, 8, _out, 0);
            ctr++;
            return BitConverter.ToUInt64(_out, 0);
        }

        public static UInt64 Store(object o)
        {
            var k = GenKey();
            contents[k] = o;
            return k;
        }
        public static bool IsValid(UInt64 k) => contents.ContainsKey(k);
        public static object Fetch(UInt64 k) => contents[k];
        public static void Delete(UInt64 k) => contents.Remove(k);
        public static void Modify(UInt64 k, object o)
        {
            if (!contents.ContainsKey(k))
                throw new ArgumentException("Addressed object does not exist");
            contents[k] = o;
        }

        public class Handle<T>
        {
            //A class to assist in changing object lifespan from explicit to ownership-based.
            //The owner will create a Handle, and then pass out Address to clients.
            //Handle also adds some convenience by retaining Type information.
            //IDisposable could easily be implemented at a later time if desired
            //https://docs.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-dispose

            private UInt64 _key;

            public Handle(T o)
            {
                _key = Store(o);
            }

            ~Handle()
            {
                Delete(_key); //doesn't throw an exception
            }

            public bool IsValid => AddressedStorage.IsValid(_key);
            public UInt64 Key => _key;
            public T Object
            {
                get
                {
                    return (T)Fetch(_key);
                }
                set
                {
                    Modify(_key, value);
                }
            }
        }
    }

    public static partial class DebugUtil
    {
        public static void Trigger()
        {
        }
    }
}
