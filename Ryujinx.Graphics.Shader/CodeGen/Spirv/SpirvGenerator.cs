﻿using Ryujinx.Common;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.StructuredIr;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using static Spv.Specification;

namespace Ryujinx.Graphics.Shader.CodeGen.Spirv
{
    using SpvInstruction = Spv.Generator.Instruction;
    using SpvLiteralInteger = Spv.Generator.LiteralInteger;

    using SpvInstructionPool = Spv.Generator.GeneratorPool<Spv.Generator.Instruction>;
    using SpvLiteralIntegerPool = Spv.Generator.GeneratorPool<Spv.Generator.LiteralInteger>;

    static class SpirvGenerator
    {
        // Resource pools for Spirv generation. Note: Increase count when more threads are being used.
        private const int GeneratorPoolCount = 1;
        private static ObjectPool<SpvInstructionPool> InstructionPool;
        private static ObjectPool<SpvLiteralIntegerPool> IntegerPool;
        private static object PoolLock;

        static SpirvGenerator()
        {
            InstructionPool = new (() => new SpvInstructionPool(), GeneratorPoolCount);
            IntegerPool = new (() => new SpvLiteralIntegerPool(), GeneratorPoolCount);
            PoolLock = new object();
        }

        private const HelperFunctionsMask NeedsInvocationIdMask =
            HelperFunctionsMask.Shuffle |
            HelperFunctionsMask.ShuffleDown |
            HelperFunctionsMask.ShuffleUp |
            HelperFunctionsMask.ShuffleXor |
            HelperFunctionsMask.SwizzleAdd;

        public static byte[] Generate(StructuredProgramInfo info, ShaderConfig config)
        {
            SpvInstructionPool instPool;
            SpvLiteralIntegerPool integerPool;

            lock (PoolLock)
            {
                instPool = InstructionPool.Allocate();
                integerPool = IntegerPool.Allocate();
            }

            CodeGenContext context = new CodeGenContext(config, instPool, integerPool);

            context.AddCapability(Capability.GroupNonUniformBallot);
            context.AddCapability(Capability.ImageBuffer);
            context.AddCapability(Capability.ImageQuery);
            context.AddCapability(Capability.SampledBuffer);
            context.AddCapability(Capability.SubgroupBallotKHR);
            context.AddCapability(Capability.SubgroupVoteKHR);

            if (config.Stage == ShaderStage.Geometry)
            {
                context.AddCapability(Capability.Geometry);
            }

            context.AddExtension("SPV_KHR_shader_ballot");
            context.AddExtension("SPV_KHR_subgroup_vote");

            Declarations.DeclareAll(context, info);

            if ((info.HelperFunctionsMask & NeedsInvocationIdMask) != 0)
            {
                Declarations.DeclareInvocationId(context);
            }

            for (int funcIndex = 0; funcIndex < info.Functions.Count; funcIndex++)
            {
                var function = info.Functions[funcIndex];
                var retType = context.GetType(function.ReturnType.Convert());

                var funcArgs = new SpvInstruction[function.InArguments.Length + function.OutArguments.Length];

                for (int argIndex = 0; argIndex < funcArgs.Length; argIndex++)
                {
                    var argType = context.GetType(function.GetArgumentType(argIndex).Convert());
                    var argPointerType = context.TypePointer(StorageClass.Function, argType);
                    funcArgs[argIndex] = argPointerType;
                }

                var funcType = context.TypeFunction(retType, false, funcArgs);
                var spvFunc = context.Function(retType, FunctionControlMask.MaskNone, funcType);

                context.DeclareFunction(funcIndex, function, spvFunc);
            }

            for (int funcIndex = 0; funcIndex < info.Functions.Count; funcIndex++)
            {
                Generate(context, info, funcIndex);
            }

            byte[] result = context.Generate();

            lock (PoolLock)
            {
                InstructionPool.Release(instPool);
                IntegerPool.Release(integerPool);
            }

            return result;
        }

