using System;
using System.Collections.Generic;
using System.IO;

namespace Python
{
    public static class Util
    {
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

        //do I actually need to override this?
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
}
