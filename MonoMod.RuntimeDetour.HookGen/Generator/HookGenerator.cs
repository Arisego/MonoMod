﻿using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MonoMod.RuntimeDetour.HookGen.Generator {
    class HookGenerator {

        const string ObsoleteMessageBackCompat = "This method only exists for backwards-compatibility purposes.";

        public MonoModder Modder;

        public ModuleDefinition OutputModule;

        public string Namespace;
        public bool HookOrig;
        public bool HookPrivate;

        public ModuleDefinition module_HookGen;

        public TypeReference t_MulticastDelegate;
        public TypeReference t_IAsyncResult;
        public TypeReference t_AsyncCallback;
        public TypeReference t_MethodBase;
        public TypeReference t_RuntimeMethodHandle;
        public TypeReference t_EditorBrowsableState;

        public TypeReference t_HookEndpointManager;
        public TypeDefinition td_HookEndpoint;
        public TypeReference t_HookEndpoint;

        public MethodReference m_Object_ctor;
        public MethodReference m_ObsoleteAttribute_ctor;
        public MethodReference m_EditorBrowsableAttribute_ctor;

        public MethodReference m_GetMethodFromHandle;
        public MethodReference m_Get;
        public MethodReference m_Set;
        public MethodReference m_Add;
        public MethodReference m_Remove;

        public string HookWrapperName;
        public TypeDefinition td_HookWrapper;
        public MethodDefinition md_HookWrapper_ctor;
        public FieldDefinition fd_HookWrapper_Endpoint;

        public HookGenerator(MonoModder modder, string name) {
            Modder = modder;

            OutputModule = ModuleDefinition.CreateModule(name, new ModuleParameters {
                Architecture = modder.Module.Architecture,
                AssemblyResolver = modder.Module.AssemblyResolver,
                Kind = ModuleKind.Dll,
                Runtime = modder.Module.Runtime
            });

            Namespace = Environment.GetEnvironmentVariable("MONOMOD_HOOKGEN_NAMESPACE");
            if (string.IsNullOrEmpty(Namespace))
                Namespace = "On";
            HookOrig = Environment.GetEnvironmentVariable("MONOMOD_HOOKGEN_ORIG") == "1";
            HookPrivate = Environment.GetEnvironmentVariable("MONOMOD_HOOKGEN_PRIVATE") == "1";
            HookWrapperName = Environment.GetEnvironmentVariable("MONOMOD_HOOKGEN_WRAPPER");
            if (string.IsNullOrEmpty(HookWrapperName))
                HookWrapperName = $"{Namespace}.{modder.Module.Assembly.Name.Name}Hook";

            modder.MapDependency(modder.Module, "MonoMod.RuntimeDetour.HookGen");
            if (!modder.DependencyCache.TryGetValue("MonoMod.RuntimeDetour.HookGen", out module_HookGen))
                throw new FileNotFoundException("MonoMod.RuntimeDetour.HookGen not found!");

            t_MulticastDelegate = OutputModule.ImportReference(modder.FindType("System.MulticastDelegate"));
            t_IAsyncResult = OutputModule.ImportReference(modder.FindType("System.IAsyncResult"));
            t_AsyncCallback = OutputModule.ImportReference(modder.FindType("System.AsyncCallback"));
            t_MethodBase = OutputModule.ImportReference(modder.FindType("System.Reflection.MethodBase"));
            t_RuntimeMethodHandle = OutputModule.ImportReference(modder.FindType("System.RuntimeMethodHandle"));
            t_EditorBrowsableState = OutputModule.ImportReference(modder.FindType("System.ComponentModel.EditorBrowsableState"));

            TypeDefinition td_HookEndpointManager = module_HookGen.GetType("MonoMod.RuntimeDetour.HookGen.HookEndpointManager");
            t_HookEndpointManager = OutputModule.ImportReference(td_HookEndpointManager);
            td_HookEndpoint = module_HookGen.GetType("MonoMod.RuntimeDetour.HookGen.HookEndpoint`1");
            t_HookEndpoint = OutputModule.ImportReference(td_HookEndpoint);

            m_Object_ctor = OutputModule.ImportReference(modder.FindType("System.Object").Resolve().FindMethod("System.Void .ctor()"));
            m_ObsoleteAttribute_ctor = OutputModule.ImportReference(modder.FindType("System.ObsoleteAttribute").Resolve().FindMethod("System.Void .ctor(System.String,System.Boolean)"));
            m_EditorBrowsableAttribute_ctor = OutputModule.ImportReference(modder.FindType("System.ComponentModel.EditorBrowsableAttribute").Resolve().FindMethod("System.Void .ctor(System.ComponentModel.EditorBrowsableState)"));

            m_GetMethodFromHandle = OutputModule.ImportReference(
                new MethodReference("GetMethodFromHandle", t_MethodBase, t_MethodBase) {
                    Parameters = {
                        new ParameterDefinition(t_RuntimeMethodHandle)
                    }
                }
            );
            m_Get = OutputModule.ImportReference(td_HookEndpointManager.FindMethod("Get"));
            m_Set = OutputModule.ImportReference(td_HookEndpointManager.FindMethod("Set"));
            m_Add = OutputModule.ImportReference(td_HookEndpointManager.FindMethod("Add"));
            m_Remove = OutputModule.ImportReference(td_HookEndpointManager.FindMethod("Remove"));

        }

        public void Generate() {
            // Generate the hook wrapper before generating anything else.
            // This is required to prevent mods from depending on HookGen itself.
            if (td_HookWrapper == null) {
                Modder.LogVerbose($"[HookGen] Generating hook wrapper {HookWrapperName}");
                int namespaceIndex = HookWrapperName.LastIndexOf(".");
                td_HookWrapper = new TypeDefinition(
                    namespaceIndex < 0 ? "" : HookWrapperName.Substring(0, namespaceIndex),
                    namespaceIndex < 0 ? HookWrapperName : HookWrapperName.Substring(namespaceIndex + 1),
                    TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
                    OutputModule.TypeSystem.Object
                );
                if (!td_HookWrapper.Name.EndsWith("`1"))
                    td_HookWrapper.Name += "`1";

                td_HookWrapper.CustomAttributes.Add(GenerateEditorBrowsable(EditorBrowsableState.Never));

                foreach (GenericParameter genParam in td_HookEndpoint.GenericParameters)
                    td_HookWrapper.GenericParameters.Add(genParam.Relink(Relinker, td_HookWrapper));

                ILProcessor il;

                md_HookWrapper_ctor = new MethodDefinition(".ctor",
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    OutputModule.TypeSystem.Void
                );
                md_HookWrapper_ctor.Body = new MethodBody(md_HookWrapper_ctor);
                il = md_HookWrapper_ctor.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, OutputModule.ImportReference(m_Object_ctor));
                il.Emit(OpCodes.Ret);
                td_HookWrapper.Methods.Add(md_HookWrapper_ctor);

                fd_HookWrapper_Endpoint = new FieldDefinition("Endpoint", FieldAttributes.Public, t_HookEndpoint);
                td_HookWrapper.Fields.Add(fd_HookWrapper_Endpoint);

                // Proxy all public methods, events and properties from HookEndpoint to HookWrapper.
                foreach (MethodDefinition method in td_HookEndpoint.Methods) {
                    if (method.IsRuntimeSpecialName || !method.IsPublic)
                        continue;

                    MethodDefinition proxy = new MethodDefinition(method.Name, method.Attributes, method.ReturnType);
                    td_HookWrapper.Methods.Add(proxy);

                    foreach (GenericParameter genParam in method.GenericParameters)
                        proxy.GenericParameters.Add(genParam.Relink(Relinker, proxy));

                    foreach (ParameterDefinition param in method.Parameters) {
                        TypeReference paramType = param.ParameterType;

                        paramType = param.ParameterType.Relink(WrappedRelinker, proxy);

                        proxy.Parameters.Add(new ParameterDefinition(
                            param.Name,
                            param.Attributes,
                            paramType
                        ) {
                            Constant = param.Constant
                        });
                    }

                    proxy.ReturnType = proxy.ReturnType?.Relink(WrappedRelinker, proxy);

                    proxy.Body = new MethodBody(proxy);
                    il = proxy.Body.GetILProcessor();

                    if (method.ReturnType.GetElementType().FullName == t_HookEndpoint.FullName) {
                        il.Emit(OpCodes.Newobj, md_HookWrapper_ctor);
                        il.Emit(OpCodes.Dup);
                    }

                    if (!method.IsStatic) {
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldfld, fd_HookWrapper_Endpoint);
                        for (int i = 0; i < method.Parameters.Count; i++) {
                            il.Emit(OpCodes.Ldarg, i + 1);
                            if (method.Parameters[i].ParameterType.GetElementType().FullName == t_HookEndpoint.FullName)
                                il.Emit(OpCodes.Ldfld, fd_HookWrapper_Endpoint);
                        }
                        il.Emit(OpCodes.Callvirt, method.Relink(Relinker, proxy));
                    } else {
                        for (int i = 0; i < method.Parameters.Count; i++) {
                            il.Emit(OpCodes.Ldarg, i);
                            if (method.Parameters[i].ParameterType.GetElementType().FullName == t_HookEndpoint.FullName)
                                il.Emit(OpCodes.Ldfld, fd_HookWrapper_Endpoint);
                        }
                        il.Emit(OpCodes.Callvirt, method.Relink(Relinker, proxy));
                    }

                    if (method.ReturnType.GetElementType().FullName == t_HookEndpoint.FullName)
                        il.Emit(OpCodes.Stfld, fd_HookWrapper_Endpoint);
                    il.Emit(OpCodes.Ret);

                }

                foreach (PropertyDefinition prop in td_HookEndpoint.Properties) {
                    PropertyDefinition proxy = new PropertyDefinition(prop.Name, prop.Attributes, prop.PropertyType.Relink(WrappedRelinker, td_HookWrapper));
                    td_HookWrapper.Properties.Add(proxy);

                    MethodDefinition proxyMethod;

                    if (prop.GetMethod != null) {
                        if ((proxyMethod = td_HookWrapper.FindMethod(prop.GetMethod.GetFindableID(withType: false))) == null)
                            goto Next;
                        proxy.GetMethod = proxyMethod;
                    }

                    if (prop.SetMethod != null) {
                        if ((proxyMethod = td_HookWrapper.FindMethod(prop.SetMethod.GetFindableID(withType: false))) == null)
                            goto Next;
                        proxy.SetMethod = proxyMethod;
                    }

                    foreach (MethodDefinition method in prop.OtherMethods) {
                        if ((proxyMethod = td_HookWrapper.FindMethod(method.GetFindableID(withType: false))) == null)
                            goto Next;
                        proxy.OtherMethods.Add(proxyMethod);
                    }

                    Next: continue;
                }

                foreach (EventDefinition evt in td_HookEndpoint.Events) {
                    EventDefinition proxy = new EventDefinition(evt.Name, evt.Attributes, evt.EventType.Relink(WrappedRelinker, td_HookWrapper));
                    td_HookWrapper.Events.Add(proxy);

                    MethodDefinition proxyMethod;

                    if (evt.AddMethod != null) {
                        if ((proxyMethod = td_HookWrapper.FindMethod(evt.AddMethod.GetFindableID(withType: false))) == null)
                            goto Next;
                        proxy.AddMethod = proxyMethod;
                    }

                    if (evt.RemoveMethod != null) {
                        if ((proxyMethod = td_HookWrapper.FindMethod(evt.RemoveMethod.GetFindableID(withType: false))) == null)
                            goto Next;
                        proxy.RemoveMethod = proxyMethod;
                    }

                    if (evt.InvokeMethod != null) {
                        if ((proxyMethod = td_HookWrapper.FindMethod(evt.InvokeMethod.GetFindableID(withType: false))) == null)
                            goto Next;
                        proxy.InvokeMethod = proxyMethod;
                    }

                    foreach (MethodDefinition method in evt.OtherMethods) {
                        if ((proxyMethod = td_HookWrapper.FindMethod(method.GetFindableID(withType: false))) == null)
                            goto Next;
                        proxy.OtherMethods.Add(proxyMethod);
                    }

                    Next: continue;
                }

                OutputModule.Types.Add(td_HookWrapper);
            }

            foreach (TypeDefinition type in Modder.Module.Types) {
                TypeDefinition hookType = GenerateFor(type);
                if (hookType == null)
                    continue;
                OutputModule.Types.Add(hookType);
            }
        }

        public TypeDefinition GenerateFor(TypeDefinition type) {
            if (type.HasGenericParameters ||
                type.IsRuntimeSpecialName ||
                type.Name.StartsWith("<"))
                return null;

            if (!HookPrivate && type.IsNotPublic)
                return null;

            Modder.LogVerbose($"[HookGen] Generating for type {type.FullName}");

            TypeDefinition hookType = new TypeDefinition(
                type.IsNested ? null : (Namespace + (string.IsNullOrEmpty(type.Namespace) ? "" : ("." + type.Namespace))),
                type.Name,
                (type.IsNested ? TypeAttributes.NestedPublic : TypeAttributes.Public) | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class,
                OutputModule.TypeSystem.Object
            );

            bool add = false;

            foreach (MethodDefinition method in type.Methods)
                add |= GenerateFor(hookType, method);

            foreach (TypeDefinition nested in type.NestedTypes) {
                TypeDefinition hookNested = GenerateFor(nested);
                if (hookNested == null)
                    continue;
                add = true;
                hookType.NestedTypes.Add(hookNested);
            }

            if (!add)
                return null;
            return hookType;
        }

        public bool GenerateFor(TypeDefinition hookType, MethodDefinition method) {
            if (method.HasGenericParameters ||
                (method.IsSpecialName && !method.IsConstructor))
                return false;

            if (!HookOrig && method.Name.StartsWith("orig_"))
                return false;
            if (!HookPrivate && method.IsPrivate)
                return false;

            int index = method.DeclaringType.Methods.Where(other => !other.HasGenericParameters && other.Name == method.Name).ToList().IndexOf(method);
            string suffix = "";
            if (index != 0) {
                suffix = index.ToString();
                do {
                    suffix = "_" + suffix;
                } while (method.DeclaringType.Methods.Any(other => !other.HasGenericParameters && other.Name == (method.Name + suffix)));
            }
            string name = method.Name;
            if (name.StartsWith("."))
                name = name.Substring(1);
            name = name + suffix;

            // TODO: Fix possible conflict when other members with the same names exist.

            TypeDefinition delOrig = GenerateDelegateFor(hookType, method);
            delOrig.Name = "orig_" + name;
            delOrig.CustomAttributes.Add(GenerateEditorBrowsable(EditorBrowsableState.Never));
            hookType.NestedTypes.Add(delOrig);

            TypeDefinition delHook = GenerateDelegateFor(hookType, method);
            delHook.Name = "hook_" + name;
            MethodDefinition delHookInvoke = delHook.FindMethod("Invoke");
            delHookInvoke.Parameters.Insert(0, new ParameterDefinition("orig", ParameterAttributes.None, delOrig));
            MethodDefinition delHookBeginInvoke = delHook.FindMethod("BeginInvoke");
            delHookBeginInvoke.Parameters.Insert(0, new ParameterDefinition("orig", ParameterAttributes.None, delOrig));
            delHook.CustomAttributes.Add(GenerateEditorBrowsable(EditorBrowsableState.Never));
            hookType.NestedTypes.Add(delHook);

            GenericInstanceType endpointType = new GenericInstanceType(td_HookWrapper);
            endpointType.GenericArguments.Add(delHook);
            GenericInstanceMethod endpointMethod;

            ILProcessor il;

            MethodDefinition get = new MethodDefinition(
                "get_" + name,
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.Static,
                endpointType
            ) {
                IsIL = true,
                IsManaged = true
            };
            get.Body = new MethodBody(get);
            il = get.Body.GetILProcessor();
            il.Emit(OpCodes.Newobj, md_HookWrapper_ctor);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldtoken, OutputModule.ImportReference(method));
            il.Emit(OpCodes.Call, m_GetMethodFromHandle);
            endpointMethod = new GenericInstanceMethod(m_Get);
            endpointMethod.GenericArguments.Add(delHook);
            il.Emit(OpCodes.Call, endpointMethod);
            il.Emit(OpCodes.Stfld, fd_HookWrapper_Endpoint);
            il.Emit(OpCodes.Ret);
            hookType.Methods.Add(get);

            MethodDefinition set = new MethodDefinition(
                "set_" + name,
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.Static,
                OutputModule.TypeSystem.Void
            ) {
                IsIL = true,
                IsManaged = true
            };
            set.Parameters.Add(new ParameterDefinition(null, ParameterAttributes.None, endpointType));
            set.Body = new MethodBody(set);
            il = set.Body.GetILProcessor();
            il.Emit(OpCodes.Ldtoken, OutputModule.ImportReference(method));
            il.Emit(OpCodes.Call, m_GetMethodFromHandle);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, fd_HookWrapper_Endpoint);
            endpointMethod = new GenericInstanceMethod(m_Set);
            endpointMethod.GenericArguments.Add(delHook);
            il.Emit(OpCodes.Call, endpointMethod);
            il.Emit(OpCodes.Ret);
            hookType.Methods.Add(set);

            PropertyDefinition prop = new PropertyDefinition(name, PropertyAttributes.None, endpointType) {
                GetMethod = get,
                SetMethod = set
            };
            hookType.Properties.Add(prop);

            // Legacy event proxies.

            MethodDefinition add = new MethodDefinition(
                "add_" + name,
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.Static,
                OutputModule.TypeSystem.Void
            ) {
                IsIL = true,
                IsManaged = true
            };
            add.CustomAttributes.Add(GenerateObsolete(ObsoleteMessageBackCompat, true));
            add.CustomAttributes.Add(GenerateEditorBrowsable(EditorBrowsableState.Never));
            add.Parameters.Add(new ParameterDefinition(null, ParameterAttributes.None, delHook));
            add.Body = new MethodBody(add);
            il = add.Body.GetILProcessor();
            il.Emit(OpCodes.Ldtoken, OutputModule.ImportReference(method));
            il.Emit(OpCodes.Call, m_GetMethodFromHandle);
            il.Emit(OpCodes.Ldarg_0);
            endpointMethod = new GenericInstanceMethod(m_Add);
            endpointMethod.GenericArguments.Add(delHook);
            il.Emit(OpCodes.Call, endpointMethod);
            il.Emit(OpCodes.Ret);
            hookType.Methods.Add(add);

            MethodDefinition remove = new MethodDefinition(
                "remove_" + name,
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.Static,
                OutputModule.TypeSystem.Void
            ) {
                IsIL = true,
                IsManaged = true
            };
            remove.CustomAttributes.Add(GenerateObsolete(ObsoleteMessageBackCompat, true));
            remove.CustomAttributes.Add(GenerateEditorBrowsable(EditorBrowsableState.Never));
            remove.Parameters.Add(new ParameterDefinition(null, ParameterAttributes.None, delHook));
            remove.Body = new MethodBody(remove);
            il = remove.Body.GetILProcessor();
            il.Emit(OpCodes.Ldtoken, OutputModule.ImportReference(method));
            il.Emit(OpCodes.Call, m_GetMethodFromHandle);
            il.Emit(OpCodes.Ldarg_0);
            endpointMethod = new GenericInstanceMethod(m_Remove);
            endpointMethod.GenericArguments.Add(delHook);
            il.Emit(OpCodes.Call, endpointMethod);
            il.Emit(OpCodes.Ret);
            hookType.Methods.Add(remove);

            return true;
        }

        public TypeDefinition GenerateDelegateFor(TypeDefinition hookType, MethodDefinition method) {
            int index = method.DeclaringType.Methods.Where(other => !other.HasGenericParameters && other.Name == method.Name).ToList().IndexOf(method);
            string suffix = "";
            if (index != 0) {
                suffix = index.ToString();
                do {
                    suffix = "_" + suffix;
                } while (method.DeclaringType.Methods.Any(other => !other.HasGenericParameters && other.Name == (method.Name + suffix)));
            }
            string name = method.Name;
            if (name.StartsWith("."))
                name = name.Substring(1);
            name = "d_" + name+ suffix;

            TypeDefinition del = new TypeDefinition(
                null, null,
                TypeAttributes.NestedPublic | TypeAttributes.Sealed | TypeAttributes.Class,
                t_MulticastDelegate
            );

            MethodDefinition ctor = new MethodDefinition(
                ".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.ReuseSlot,
                OutputModule.TypeSystem.Void
            ) {
                ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed,
                HasThis = true
            };
            ctor.Parameters.Add(new ParameterDefinition(OutputModule.TypeSystem.Object));
            ctor.Parameters.Add(new ParameterDefinition(OutputModule.TypeSystem.IntPtr));
            ctor.Body = new MethodBody(ctor);
            del.Methods.Add(ctor);

            MethodDefinition invoke = new MethodDefinition(
                "Invoke",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot ,
                OutputModule.ImportReference(method.ReturnType)
            ) {
                ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed,
                HasThis = true
            };
            if (!method.IsStatic)
                invoke.Parameters.Add(new ParameterDefinition("self", ParameterAttributes.None, OutputModule.ImportReference(method.DeclaringType)));
            foreach (ParameterDefinition param in method.Parameters)
                invoke.Parameters.Add(new ParameterDefinition(
                    param.Name,
                    param.Attributes & ~ParameterAttributes.Optional & ~ParameterAttributes.HasDefault,
                    OutputModule.ImportReference(param.ParameterType)
                ));
            foreach (ParameterDefinition param in method.Parameters) {
                // Check if the declaring type is accessible.
                // If not, use its base type instead.
                // Note: This will break down with type specifications!
                TypeDefinition paramType = param.ParameterType?.SafeResolve();
                TypeReference paramTypeRef = null;
                Retry:
                if (paramType == null)
                    continue;

                for (TypeDefinition parent = paramType; parent != null; parent = parent.DeclaringType) {
                    if (parent.IsNestedPublic || parent.IsPublic)
                        continue;

                    if (paramType.IsEnum) {
                        paramTypeRef = paramType.FindField("value__").FieldType;
                        break;
                    }

                    paramTypeRef = paramType.BaseType;
                    paramType = paramType.BaseType?.SafeResolve();
                    goto Retry;
                }

                // If paramTypeRef is null because the type is accessible, don't change it.
                if (paramTypeRef != null)
                    param.ParameterType = OutputModule.ImportReference(paramTypeRef);
            }
            invoke.Body = new MethodBody(invoke);
            del.Methods.Add(invoke);

            MethodDefinition invokeBegin = new MethodDefinition(
                "BeginInvoke",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                t_IAsyncResult
            ) {
                ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed,
                HasThis = true
            };
            foreach (ParameterDefinition param in invoke.Parameters)
                invokeBegin.Parameters.Add(new ParameterDefinition(param.Name, param.Attributes, param.ParameterType));
            invokeBegin.Parameters.Add(new ParameterDefinition("callback", ParameterAttributes.None, t_AsyncCallback));
            invokeBegin.Parameters.Add(new ParameterDefinition(null, ParameterAttributes.None, OutputModule.TypeSystem.Object));
            invokeBegin.Body = new MethodBody(invokeBegin);
            del.Methods.Add(invokeBegin);

            MethodDefinition invokeEnd = new MethodDefinition(
                "EndInvoke",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                OutputModule.TypeSystem.Object
            ) {
                ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed,
                HasThis = true
            };
            invokeEnd.Parameters.Add(new ParameterDefinition("result", ParameterAttributes.None, t_IAsyncResult));
            invokeEnd.Body = new MethodBody(invokeEnd);
            del.Methods.Add(invokeEnd);

            return del;
        }

        CustomAttribute GenerateObsolete(string message, bool error) {
            CustomAttribute attrib = new CustomAttribute(m_ObsoleteAttribute_ctor);
            attrib.ConstructorArguments.Add(new CustomAttributeArgument(OutputModule.TypeSystem.String, message));
            attrib.ConstructorArguments.Add(new CustomAttributeArgument(OutputModule.TypeSystem.Boolean, error));
            return attrib;
        }

        CustomAttribute GenerateEditorBrowsable(EditorBrowsableState state) {
            CustomAttribute attrib = new CustomAttribute(m_EditorBrowsableAttribute_ctor);
            attrib.ConstructorArguments.Add(new CustomAttributeArgument(t_EditorBrowsableState, state));
            return attrib;
        }

        IMetadataTokenProvider Relinker(IMetadataTokenProvider mtp, IGenericParameterProvider context) {
            return OutputModule.ImportReference(mtp);
        }

        IMetadataTokenProvider WrappedRelinker(IMetadataTokenProvider mtp, IGenericParameterProvider context) {
            if (mtp is TypeReference type && type.GetElementType().FullName == t_HookEndpoint.FullName)
                return td_HookWrapper;

            return OutputModule.ImportReference(mtp);
        }

    }
}