        private static void Generate(CodeGenContext context, StructuredProgramInfo info, int funcIndex)
        {
            var function = info.Functions[funcIndex];

            (_, var spvFunc) = context.GetFunction(funcIndex);

            context.AddFunction(spvFunc);
            context.StartFunction();

            Declarations.DeclareParameters(context, function);

            context.EnterBlock(function.MainBlock);

            Declarations.DeclareLocals(context, function);
            Declarations.DeclareLocalForArgs(context, info.Functions);

            Generate(context, function.MainBlock);

            // Functions must always end with a return.
            if (!(function.MainBlock.Last is AstOperation operation) || operation.Inst != Instruction.Return)
            {
                context.Return();
            }

            context.FunctionEnd();

            if (funcIndex == 0)
            {
                context.AddEntryPoint(context.Config.Stage.Convert(), spvFunc, "main", context.GetMainInterface());

                if (context.Config.Stage == ShaderStage.Geometry)
                {
                    InputTopology inPrimitive = context.Config.GpuAccessor.QueryPrimitiveTopology();

                    switch (inPrimitive)
                    {
                        case InputTopology.Points:
                            context.AddExecutionMode(spvFunc, ExecutionMode.InputPoints);
                            break;
                        case InputTopology.Lines:
                            context.AddExecutionMode(spvFunc, ExecutionMode.InputLines);
                            break;
                        case InputTopology.LinesAdjacency:
                            context.AddExecutionMode(spvFunc, ExecutionMode.InputLinesAdjacency);
                            break;
                        case InputTopology.TrianglesAdjacency:
                            context.AddExecutionMode(spvFunc, ExecutionMode.InputTrianglesAdjacency);
                            break;
                    }

                    context.AddExecutionMode(spvFunc, ExecutionMode.Invocations, (SpvLiteralInteger)context.InputVertices);

                    context.AddExecutionMode(spvFunc, context.Config.OutputTopology switch
                    {
                        OutputTopology.PointList => ExecutionMode.OutputPoints,
                        OutputTopology.LineStrip => ExecutionMode.OutputLineStrip,
                        OutputTopology.TriangleStrip => ExecutionMode.OutputTriangleStrip,
                        _ => throw new InvalidOperationException($"Invalid output topology \"{context.Config.OutputTopology}\".")
                    });

                    context.AddExecutionMode(spvFunc, ExecutionMode.OutputVertices, (SpvLiteralInteger)context.Config.MaxOutputVertices);
                }
                else if (context.Config.Stage == ShaderStage.Fragment)
                {
                    context.AddExecutionMode(spvFunc, context.Config.Options.TargetApi == TargetApi.Vulkan
                        ? ExecutionMode.OriginUpperLeft
                        : ExecutionMode.OriginLowerLeft);

                    if (context.Outputs.ContainsKey(AttributeConsts.FragmentOutputDepth))
                    {
                        context.AddExecutionMode(spvFunc, ExecutionMode.DepthReplacing);
                    }
                }
                else if (context.Config.Stage == ShaderStage.Compute)
                {
                    var localSizeX = (SpvLiteralInteger)context.Config.GpuAccessor.QueryComputeLocalSizeX();
                    var localSizeY = (SpvLiteralInteger)context.Config.GpuAccessor.QueryComputeLocalSizeY();
                    var localSizeZ = (SpvLiteralInteger)context.Config.GpuAccessor.QueryComputeLocalSizeZ();

                    context.AddExecutionMode(
                        spvFunc,
                        ExecutionMode.LocalSize,
                        localSizeX,
                        localSizeY,
                        localSizeZ);
                }
            }
        }

