﻿using System;
using System.Linq;
using Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace InlineIL.Fody
{
    internal class MethodWeaver
    {
        public void ProcessMethod(MethodDefinition method)
        {
            if (NeedsProcessing(method))
            {
                try
                {
                    new MethodContext(method).Process();
                }
                catch (WeavingException ex)
                {
                    throw new WeavingException($"Error processing method {method.FullName} in type {method.DeclaringType.FullName}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Unexpected error occured while processing method {method.FullName} in type {method.DeclaringType.FullName}: {ex.Message}", ex);
                }
            }
        }

        private static bool NeedsProcessing(MethodDefinition method)
        {
            return method.Body
                         .Instructions
                         .Where(i => i.OpCode == OpCodes.Call)
                         .Select(i => i.Operand)
                         .OfType<MethodReference>()
                         .Any(m => m.DeclaringType.Name == KnownNames.Short.IlType
                                   && m.DeclaringType.FullName == KnownNames.Full.IlType);
        }
    }
}
