﻿using System;
using System.Collections.Generic;
using System.Linq;
using Fody;
using InlineIL.Fody.Extensions;
using InlineIL.Fody.Support;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace InlineIL.Fody.Processing
{
    internal class MethodWeaver
    {
        private readonly ModuleWeavingContext _context;
        private readonly MethodDefinition _method;
        private readonly WeaverILProcessor _il;
        private readonly SequencePointMapper _sequencePoints;
        private readonly LabelMapper _labels;
        private readonly ArgumentConsumer _consumer;

        private ModuleDefinition Module => _context.Module;
        private IEnumerable<Instruction> Instructions => _method.Body.Instructions;

        public MethodWeaver(ModuleWeavingContext context, MethodDefinition method)
        {
            _context = context;
            _method = method;
            _il = new WeaverILProcessor(_method);
            _labels = new LabelMapper(_il);
            _sequencePoints = new SequencePointMapper(_method, context.Config);
            _consumer = new ArgumentConsumer(_il);
        }

        public static bool NeedsProcessing(ModuleWeavingContext context, MethodDefinition method)
            => HasLibReference(context, method, out _);

        private static bool HasLibReference(ModuleWeavingContext context, MethodDefinition method, out Instruction? refInstruction)
        {
            refInstruction = null;

            if (method.IsInlineILTypeUsage(context))
                return true;

            if (!method.HasBody)
                return false;

            if (method.Body.Variables.Any(i => i.VariableType.IsInlineILTypeUsage(context)))
                return true;

            foreach (var instruction in method.Body.Instructions)
            {
                refInstruction = instruction;

                switch (instruction.Operand)
                {
                    case MethodReference methodRef when methodRef.IsInlineILTypeUsage(context):
                    case TypeReference typeRef when typeRef.IsInlineILTypeUsage(context):
                    case FieldReference fieldRef when fieldRef.IsInlineILTypeUsage(context):
                    case CallSite callSite when callSite.IsInlineILTypeUsage(context):
                        return true;
                }
            }

            refInstruction = null;
            return false;
        }

        public void Process()
        {
            try
            {
                ProcessImpl();
            }
            catch (InstructionWeavingException ex)
            {
                var message = ex.Message.Contains(_method.FullName)
                    ? ex.Message
                    : ex.Instruction != null
                        ? $"{ex.Message} (in {_method.FullName} at instruction {ex.Instruction})"
                        : $"{ex.Message} (in {_method.FullName})";

                throw new WeavingException(message)
                {
                    SequencePoint = _sequencePoints.GetInputSequencePoint(ex.Instruction)
                };
            }
            catch (WeavingException ex)
            {
                throw new WeavingException($"{ex.Message} (in {_method.FullName})")
                {
                    SequencePoint = ex.SequencePoint
                };
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Unexpected error occured while processing method {_method.FullName}: {ex.Message}", ex);
            }
        }

        private void ProcessImpl()
        {
            _method.Body.SimplifyMacros();

            ValidateBeforeProcessing();
            ProcessMethodCalls();
            _labels.PostProcess();
            PostProcessTailCalls();
            _sequencePoints.PostProcess();
            ValidateAfterProcessing();

            _method.Body.SimplifyMacros();
            _method.Body.OptimizeMacros();
        }

        private void ValidateBeforeProcessing()
        {
            foreach (var instruction in Instructions)
            {
                if (instruction.OpCode == OpCodes.Call
                    && instruction.Operand is MethodReference calledMethod
                    && calledMethod.DeclaringType.FullName == KnownNames.Full.IlType)
                {
                    try
                    {
                        switch (calledMethod.Name)
                        {
                            case KnownNames.Short.PushMethod:
                                ValidatePushMethod(instruction);
                                break;
                        }
                    }
                    catch (InstructionWeavingException)
                    {
                        throw;
                    }
                    catch (WeavingException ex)
                    {
                        throw new InstructionWeavingException(instruction, $"{ex.Message} (in {_method.FullName} at instruction {instruction})");
                    }
                    catch (Exception ex)
                    {
                        throw new InstructionWeavingException(instruction, $"Unexpected error occured while processing method {_method.FullName} at instruction {instruction}: {ex}");
                    }
                }
            }
        }

        private void ProcessMethodCalls()
        {
            Instruction? instruction = Instructions.FirstOrDefault();

            while (instruction != null)
            {
                Instruction? nextInstruction = instruction.Next;

                if (instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference calledMethod)
                {
                    try
                    {
                        switch (calledMethod.DeclaringType.FullName)
                        {
                            case KnownNames.Full.IlType:
                                ProcessIlMethodCall(instruction, out nextInstruction);
                                break;

                            case KnownNames.Full.IlEmitType:
                                ProcessIlEmitMethodCall(instruction, out nextInstruction);
                                break;

                            case KnownNames.Full.TypeRefType:
                                ProcessTypeRefCall(instruction, out nextInstruction);
                                break;
                        }
                    }
                    catch (InstructionWeavingException)
                    {
                        throw;
                    }
                    catch (WeavingException ex)
                    {
                        throw new InstructionWeavingException(instruction, $"{ex.Message} (in {_method.FullName} at instruction {instruction})");
                    }
                    catch (Exception ex)
                    {
                        throw new InstructionWeavingException(instruction, $"Unexpected error occured while processing method {_method.FullName} at instruction {instruction}: {ex}");
                    }
                }

                instruction = nextInstruction;
            }
        }

        private void PostProcessTailCalls()
        {
            if (Instructions.All(i => i.OpCode != OpCodes.Tail))
                return;

            SplitSinglePointOfReturn();

            for (var instruction = Instructions.FirstOrDefault(); instruction != null; instruction = instruction.Next)
            {
                if (instruction.OpCode != OpCodes.Tail)
                    continue;

                var tailInstruction = instruction;
                var callInstruction = tailInstruction.Next;

                var validTailOpCodes = new[] { OpCodes.Call, OpCodes.Calli, OpCodes.Callvirt };

                if (callInstruction == null || !validTailOpCodes.Contains(callInstruction.OpCode))
                    throw new InstructionWeavingException(tailInstruction, $"{OpCodes.Tail.Name} must be followed by {string.Join(" or ", validTailOpCodes.Select(i => i.Name))}");

                _il.RemoveNopsAfter(callInstruction);
                instruction = callInstruction.Next;

                if (instruction.OpCode != OpCodes.Ret)
                    throw new InstructionWeavingException(callInstruction, "A tail call must be immediately followed by ret");
            }
        }

        private void SplitSinglePointOfReturn()
        {
            if (!Module.IsDebugBuild())
                return;

            var lastRetInstruction = Instructions.LastOrDefault();
            if (lastRetInstruction?.OpCode != OpCodes.Ret)
                return;

            var ldlocInstruction = lastRetInstruction.Previous;
            if (ldlocInstruction?.OpCode != OpCodes.Ldloc)
                return;

            var refsToLdloc = Instructions.Where(i => i.Operand == ldlocInstruction).ToList();

            var leaveOriginalReturnPoint = false;

            foreach (var brInstruction in refsToLdloc)
            {
                var stlocInstruction = brInstruction.Previous;

                if (brInstruction.OpCode == OpCodes.Br
                    && stlocInstruction != null
                    && stlocInstruction.OpCode == OpCodes.Stloc
                    && stlocInstruction.Operand == ldlocInstruction.Operand)
                {
                    _il.Remove(stlocInstruction);
                    _il.Replace(brInstruction, Instruction.Create(OpCodes.Ret));
                }
                else
                {
                    leaveOriginalReturnPoint = true;
                }
            }

            if (!leaveOriginalReturnPoint)
            {
                // If the compiler ever stops emitting no-op branch instructions
                var stlocInstruction = ldlocInstruction.PrevSkipNops();
                if (stlocInstruction != null && stlocInstruction.OpCode == OpCodes.Stloc && stlocInstruction.Operand == ldlocInstruction.Operand)
                    _il.Remove(stlocInstruction);

                _il.RemoveNopsAround(ldlocInstruction);
                _il.Remove(ldlocInstruction);

                if (lastRetInstruction.Previous?.OpCode == OpCodes.Ret)
                    _il.Remove(lastRetInstruction);
            }
        }

        private void ValidateAfterProcessing()
        {
            if (HasLibReference(_context, _method, out var libReferencingInstruction))
                throw new InstructionWeavingException(libReferencingInstruction, "Unconsumed reference to InlineIL");

            var invalidRefs = _il.GetAllReferencedInstructions().Except(Instructions).ToList();
            if (invalidRefs.Any())
                throw new WeavingException($"Found invalid references to instructions:{Environment.NewLine}{string.Join(Environment.NewLine, invalidRefs)}");
        }

        private void ProcessIlMethodCall(Instruction instruction, out Instruction nextInstruction)
        {
            var calledMethod = (MethodReference)instruction.Operand;
            nextInstruction = instruction.Next;

            switch (calledMethod.Name)
            {
                case KnownNames.Short.PushMethod:
                    ProcessPushMethod(instruction);
                    break;

                case KnownNames.Short.PopMethod:
                    ProcessPopMethod(instruction);
                    break;

                case KnownNames.Short.UnreachableMethod:
                    ProcessUnreachableMethod(instruction, out nextInstruction);
                    break;

                case KnownNames.Short.ReturnMethod:
                case KnownNames.Short.ReturnRefMethod:
                case KnownNames.Short.ReturnPointerMethod:
                    ProcessReturnMethod(instruction);
                    break;

                case KnownNames.Short.MarkLabelMethod:
                    ProcessMarkLabelMethod(instruction);
                    break;

                case KnownNames.Short.DeclareLocalsMethod:
                    ProcessDeclareLocalsMethod(instruction);
                    break;

                default:
                    throw new InstructionWeavingException(instruction, $"Unsupported method: {calledMethod.FullName}");
            }
        }

        private void ProcessIlEmitMethodCall(Instruction emitCallInstruction, out Instruction? nextInstruction)
        {
            var emittedInstruction = CreateInstructionToEmit();
            _il.Replace(emitCallInstruction, emittedInstruction);

            if (emittedInstruction.OpCode.OpCodeType == OpCodeType.Prefix)
                _il.RemoveNopsAfter(emittedInstruction);

            var sequencePoint = _sequencePoints.MapSequencePoint(emitCallInstruction, emittedInstruction);

            if (emittedInstruction.Previous?.OpCode.OpCodeType == OpCodeType.Prefix)
                _sequencePoints.MergeWithPreviousSequencePoint(sequencePoint);

            nextInstruction = emittedInstruction.NextSkipNops();

            switch (emittedInstruction.OpCode.Code)
            {
                case Code.Ret:
                case Code.Endfinally:
                case Code.Endfilter:
                {
                    if (nextInstruction?.OpCode == emittedInstruction.OpCode)
                        _il.Remove(emittedInstruction);

                    break;
                }

                case Code.Leave:
                case Code.Leave_S:
                case Code.Throw:
                case Code.Rethrow:
                {
                    if (nextInstruction?.OpCode == OpCodes.Leave || nextInstruction?.OpCode == OpCodes.Leave_S)
                    {
                        _il.RemoveNopsAfter(emittedInstruction);
                        _il.Remove(emittedInstruction);
                        _il.Replace(nextInstruction, emittedInstruction);
                        nextInstruction = emittedInstruction.NextSkipNops();
                    }

                    break;
                }
            }

            Instruction CreateInstructionToEmit()
            {
                var method = (MethodReference)emitCallInstruction.Operand;
                var opCode = OpCodeMap.FromCecilFieldName(method.Name);
                var args = _il.GetArgumentPushInstructionsInSameBasicBlock(emitCallInstruction);

                switch (opCode.OperandType)
                {
                    case OperandType.InlineNone:
                        if (args.Length != 0)
                            throw new InstructionWeavingException(emitCallInstruction, "Unexpected operand argument");

                        return _il.Create(opCode);

                    case OperandType.InlineI:
                    case OperandType.ShortInlineI:
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                    case OperandType.ShortInlineR:
                        return _il.CreateConst(opCode, _consumer.ConsumeArgConst(args.Single()));

                    case OperandType.InlineString:
                        return _il.CreateConst(opCode, _consumer.ConsumeArgString(args.Single()));

                    case OperandType.InlineType:
                        return _il.Create(opCode, _consumer.ConsumeArgTypeRef(args.Single()));

                    case OperandType.InlineMethod:
                        return _il.Create(opCode, _consumer.ConsumeArgMethodRef(args.Single()));

                    case OperandType.InlineField:
                        return _il.Create(opCode, _consumer.ConsumeArgFieldRef(args.Single()));

                    case OperandType.InlineTok:
                    {
                        switch (method.Parameters[0].ParameterType.FullName)
                        {
                            case KnownNames.Full.TypeRefType:
                                return _il.Create(opCode, _consumer.ConsumeArgTypeRef(args.Single()));

                            case KnownNames.Full.MethodRefType:
                                return _il.Create(opCode, _consumer.ConsumeArgMethodRef(args.Single()));

                            case KnownNames.Full.FieldRefType:
                                return _il.Create(opCode, _consumer.ConsumeArgFieldRef(args.Single()));

                            default:
                                throw new InstructionWeavingException(emitCallInstruction, $"Unexpected argument type: {method.Parameters[0].ParameterType.FullName}");
                        }
                    }

                    case OperandType.InlineBrTarget:
                    case OperandType.ShortInlineBrTarget:
                    {
                        var labelName = _consumer.ConsumeArgString(args.Single());
                        return _labels.CreateBranchInstruction(opCode, labelName);
                    }

                    case OperandType.InlineSwitch:
                    {
                        var labelNames = _consumer.ConsumeArgArray(args.Single(), _consumer.ConsumeArgString).ToList();
                        return _labels.CreateSwitchInstruction(labelNames);
                    }

                    case OperandType.InlineVar:
                    case OperandType.ShortInlineVar:
                    {
                        switch (method.Parameters[0].ParameterType.FullName)
                        {
                            case "System.String":
                                return _il.Create(opCode, _consumer.ConsumeArgLocalRef(args.Single()));

                            case "System.Byte":
                            case "System.UInt16":
                                return _il.CreateConst(opCode, _consumer.ConsumeArgConst(args.Single()));

                            default:
                                throw new InstructionWeavingException(emitCallInstruction, $"Unexpected argument type: {method.Parameters[0].ParameterType.FullName}");
                        }
                    }

                    case OperandType.InlineArg:
                    case OperandType.ShortInlineArg:
                    {
                        switch (method.Parameters[0].ParameterType.FullName)
                        {
                            case "System.String":
                                return _il.CreateConst(opCode, _consumer.ConsumeArgParamName(args.Single()));

                            case "System.Byte":
                            case "System.UInt16":
                                return _il.CreateConst(opCode, _consumer.ConsumeArgConst(args.Single()));

                            default:
                                throw new InstructionWeavingException(emitCallInstruction, $"Unexpected argument type: {method.Parameters[0].ParameterType.FullName}");
                        }
                    }

                    case OperandType.InlineSig:
                        return _il.Create(opCode, _consumer.ConsumeArgCallSite(args.Single()));

                    default:
                        throw new NotSupportedException($"Unsupported operand type: {opCode.OperandType}");
                }
            }
        }

        private void ProcessTypeRefCall(Instruction instruction, out Instruction nextInstruction)
        {
            var calledMethod = (MethodReference)instruction.Operand;
            nextInstruction = instruction.Next;

            switch (calledMethod.Name)
            {
                case "get_CoreLibrary":
                {
                    var coreLibrary = Module.GetCoreLibrary();
                    if (coreLibrary == null)
                        throw new InstructionWeavingException(instruction, "Could not resolve core library");

                    var newInstruction = Instruction.Create(OpCodes.Ldstr, coreLibrary.Name);
                    _il.Replace(instruction, newInstruction, true);
                    _sequencePoints.MapSequencePoint(instruction, newInstruction);
                    break;
                }
            }
        }

        private void ValidatePushMethod(Instruction instruction)
        {
            if (_method.Body.ExceptionHandlers.Any(h => h.HandlerType == ExceptionHandlerType.Catch && h.HandlerStart == instruction
                                                        || h.HandlerType == ExceptionHandlerType.Filter && (h.FilterStart == instruction || h.HandlerStart == instruction)))
                return;

            var args = instruction.GetArgumentPushInstructions();
            var prevInstruction = instruction.PrevSkipNops();

            if (args[0] != prevInstruction)
                throw new InstructionWeavingException(instruction, "IL.Push cannot be used in this context, as the instruction which supplies its argument does not immediately precede the call");
        }

        private void ProcessPushMethod(Instruction instruction)
        {
            _il.Remove(instruction);
        }

        private void ProcessPopMethod(Instruction instruction)
        {
            var target = _il.GetArgumentPushInstructionsInSameBasicBlock(instruction).Single();

            switch (target.OpCode.Code)
            {
                case Code.Ldloca when target.Operand is VariableDefinition operandVar:
                {
                    _il.Remove(target);
                    _il.Replace(instruction, Instruction.Create(OpCodes.Stloc, operandVar));
                    break;
                }

                case Code.Ldarga when target.Operand is ParameterDefinition operandParam:
                {
                    _il.Remove(target);
                    _il.Replace(instruction, Instruction.Create(OpCodes.Starg, operandParam));
                    break;
                }

                case Code.Ldsflda when target.Operand is FieldDefinition operandField:
                {
                    _il.Remove(target);
                    _il.Replace(instruction, Instruction.Create(OpCodes.Stsfld, operandField));
                    break;
                }

                case Code.Ldflda:
                    throw new InstructionWeavingException(instruction, "IL.Pop does not support instance field references. Emit a stfld instruction instead.");

                case Code.Ldelema:
                    throw new InstructionWeavingException(instruction, "IL.Pop does not support array references. Emit a stelem instruction instead.");

                case Code.Ldarg:
                    throw new InstructionWeavingException(instruction, "IL.Pop does not support ref method arguments. Emit ldarg/stind instructions instead.");

                case Code.Ldloc:
                    throw new InstructionWeavingException(instruction, "IL.Pop does not support ref locals.");

                default:
                    throw new InstructionWeavingException(instruction, $"IL.Pop does not support this kind of argument: {target.OpCode}");
            }
        }

        private void ProcessUnreachableMethod(Instruction instruction, out Instruction nextInstruction)
        {
            var throwInstruction = instruction.NextSkipNops();
            if (throwInstruction?.OpCode != OpCodes.Throw)
                throw new InstructionWeavingException(instruction, "The result of the IL.Unreachable method should be immediately thrown: throw IL.Unreachable();");

            _il.Remove(instruction);
            _il.RemoveNopsAround(throwInstruction);
            nextInstruction = throwInstruction.Next;
            _il.Remove(throwInstruction);
        }

        private void ProcessReturnMethod(Instruction instruction)
        {
            ValidateReturnMethod();

            _il.Remove(instruction);
            _sequencePoints.MapSequencePoint(instruction, instruction.Next);

            void ValidateReturnMethod()
            {
                var currentInstruction = instruction.NextSkipNops();

                while (true)
                {
                    switch (currentInstruction?.OpCode.Code)
                    {
                        case Code.Ret:
                            return;

                        case Code.Stloc:
                        {
                            var localIndex = ((VariableReference)currentInstruction.Operand).Index;
                            var branchInstruction = currentInstruction.NextSkipNops();

                            switch (branchInstruction?.OpCode.Code)
                            {
                                case Code.Br: // Debug builds
                                case Code.Leave: // try/catch blocks
                                {
                                    if (branchInstruction.Operand is Instruction branchTarget)
                                    {
                                        branchTarget = branchTarget.SkipNops() ?? throw new InstructionWeavingException(branchTarget, "Unexpected end of method");

                                        if (branchTarget.OpCode == OpCodes.Ldloc && ((VariableReference)branchTarget.Operand).Index == localIndex)
                                            return;
                                    }

                                    break;
                                }
                            }

                            throw InvalidReturnException();
                        }

                        default:
                        {
                            // Allow implicit conversions
                            if (currentInstruction != null
                                && (currentInstruction.OpCode.FlowControl == FlowControl.Next
                                    || currentInstruction.OpCode.FlowControl == FlowControl.Call)
                                && currentInstruction.GetPopCount() == 1
                                && currentInstruction.GetPushCount() == 1
                            )
                            {
                                currentInstruction = currentInstruction.NextSkipNops();
                                continue;
                            }

                            throw InvalidReturnException();
                        }
                    }
                }

                Exception InvalidReturnException()
                {
                    var calledMethod = (MethodReference)instruction.Operand;

                    switch (calledMethod.Name)
                    {
                        case KnownNames.Short.ReturnMethod:
                            return new InstructionWeavingException(instruction, $"The result of the IL.{calledMethod.Name} method should be immediately returned: return IL.{calledMethod.Name}<T>();");

                        case KnownNames.Short.ReturnRefMethod:
                            return new InstructionWeavingException(instruction, $"The result of the IL.{calledMethod.Name} method should be immediately returned: return ref IL.{calledMethod.Name}<T>();");

                        case KnownNames.Short.ReturnPointerMethod:
                            if (!calledMethod.HasGenericParameters)
                                return new InstructionWeavingException(instruction, $"The result of the IL.{calledMethod.Name} method should be immediately returned: return IL.{calledMethod.Name}();");

                            goto case KnownNames.Short.ReturnMethod;

                        default:
                            return new InstructionWeavingException(instruction, $"Unexpected method call: IL.{calledMethod.Name}");
                    }
                }
            }
        }

        private void ProcessMarkLabelMethod(Instruction instruction)
        {
            var labelName = _consumer.ConsumeArgString(_il.GetArgumentPushInstructionsInSameBasicBlock(instruction).Single());
            _il.Replace(instruction, _labels.MarkLabel(labelName));
        }

        private void ProcessDeclareLocalsMethod(Instruction instruction)
        {
            var method = (MethodReference)instruction.Operand;

            switch (method.FullName)
            {
                case "System.Void InlineIL.IL::DeclareLocals(InlineIL.LocalVar[])":
                {
                    var args = _il.GetArgumentPushInstructionsInSameBasicBlock(instruction);
                    _method.Body.InitLocals = true;
                    _il.DeclareLocals(_consumer.ConsumeArgArray(args[0], _consumer.ConsumeArgLocalVarBuilder));
                    _il.Remove(instruction);
                    return;
                }

                case "System.Void InlineIL.IL::DeclareLocals(System.Boolean,InlineIL.LocalVar[])":
                {
                    var args = _il.GetArgumentPushInstructionsInSameBasicBlock(instruction);
                    _method.Body.InitLocals = _consumer.ConsumeArgBool(args[0]);
                    _il.DeclareLocals(_consumer.ConsumeArgArray(args[1], _consumer.ConsumeArgLocalVarBuilder));
                    _il.Remove(instruction);
                    return;
                }

                default:
                    throw new InstructionWeavingException(instruction, $"Unexpected instruction, expected a InlineIL.DeclareLocals method call but was: {instruction}");
            }
        }
    }
}
