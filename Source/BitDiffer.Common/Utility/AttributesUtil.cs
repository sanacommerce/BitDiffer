using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitDiffer.Common.Utility
{
    public static class AttributesUtil
    {
        public static IEnumerable<string> GetNotSupportedAttributesFullNames()
        {
            yield return "System.Diagnostics.CodeAnalysis.SuppressMessageAttribute";
        }

        public static bool IsAttributeSupported(string fullName)
        {
            return GetNotSupportedAttributesFullNames().All(n => n != fullName);
        }
    }
}
