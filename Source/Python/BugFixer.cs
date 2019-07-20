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

            Fix_System_Text_Encoding_GetEncodings();
        }

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
    }
}
