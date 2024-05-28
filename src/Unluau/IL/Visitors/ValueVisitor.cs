﻿using System.Xml.Linq;
using Unluau.Decompile.IL.Statements;
using Unluau.Decompile.IL.Statements.Blocks;
using Unluau.Decompile.IL.Statements.Instructions;
using Unluau.Decompile.IL.Values;
using Unluau.Decompile.IL.Values.Binaries;
using Unluau.Decompile.IL.Values.Conditions;
using Unluau.Decompile.IL.Values.Unaries;

namespace Unluau.Decompile.IL.Visitors
{
    /// <summary>
    /// Visits the IL and resolves references if possible.
    /// </summary>
    public class ValueVisitor : Visitor
    {
        private BasicBlock? _lastBlock;

        public override bool Visit(Closure node)
        {
            // First we need to resolve all of our references.
            node.VisitChildren(this);

            // Now we need to remove all of the dead instructions.
            node.VisitChildren(this);

            return false;
        }
        public override bool Visit(BasicBlock node)
        {
            // Visit the block's children and update the scope.
            VisitBlockChildren(node);

            return false;
        }

        public override bool Visit(IfBlock node)
        {
            node.Condition.Visit(this);

            return Visit(node as BasicBlock);
        }

        public override bool Visit(Call node)
        {
            node.CallResult = ResolveCallResult(node.CallResult);

            // We don't want to propagate the results from a function call. They need to be
            // contained in a local variable.
            if (node.Slots.Length > 1)
            {
                foreach (var slot in node.Slots)
                    slot.References++;
            }
            else if (node.Slots.Length == 1)
                TryDelete(node, node.Slots.First());

            return true;
        }

        public override bool Visit(Equals node)
        {
            node.Right = ResolveValue(node.Right);
            node.Left = ResolveValue(node.Left);

            return true;
        }

        public override bool Visit(NotEquals node)
        {
            node.Right = ResolveValue(node.Right);
            node.Left = ResolveValue(node.Left);

            return true;
        }

        public override bool Visit(Test node)
        {
            node.Value = ResolveValue(node.Value);

            return true;
        }

        public override bool Visit(NotTest node)
        {
            node.Value = ResolveValue(node.Value);

            return true;
        }

        public override bool Visit(GetIndex node)
        {
            if (!TryDelete(node, node.Slot))
                node.Index = ResolveIndex(node.Index);

            return true;
        }

        public override bool Visit(GetIndexSelf node)
        {
            return Visit(node as GetIndex);
        }

        public override bool Visit(SetIndex node)
        {
            node.Index = ResolveIndex(node.Index);
            node.Value = ResolveValue(node.Value);

            return true;
        }

        public override bool Visit(SetGlobal node)
        {
            node.Global = (Global)ResolveValue(node.Global);
            node.Value = ResolveValue(node.Value);
            
            return true;
        }

        public override bool Visit(LoadValue node)
        {
            TryDelete(node, node.Slot);

            node.Value = ResolveValue(node.Value);

            return true;
        }

        public override bool Visit(Move node)
        {
            if (node.Source.References > 1 || node.Target.References == 1)
            {
                node.Target.Id = node.Source.Id;
                node.Target.References += node.Source.References;

                // The `_lastBlock` variable should not be null because to get to this instruction
                // we need to have processed a block.
                _lastBlock!.Statements.Remove(node);
            }

            return true;
        }

        public override bool Visit(Return node)
        {
            node.Values = ResolveValueList(node.Values);

            return true;
        }

        /// <summary>
        /// Resolves a list of values in the IL.
        /// </summary>
        /// <param name="values">The values.</param>
        /// <returns>The resolved values.</returns>
        private static BasicValue[] ResolveValueList(BasicValue[] values)
        {
            var resolved = new BasicValue[values.Length];

            for (int i = 0; i < values.Length; ++i)
                resolved[i] = ResolveValue(values[i]);

            return resolved;
        }

        /// <summary>
        /// Resolves a basic value in the IL.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The resolved value.</returns>
        private static BasicValue ResolveValue(BasicValue value)
        {
            if (value is Reference reference)
                return ResolveReference(reference);
            
            if (value is Values.Index index)
                return ResolveIndex(index);
            
            if (value is Table table)
                ResolveTable(table);

            if (value is Concat concat)
                concat.Values = ResolveValueList(concat.Values);

            if (value is BasicUnary unary)
                unary.Value = ResolveValue(unary.Value);

            if (value is BasicBinary binary)
            {
                binary.Left = ResolveValue(binary.Left);
                binary.Right = ResolveValue(binary.Right);
            }

            return value;
        }

        /// <summary>
        /// Resolves a reference to a register slot.
        /// </summary>
        /// <param name="reference">The reference.</param>
        /// <returns>The resolved value.</returns>
        private static BasicValue ResolveReference(Reference reference)
        {
            // If we only have one reference to a slot, then we can just replace this reference
            // with its BasicValue. 
            if (reference.Slot.References == 1)
            {
                // We set the number of references to a negative value so that the load instruction 
                // for this slot can be removed.
                reference.Slot.References = -1;

                return reference.Slot.Value;
            }

            return reference;
        }

        /// <summary>
        /// Resolves an index operation.
        /// </summary>
        /// <param name="index">The reference.</param>
        /// <returns>The resolved index.</returns>
        private static Values.Index ResolveIndex(Values.Index index)
        {
            index.Indexable = ResolveValue(index.Indexable);
            index.Key = ResolveValue(index.Key);

            return index;
        }

        /// <summary>
        /// Resolves a table reference.
        /// </summary>
        /// <param name="table">The reference.</param>
        /// <returns>The resolved table value.</returns>
        private static Table ResolveTable(Table table)
        {
            foreach (var entry in table.Entries)
                entry.Value = ResolveValue(entry.Value);

            return table;
        }

        /// <summary>
        /// Resolves a call operation.
        /// </summary>
        /// <param name="call">The call.</param>
        /// <returns>The resolved call.</returns>
        private static CallResult ResolveCallResult(CallResult call)
        {
            call.Callee = ResolveValue(call.Callee);
            call.Arguments = ResolveValueList(call.Arguments);

            return call;
        }

        /// <summary>
        /// Tries to delete an instruction from the instructions list if its "dead".
        /// </summary>
        /// <param name="node">The instruction</param>
        /// <param name="slot">Its slot.</param>
        /// <returns>True if deleted.</returns>
        private bool TryDelete(Instruction node, Slot slot)
        {
            if (slot.References == -1 && _lastBlock != null)
            {
                _lastBlock.Statements.Remove(node);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Visits the children of a block and sets the last block.
        /// </summary>
        /// <param name="block">The block.</param>
        private void VisitBlockChildren(BasicBlock block)
        {
            var previousBlock = _lastBlock;

            _lastBlock = block;

            // We call .ToList() to copy the list so that there are no concurrency issues. In the end
            // we do end up adding/removing items in the statements list.
            foreach (var statement in block.Statements.ToList())
                statement.Visit(this);

            _lastBlock = previousBlock;
        }
    }
}
