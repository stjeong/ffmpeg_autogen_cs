using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace FFmpeg.OSDepends
{
    static internal class NativeMethods
    {
        [DllImport("Kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
        internal static unsafe extern void MoveMemory(byte* dest, byte* src, int size);
    }
}
