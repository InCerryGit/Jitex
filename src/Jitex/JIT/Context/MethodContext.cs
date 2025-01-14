﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Jitex.Intercept;
using Jitex.PE;
using Jitex.Runtime;
using Jitex.Utils;
using MethodBody = Jitex.Builder.Method.MethodBody;

namespace Jitex.JIT.Context
{
    /// <summary>
    /// Context for method resolution.
    /// </summary>
    public class MethodContext : ContextBase
    {
        private MethodBody? _body;

        /// <summary>
        /// Resolution mode.
        /// </summary>
        [Flags]
        public enum ResolveMode
        {
            /// <summary>
            /// MSIL (pre-compile)
            /// </summary>
            IL = 1 << 0,

            /// <summary>
            /// Bytecode (pos-compile)
            /// </summary>
            Native = 1 << 1,

            /// <summary>
            /// Detour
            /// </summary>
            Detour = 1 << 2,

            /// <summary>
            /// Native entry of method
            /// </summary>
            Entry = 1 << 4
        }

        /// <summary>
        /// Method original which will compiled.
        /// </summary>
        public MethodBase Method { get; }

        /// <summary>
        /// Body of method to compile.
        /// </summary>
        public MethodBody Body
        {
            get => _body ??= new MethodBody(Method);
            private set => _body = value;
        }

        /// <summary>
        /// If method is already resolved
        /// </summary>
        public bool IsResolved { get; private set; }

        /// <summary>
        /// Byte-code from method (only to ASM mode)
        /// </summary>
        internal byte[]? NativeCode { get; private set; }

        internal NativeCode? EntryContext { get; private set; }

        internal DetourContext? DetourContext { get; private set; }

        /// <summary>
        /// Resolution mode.
        /// </summary>
        internal ResolveMode Mode { get; private set; }

        internal MethodContext(MethodBase method, MethodBase? source, bool hasSource) : base(source, hasSource)
        {
            Method = method;
        }

        /// <summary>
        /// Resolve method by native code (asm).
        /// </summary>
        /// <param name="nativeCode">ASM to inject.</param>
        public void ResolveNative(IEnumerable<byte> nativeCode)
        {
            if (OSHelper.IsHardenedRuntime)
                throw new InvalidOperationException("Detour is not supported on Apple Silicon.");

            NativeCode = nativeCode.ToArray();
            IsResolved = true;
            Mode = ResolveMode.Native;
        }

        /// <summary>
        /// Resolve method by IL.
        /// </summary>
        /// <param name="il">IL instructions.</param>
        /// <param name="maxStack">Stack size to instrucitons.</param>
        public void ResolveIL(IEnumerable<byte> il, uint maxStack = 8)
        {
            Body = new MethodBody(il.ToArray(), maxStack);
            IsResolved = true;
            Mode = ResolveMode.IL;
        }

        /// <summary>
        /// Resolve method by Delegate.
        /// </summary>
        /// <typeparam name="T">Type of delegate.</typeparam>
        /// <param name="del">Delegate to resolve.</param>
        public void ResolveMethod<T>(T del) where T : Delegate => ResolveMethod(del.Method);

        /// <summary>
        /// Resolve method by MethodBase.
        /// </summary>
        /// <param name="method">Body of new method.</param>
        public void ResolveMethod(MethodBase method)
        {
            ResolveBody(new MethodBody(method));
        }

        /// <summary>
        /// Resolve method by MethodBody.
        /// </summary>
        /// <param name="methodBody">Body of new method.</param>
        public void ResolveBody(MethodBody methodBody)
        {
            Body = methodBody;
            IsResolved = true;
            Mode = ResolveMode.IL;
        }

        /// <summary>
        /// Detour to another method.
        /// </summary>
        /// <param name="method"></param>
        public DetourContext ResolveDetour(MethodInfo method) => ResolveDetour(method as MethodBase);

        /// <summary>
        /// Detour to a Delegate
        /// </summary>
        /// <param name="del"></param>
        public DetourContext ResolveDetour(Delegate del) => ResolveDetour<Delegate>(del);

        /// <summary>
        /// Detour to a delegate or method
        /// </summary>
        /// <param name="del">Delegate or method to detour.</param>
        /// <typeparam name="T"></typeparam>
        /// <returns>Context of detour.</returns>
        public DetourContext ResolveDetour<T>(T del) where T : Delegate => ResolveDetour(del.Method);

        /// <summary>
        /// Detour to an address.
        /// </summary>
        /// <param name="address">Address to detour.</param>
        /// <returns>Context of detour.</returns>
        public DetourContext ResolveDetour(IntPtr address)
        {
            DetourContext = new DetourContext(address);
            return ResolveDetour(DetourContext);
        }

        /// <summary>
        /// Detour to a method.
        /// </summary>
        /// <param name="method">Method to detour.</param>
        /// <returns>Context of detour.</returns>
        public DetourContext ResolveDetour(MethodBase method)
        {
            DetourContext = new DetourContext(method);
            return ResolveDetour(DetourContext);
        }

        private DetourContext ResolveDetour(DetourContext context)
        {
            if (OSHelper.IsHardenedRuntime)
                throw new InvalidOperationException("Detour is not supported on Apple Silicon.");

            IsResolved = true;
            Mode = ResolveMode.Detour;
            return context;
        }

        /// <summary>
        /// Resolve method by entry.
        /// </summary>
        /// <param name="entryMethod">New entry method.</param>
        public void ResolveEntry(MethodBase entryMethod)
        {
            NativeReader reader = new NativeReader(entryMethod.Module);
            IntPtr address;

            if (reader.IsReadyToRun(entryMethod))
            {
                address = MethodHelper.GetNativeAddress(entryMethod);
            }
            else
            {
                CancellationTokenSource source = new CancellationTokenSource(TimeSpan.FromSeconds(1));

                try
                {
                    var nativeCode = RuntimeMethodCache.GetNativeCodeAsync(entryMethod, source.Token)
                        .GetAwaiter()
                        .GetResult();

                    address = nativeCode.Address;
                }
                catch (OperationCanceledException)
                {
                    address = MethodHelper.GetNativeAddress(entryMethod);
                }
            }

            ResolveEntry(address);
        }

        /// <summary>
        /// Resolve method by entry.
        /// </summary>
        /// <param name="entryMethod">New entry method.</param>
        public void ResolveEntry(IntPtr entryMethod)
        {
            EntryContext = new NativeCode(entryMethod, 10);
            IsResolved = true;
            Mode = ResolveMode.Entry;
        }

        /// <summary>
        /// Intercept calls from method.
        /// </summary>
        /// <param name="reuseReferences">If should reuse references on assembly.</param>
        public void InterceptCall(bool reuseReferences = false)
        {
            InterceptorBuilder builder = new InterceptorBuilder(Method, Body);
            MethodBody body = builder.InjectInterceptor(reuseReferences);

            ResolveBody(body);
        }
    }
}