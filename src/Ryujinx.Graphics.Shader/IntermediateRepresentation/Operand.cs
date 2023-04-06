using Ryujinx.Graphics.Shader.Decoders;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Shader.IntermediateRepresentation
{
    class Operand
    {
        public OperandType Type { get; }

        public int Value { get; }

        public INode AsgOp { get; set; }

        public HashSet<INode> UseOps { get; }

        private Operand()
        {
            UseOps = new HashSet<INode>();
        }

        public Operand(OperandType type) : this()
        {
            Type = type;
        }

        public Operand(OperandType type, int value) : this()
        {
            Type  = type;
            Value = value;
        }

        public Operand(Register reg) : this()
        {
            Type  = OperandType.Register;
            Value = PackRegInfo(reg.Index, reg.Type);
        }

        private static int PackRegInfo(int index, RegisterType type)
        {
            return ((int)type << 24) | index;
        }

        public Register GetRegister()
        {
            return new Register(Value & 0xffffff, (RegisterType)(Value >> 24));
        }

        public float AsFloat()
        {
            return BitConverter.Int32BitsToSingle(Value);
        }
    }
}