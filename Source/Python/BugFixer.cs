using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;

namespace Python
{
    internal static class BugFixer
    {
        private static bool ran = false;

        internal static void Run()
        {
            if (ran) throw new InvalidOperationException("BugFixer was already run.");
            ran = true;

            Fix_System_Text_Encoding_GetEncodings(); //1
            Fix_System_Collections_Generic_HashSet_CreateSetComparer.Run(); //2
        }

        #region Fix 1
        private static void Fix_System_Text_Encoding_GetEncodings()
        {
            //GetEncodings() current behaviour is to return a mix of broken encodings interspersed with the valid ones.
            //This problem seems to be specific to this version of Mono.
            //The following nasty hack removes the broken encodings.
            //a related article:
            //https://xamarin.github.io/bugzilla-archives/81/8117/bug.html

            var valid_encodings = new List<EncodingInfo>();

            foreach (EncodingInfo info in Encoding.GetEncodings())
            {
                try { info.GetEncoding(); }
                catch { continue; }
                valid_encodings.Add(info);
            }

            typeof(Encoding).GetField("encoding_infos", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, valid_encodings.ToArray());
        }
        #endregion

        #region Fix 2
        private static class Fix_System_Collections_Generic_HashSet_CreateSetComparer
        {
            private static Type[] patchForTypes = {
                typeof(IronPython.Runtime.Types.PythonType),
                typeof(string)
            };

            public static void Run()
            {
                var harmony = Util.Harmony;
                foreach (var t in patchForTypes)
                {
                    harmony.Patch(
                        original: typeof(System.Collections.Generic.HashSet<>).MakeGenericType(t)
                        .GetMethod("CreateSetComparer", BindingFlags.Public | BindingFlags.Static),
                        prefix: new Harmony.HarmonyMethod(typeof(Fix_System_Collections_Generic_HashSet_CreateSetComparer)
                        .GetMethod("PatchMethod", BindingFlags.NonPublic | BindingFlags.Static)
                        .MakeGenericMethod(t))
                        );
                }
            }

            private static bool PatchMethod<T>(ref IEqualityComparer<HashSet<T>> __result)
            {
                __result = new HashSetEqualityComparer<T>();
                return false;
            }

            private class HashSetEqualityComparer<T> : IEqualityComparer<HashSet<T>>
            {
                public bool Equals(HashSet<T> lhs, HashSet<T> rhs)
                {
                    if (lhs == rhs)
                        return true;

                    if (lhs == null || rhs == null || lhs.Count != rhs.Count)
                        return false;

                    foreach (var item in lhs)
                        if (!rhs.Contains(item))
                            return false;

                    return true;
                }

                public int GetHashCode(HashSet<T> hashset)
                {
                    if (hashset == null)
                        return 0;

                    IEqualityComparer<T> comparer = EqualityComparer<T>.Default;
                    int hash = 0;
                    foreach (var item in hashset)
                        hash ^= comparer.GetHashCode(item);

                    return hash;
                }
            }
        }
        #endregion
    }
}
