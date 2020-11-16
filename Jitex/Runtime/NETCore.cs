﻿using System;
using System.Runtime.InteropServices;

namespace Jitex.Runtime
{
    internal sealed class NETCore : RuntimeFramework
    {
#if Windows
        private const string jitLibraryName = "clrjit.dll";
#elif Linux
        private const string jitLibraryName = "libclrjit.so";
#else
        private const string jitLibraryName = "libclrjit.dylib";
#endif
        [DllImport(jitLibraryName, CallingConvention = CallingConvention.StdCall, SetLastError = true, EntryPoint = "getJit", BestFitMapping = true)]
        private static extern IntPtr GetJit();

        public NETCore() : base(true)
        {

        }

        protected override IntPtr GetJitAddress()
        {
            return GetJit();
        }
    }
}
