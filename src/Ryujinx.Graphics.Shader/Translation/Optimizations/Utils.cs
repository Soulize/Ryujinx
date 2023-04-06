using Ryujinx.Graphics.Shader.IntermediateRepresentation;

using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Translation.Optimizations
{
    static class Utils
    {
        public static Operation CreateLoadConstant(ShaderConfig config, int slot, int wordOffset)
        {
            int binding = config.ResourceManager.GetConstantBufferBinding(slot);

            return new Operation(
                Instruction.Load,
                StorageKind.ConstantBuffer,
                Local(),
                Const(binding),
                Const(0),
                Const(wordOffset >> 2),
                Const(wordOffset & 3));
        }

        public static bool TryGetConstantBuffer(ShaderConfig config, Operand operand, out int slot, out int offset)
        {
            slot = 0;
            offset = 0;

            if (!(operand.AsgOp is Operation operation) ||
                operation.Inst != Instruction.Load ||
                operation.StorageKind != StorageKind.ConstantBuffer ||
                operation.SourcesCount != 4)
            {
                return false;
            }

            Operand bindingIndex = operation.GetSource(0);
            Operand fieldIndex = operation.GetSource(1);
            Operand vecIndex = operation.GetSource(2);
            Operand elemIndex = operation.GetSource(3);

            if (bindingIndex.Type != OperandType.Constant ||
                fieldIndex.Type != OperandType.Constant ||
                fieldIndex.Value != 0 ||
                vecIndex.Type != OperandType.Constant ||
                elemIndex.Type != OperandType.Constant)
            {
                return false;
            }

            if (!config.ResourceManager.TryGetConstantBufferSlot(bindingIndex.Value, out slot))
            {
                return false;
            }

            offset = vecIndex.Value * 4 + elemIndex.Value;

            return true;
        }

        private static Operation FindBranchSource(BasicBlock block)
        {
            foreach (BasicBlock sourceBlock in block.Predecessors)
            {
                if (sourceBlock.Operations.Count > 0)
                {
                    if (sourceBlock.GetLastOp() is Operation lastOp && IsConditionalBranch(lastOp.Inst) && sourceBlock.Next == block)
                    {
                        return lastOp;
                    }
                }
            }

            return null;
        }

        private static bool IsConditionalBranch(Instruction inst)
        {
            return inst == Instruction.BranchIfFalse || inst == Instruction.BranchIfTrue;
        }

        private static bool BlockConditionsMatch(BasicBlock currentBlock, BasicBlock queryBlock)
        {
            // Check if all the conditions for the query block are satisfied by the current block.
            // Just checks the top-most conditional for now.

            Operation currentBranch = FindBranchSource(currentBlock);
            Operation queryBranch = FindBranchSource(queryBlock);

            Operand currentCondition = currentBranch?.GetSource(0);
            Operand queryCondition = queryBranch?.GetSource(0);

            // The condition should be the same operand instance.

            return currentBranch != null && queryBranch != null &&
                   currentBranch.Inst == queryBranch.Inst &&
                   currentCondition == queryCondition;
        }

        public static Operand FindLastOperation(Operand source, BasicBlock block)
        {
            if (source.AsgOp is PhiNode phiNode)
            {
                // This source can have a different value depending on a previous branch.
                // Ensure that conditions met for that branch are also met for the current one.
                // Prefer the latest sources for the phi node.

                for (int i = phiNode.SourcesCount - 1; i >= 0; i--)
                {
                    BasicBlock phiBlock = phiNode.GetBlock(i);

                    if (BlockConditionsMatch(block, phiBlock))
                    {
                        return phiNode.GetSource(i);
                    }
                }
            }

            return source;
        }
    }
}