        private static void Generate(CodeGenContext context, AstBlock block)
        {
            AstBlockVisitor visitor = new AstBlockVisitor(block);

            var loopTargets = new Dictionary<AstBlock, (SpvInstruction, SpvInstruction)>();

            context.LoopTargets = loopTargets;

            visitor.BlockEntered += (sender, e) =>
            {
                AstBlock mergeBlock = e.Block.Parent;

                if (e.Block.Type == AstBlockType.If)
                {
                    AstBlock ifTrueBlock = e.Block;
                    AstBlock ifFalseBlock;

                    if (AstHelper.Next(e.Block) is AstBlock nextBlock && nextBlock.Type == AstBlockType.Else)
                    {
                        ifFalseBlock = nextBlock;
                    }
                    else
                    {
                        ifFalseBlock = mergeBlock;
                    }

                    var condition = context.Get(AggregateType.Bool, e.Block.Condition);

                    context.SelectionMerge(context.GetNextLabel(mergeBlock), SelectionControlMask.MaskNone);
                    context.BranchConditional(condition, context.GetNextLabel(ifTrueBlock), context.GetNextLabel(ifFalseBlock));
                }
                else if (e.Block.Type == AstBlockType.DoWhile)
                {
                    var continueTarget = context.Label();

                    loopTargets.Add(e.Block, (context.NewBlock(), continueTarget));

                    context.LoopMerge(context.GetNextLabel(mergeBlock), continueTarget, LoopControlMask.MaskNone);
                    context.Branch(context.GetFirstLabel(e.Block));
                }

                context.EnterBlock(e.Block);
            };

            visitor.BlockLeft += (sender, e) =>
            {
                if (e.Block.Parent != null)
                {
                    if (e.Block.Type == AstBlockType.DoWhile)
                    {
                        // This is a loop, we need to jump back to the loop header
                        // if the condition is true.
                        AstBlock mergeBlock = e.Block.Parent;

                        (var loopTarget, var continueTarget) = loopTargets[e.Block];

                        context.Branch(continueTarget);
                        context.AddLabel(continueTarget);

                        var condition = context.Get(AggregateType.Bool, e.Block.Condition);

                        context.BranchConditional(condition, loopTarget, context.GetNextLabel(mergeBlock));
                    }
                    else
                    {
                        // We only need a branch if the last instruction didn't
                        // already cause the program to exit or jump elsewhere.
                        bool lastIsCf = e.Block.Last is AstOperation lastOp &&
                            (lastOp.Inst == Instruction.Discard ||
                             lastOp.Inst == Instruction.LoopBreak ||
                             lastOp.Inst == Instruction.LoopContinue ||
                             lastOp.Inst == Instruction.Return);

                        if (!lastIsCf)
                        {
                            context.Branch(context.GetNextLabel(e.Block.Parent));
                        }
                    }

                    bool hasElse = AstHelper.Next(e.Block) is AstBlock nextBlock &&
                        (nextBlock.Type == AstBlockType.Else ||
                         nextBlock.Type == AstBlockType.ElseIf);

                    // Re-enter the parent block.
                    if (e.Block.Parent != null && !hasElse)
                    {
                        context.EnterBlock(e.Block.Parent);
                    }
                }
            };

            foreach (IAstNode node in visitor.Visit())
            {
                if (node is AstAssignment assignment)
                {
                    var dest = (AstOperand)assignment.Destination;

                    if (dest.Type == OperandType.LocalVariable)
                    {
                        var source = context.Get(dest.VarType.Convert(), assignment.Source);
                        context.Store(context.GetLocalPointer(dest), source);
                    }
                    else if (dest.Type == OperandType.Attribute)
                    {
                        var elemPointer = context.GetAttributeElemPointer(dest.Value, true, null, out var elemType);
                        context.Store(elemPointer, context.Get(elemType, assignment.Source));
                    }
                    else if (dest.Type == OperandType.Argument)
                    {
                        var source = context.Get(dest.VarType.Convert(), assignment.Source);
                        context.Store(context.GetArgumentPointer(dest), source);
                    }
                    else
                    {
                        throw new NotImplementedException(dest.Type.ToString());
                    }
                }
                else if (node is AstOperation operation)
                {
                    Instructions.Generate(context, operation);
                }
            }
        }
    }
}
