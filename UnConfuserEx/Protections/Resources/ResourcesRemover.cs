﻿using dnlib.DotNet;
using dnlib.DotNet.Emit;
using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using UnConfuserEx.Protections.Resources;

namespace UnConfuserEx.Protections
{
    internal class ResourcesRemover : IProtection
    {
        private static ILog Logger = LogManager.GetLogger("Resources");

        private enum DecryptionType
        {
            Normal,
            Dynamic
        };

        public string Name => "Resources";

        int callIndex;
        MethodDef? initializeMethod;
        byte[]? data;

        FieldDef? dataField;
        FieldDef? assemblyField;
        MethodDef? handlerMethod;

        public bool IsPresent(ref ModuleDefMD module)
        {
            var cctor = module.GlobalType.FindStaticConstructor();

            if (cctor == null || !(cctor.HasBody) || cctor.Body.Instructions.Count == 0)
                return false;

            IList<Instruction> instrs;

            // Check the first calls in the constructor
            callIndex = 0;
            while (cctor.Body.Instructions[callIndex].OpCode == OpCodes.Call)
            {
                var method = cctor.Body.Instructions[callIndex].Operand as MethodDef;
                if (!method!.HasBody)
                {
                    callIndex++;
                    continue;
                }

                instrs = method.Body.Instructions;
                for (int i = 0; i < instrs.Count - 3; i++)
                {
                    if (instrs[i].OpCode == OpCodes.Stsfld
                        && instrs[i + 1].OpCode == OpCodes.Call
                        && instrs[i + 1].Operand.ToString()!.Contains("AppDomain::get_CurrentDomain")
                        && instrs[i + 2].OpCode == OpCodes.Ldnull
                        && instrs[i + 3].OpCode == OpCodes.Ldftn)
                    {

                        assemblyField = instrs[i].Operand as FieldDef;
                        initializeMethod = method;
                        handlerMethod = instrs[i + 3].Operand as MethodDef;

                        return true;
                    }
                }
                callIndex++;
            }
            callIndex = -1;

            // If we didn't find it there, check the cctor itself
            instrs = cctor.Body.Instructions;
            for (int i = 0; i < instrs.Count - 3; i++)
            {
                if (instrs[i].OpCode == OpCodes.Stsfld
                    && instrs[i + 1].OpCode == OpCodes.Call
                    && instrs[i + 1].Operand.ToString()!.Contains("AppDomain::get_CurrentDomain")
                    && instrs[i + 2].OpCode == OpCodes.Ldnull
                    && instrs[i + 3].OpCode == OpCodes.Ldftn)
                {
                    assemblyField = instrs[i].Operand as FieldDef;
                    initializeMethod = cctor;
                    handlerMethod = instrs[i + 3].Operand as MethodDef;

                    return true;
                }
            }
            return false;
        }

        public bool Remove(ref ModuleDefMD module) 
        {
            if (!GetEncryptedData())
            {
                Logger.Error("Failed to get encrypted resource data");
                return false;
            }

            if (!DecryptData())
            {
                Logger.Error("Failed to decrypt resource data");
                return false;
            }

            data = Utils.DecompressLZMA(data!, initializeMethod!);

            var loadedModule = ModuleDefMD.Load(data);
            foreach (var resource in loadedModule.Resources)
            {
                int index = module.Resources.ToList().FindIndex(r => r.Name.Equals(resource.Name));
                if ( index != -1)
                { 
                    module.Resources.RemoveAt(index);
                }
                module.Resources.Add(resource);
            }

            module.GlobalType.Methods.Remove(handlerMethod);
            module.GlobalType.Fields.Remove(assemblyField);
            module.GlobalType.Fields.Remove(dataField);

            var cctor = module.GlobalType.FindStaticConstructor();

            // Remove the decryption call/instructions
            if (callIndex == -1)
            {
                // TODO: Is this ever actually in the static constructor?
                throw new NotImplementedException("Resources init was in the static constructor");
            }
            else
            {
                cctor.Body.Instructions.RemoveAt(callIndex);
                module.GlobalType.Methods.Remove(initializeMethod);
            }

            return true;
        }

        private bool GetEncryptedData()
        {
            var instrs = initializeMethod!.Body.Instructions;
            for (int i = 0; i < instrs.Count; i++)
            {
                if (instrs[i].OpCode == OpCodes.Ldtoken
                    && instrs[i + 1].OpCode == OpCodes.Call)
                {
                    dataField = instrs[i].Operand as FieldDef;
                    data = dataField!.InitialValue;
                    return true;
                }
            }
            return false;
        }
        private uint[]? GetInitialArray()
        {
            uint? key = null;
            bool? shlFirst = null;

            var instrs = initializeMethod!.Body.Instructions;
            for (int i = 0; i < instrs.Count - 2; i++)
            {
                if (instrs[i].OpCode == OpCodes.Newarr
                    && instrs[i + 2].OpCode == OpCodes.Ldc_I4)
                {
                    key = (uint)(int)instrs[i + 2].Operand;
                }
                else if (instrs[i].OpCode == OpCodes.Ldc_I4_S
                        && instrs[i].Operand is sbyte val
                        && val == 13)
                {
                    shlFirst = instrs[i + 1].OpCode == OpCodes.Shl;
                }

                if (key != null && shlFirst != null)
                    break;
            }

            if (key == null || shlFirst == null)
                return null;


            uint[] ret = new uint[16];
            for (int j = 0; j < 16; j++)
            {
                key ^= (bool)shlFirst ? key << 13 : key >> 13;
                key ^= (bool)shlFirst ? key >> 25 : key << 25;
                key ^= (bool)shlFirst ? key << 27 : key >> 27;
                ret[j] = (uint)key;
            }
            return ret;
        }

        private DecryptionType GetDecryptionType()
        {
            // TODO: Support dynamic decryption
            return DecryptionType.Normal;
        }

        private bool DecryptData()
        {
            DecryptionType type = GetDecryptionType();

            uint[] uintData = new uint[data!.Length >> 2];
            Buffer.BlockCopy(data, 0, uintData, 0, data.Length);

            uint[]? key = GetInitialArray();
            if (key == null)
            {
                Logger.Error("Failed to get initial array");
                return false;
            }

            IDecryptor decryptor;
            if (type == DecryptionType.Normal)
            {
                decryptor = new NormalDecryptor();
            }
            else
            {
                throw new NotImplementedException();
            }

            data = decryptor.Decrypt(key, uintData);

            return true;
        }
    }
}
