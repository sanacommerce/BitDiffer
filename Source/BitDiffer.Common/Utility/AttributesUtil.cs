using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitDiffer.Common.Utility
{
    public static class AttributesUtil
    {
        public static bool IsAttributeSupported(string fullName)
        {
            return fullName != "System.Diagnostics.CodeAnalysis.SuppressMessageAttribute";
        }
    }
}
