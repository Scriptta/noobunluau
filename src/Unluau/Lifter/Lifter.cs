﻿// Copyright (c) Valence. All Rights Reserved.
// Licensed under the Apache License, Version 2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Unluau
{
    public class Lifter
    {
        private Chunk chunk;
        private DecompilerOptions options;
        private static int upvalueId = 0;

        private enum CaptureType : byte
        {
            Value,
            Reference,
            Upvalue
        }

        public Lifter(Chunk chunk, DecompilerOptions options)
        { 
            this.chunk = chunk;
            this.options = options;
        }

        public OuterBlock LiftProgram()
        {
            Function main = chunk.Functions[chunk.MainIndex];
            Registers registers = CreateRegisters(main);

            var block = new OuterBlock(LiftBlock(main, registers));

            // Note: these values doesn't get cleared after each test in Unluau.Test so we have to do it
            // manually here.
            Decleration.ResetCounters();

            return block;
        }

        private Block LiftBlock(Function function, Registers registers, int pcStart = 0, int pcStop = -1)
        {
            if (pcStop == -1)
                pcStop = function.Instructions.Count;

            Block block = new Block();

            for (int pc = pcStart; pc < pcStop; pc++)
            {
                Instruction instruction = function.Instructions[pc];
                OpProperties properties = instruction.GetProperties();

                switch (properties.Code)
                {
                    case OpCode.LOADKX:
                    case OpCode.LOADK:
                    {
                        Constant target = properties.Code == OpCode.LOADK 
                            ? function.Constants[instruction.D] : function.GetConstant(++pc);

                        registers.LoadRegister(instruction.A, ConstantToExpression(target), block);
                        break;
                    }
                    case OpCode.LOADN:
                    {
                        registers.LoadRegister(instruction.A, new NumberLiteral(instruction.D), block);
                        break;
                    }
                    case OpCode.GETGLOBAL:
                    {
                        Constant target = function.GetConstant(++pc);

                        registers.LoadRegister(instruction.A, GetConstantAsGlobal(target), block);
                        break;
                    }
                    case OpCode.SETGLOBAL:
                    {
                        Constant target = function.GetConstant(++pc);

                        block.AddStatement(new Assignment(GetConstantAsGlobal(target), registers.GetExpression(instruction.A)));
                        break;
                    }
                    case OpCode.GETIMPORT:
                    {
                        ImportConstant target = (ImportConstant)function.Constants[instruction.D];

                        if (target.Value.Count > 1)
                        {
                            Expression expression = new NameIndex(new Global(target.Value[0].Value), target.Value[1].Value);

                            if (target.Value.Count > 2)
                                expression = new NameIndex(expression, target.Value[2].Value);

                            registers.LoadRegister(instruction.A, expression, block);
                        } 
                        else
                            registers.LoadRegister(instruction.A, GetConstantAsGlobal(target), block);
                        
                        // Skip next instruction (we used the constant instead of AUX)
                        pc++;
                        break;
                    }
                    case OpCode.NAMECALL:
                    case OpCode.GETTABLEKS:
                    {   
                        Constant target = function.GetConstant(++pc);
                        string targetValue = (target as StringConstant)!.Value;

                        bool isNamecall = properties.Code == OpCode.NAMECALL;
                        Expression nameExpression = registers.GetExpression(instruction.B);

                        Expression expression = new NameIndex(nameExpression, targetValue, isNamecall);

                        // This is a namecall and the function name is format we need to account for string interpolation
                        if (isNamecall && targetValue == "format")
                        {
                            // Extract the name index from the expression
                            NameIndex? nameIndex = expression as NameIndex;

                            if (options.PerferStringInterpolation)
                            {
                                // If we perfer string interpolation set the expression as an interpolated string
                                string format = (((LocalExpression)nameIndex!.Expression).Expression as StringLiteral)!.Value;
                                expression = new InterpolatedStringLiteral(format, new List<Expression>());
                            } 
                            else
                            {
                                // Otherwise add an expression group around the string literal
                                nameIndex!.Expression = new ExpressionGroup(nameIndex!.Expression);
                                expression = nameIndex;
                            }
                        }

                        registers.LoadRegister(instruction.A, expression, block);
                        break;
                    }
                    case OpCode.GETTABLEN:
                    {
                        ExpressionIndex index = new ExpressionIndex(registers.GetExpression(instruction.B), new NumberLiteral(instruction.C + 1));

                        registers.LoadRegister(instruction.A, index, block);
                        break;
                    }
                    case OpCode.GETTABLE:
                    {
                        ExpressionIndex expressionIndex = new ExpressionIndex(registers.GetExpression(instruction.B), registers.GetExpression(instruction.C));

                        registers.LoadRegister(instruction.A, expressionIndex, block);
                        break;
                    }
                    case OpCode.CALL:
                    {
                        IList<Expression> arguments = new List<Expression>();

                        Expression callFunction = registers.GetExpression(instruction.A);

                        int numArgs = instruction.B > 0 ? instruction.B : (registers.Top - instruction.A) + 1;

                        for (int slot = 1 + IsSelf(callFunction); slot < numArgs; ++slot)
                        {
                            int register = instruction.A + slot;
                            Expression expression = registers.GetExpression(register);

                            if (expression != null)
                            {
                                arguments.Add(expression);

                                registers.FreeRegister(register, block);
                            }
                            
                        }

                        // Free the function register
                        registers.FreeRegister(instruction.A, block);

                        Expression call = new FunctionCall(callFunction, arguments);

                        // Note: we do this for string interpolation to work
                        var callFunctionValue = ((LocalExpression)callFunction).Expression;
                        if (callFunctionValue is InterpolatedStringLiteral)
                        {
                            InterpolatedStringLiteral literal = (InterpolatedStringLiteral)callFunctionValue;

                            literal.Arguments = arguments;
                            call = literal;
                        }

                        if (instruction.C - 1 == 0)
                            block.AddStatement(call);
                        else
                            registers.LoadRegister(instruction.A, call, block);
                        break;
                    }
                    case OpCode.MOVE:
                    {
                        var fromExpression = registers.GetExpression(instruction.B);
                        var toExpression = registers.GetExpression(instruction.A);

                        // If our target register is empty, then load into that register
                        if (toExpression is null)
                            registers.LoadRegister(instruction.A, fromExpression, block);
                        else
                        {
                            // Now create the reassignment
                            block.AddStatement(new Assignment(toExpression, fromExpression));
                        }
                        break;
                    }
                    case OpCode.LOADNIL:
                    {
                        registers.LoadRegister(instruction.A, new NilLiteral(), block);
                        break;
                    }
                    case OpCode.LOADB:
                    {
                        registers.LoadRegister(instruction.A, new BooleanLiteral(instruction.B == 1), block);
                        break;
                    }

                    // Uses register for right hand expression
                    case OpCode.ADD:
                    case OpCode.SUB:
                    case OpCode.MUL:
                    case OpCode.DIV:
                    case OpCode.MOD:
                    case OpCode.POW:
                    // Uses constant for right hand expression
                    case OpCode.ADDK:
                    case OpCode.SUBK:
                    case OpCode.MULK:
                    case OpCode.DIVK:
                    case OpCode.MODK:
                    case OpCode.POWK:
                    {
                        Expression right =  (properties.Code >= OpCode.ADDK) ? ConstantToExpression(function.Constants[instruction.C]) 
                            : registers.GetExpression(instruction.C);
                        Expression left = registers.GetExpression(instruction.B);

                        // Get the equivalent binary operation for the opcode
                        BinaryExpression.BinaryOperation operation = BinaryExpression.GetBinaryOperation(properties.Code);

                        // Get the prescedence of the left and right hand expressions and the current operation
                        int leftPresedence = BinaryExpression.GetBinaryOperationPrescedence(left), rightPrescedence = BinaryExpression.GetBinaryOperationPrescedence(right);
                        int currentPrescedence = BinaryExpression.GetBinaryOperationPrescedence(operation);
                        
                        // If the left hand expression has a lower prescedence than the current operation, we need to wrap it in an expression group
                        if (leftPresedence < currentPrescedence && leftPresedence > 0)
                            left = new ExpressionGroup(left);

                        // If the right hand expression has a lower prescedence than the current operation, we need to wrap it in an expression group
                        if (rightPrescedence < currentPrescedence && rightPrescedence > 0)
                            right = new ExpressionGroup(right);

                        BinaryExpression binary = new BinaryExpression(left, operation, right);

                        registers.LoadRegister(instruction.A, binary, block);
                        break;
                    }
                    case OpCode.CONCAT:
                    {
                        // Kinda annoying, gotta unravel the whole TING
                        Expression expression = BuildConcat(registers, instruction.B, instruction.C);

                        registers.LoadRegister(instruction.A, expression, block);
                        break;
                    }
                    case OpCode.NOT:
                    case OpCode.LENGTH:
                    case OpCode.MINUS:
                    {
                        registers.LoadRegister(instruction.A, registers.GetExpression(instruction.B), block);
                        break;
                    }
                    case OpCode.SETTABLEKS:
                    {
                        StringConstant target = (StringConstant)function.GetConstant(++pc);
                        Expression table = registers.GetExpression(instruction.B), tableValue = ((LocalExpression)table).Expression;

                        if (options.InlineTableDefintions && tableValue is TableLiteral)
                        {
                            if (((LocalExpression)table).Decleration.Referenced == 1)
                                tableValue = registers.GetRefExpressionValue(instruction.B);

                            TableLiteral tableLiteral = (TableLiteral)tableValue;

                            if (tableLiteral.MaxEntries > tableLiteral.Entries.Count)
                            {
                                tableLiteral.AddEntry(new TableLiteral.Entry(GetConstantAsGlobal(target), registers.GetExpression(instruction.A)));
                                break;
                            }
                        }

                        NameIndex nameIndex = new NameIndex(table, target.Value);

                        block.AddStatement(new Assignment(nameIndex, registers.GetExpression(instruction.A)));
                        break;
                    }
                    case OpCode.SETTABLE:
                    {
                        Expression expression = registers.GetRefExpressionValue(instruction.C), value = registers.GetExpression(instruction.A);
                        Expression table = registers.GetExpression(instruction.B, false), tableValue = ((LocalExpression)table).Expression;           

                        if (options.InlineTableDefintions && tableValue is TableLiteral)
                        {
                            TableLiteral tableLiteral = (TableLiteral)tableValue;

                            if (tableLiteral.MaxEntries > tableLiteral.Entries.Count)
                            {
                                tableLiteral.AddEntry(new TableLiteral.Entry(expression, value));
                                break;
                            }
                        }

                        if (expression is NumberLiteral || expression is StringLiteral)
                            expression = new ExpressionIndex(table, expression);

                        block.AddStatement(new Assignment(expression, value));
                        break;
                    }
                    case OpCode.NEWTABLE:
                    {
                        // Todo: rewrite this stuff so it all works with 64 bit integers
                        int arraySize = Convert.ToInt32(function.Instructions[++pc].Value);
                        int hashSize = instruction.B == 0 ? 0 : (1 << (instruction.B - 1));

                        TableLiteral expression;

                        if (arraySize > 0)
                            expression = new TableLiteral(arraySize, true);
                        else
                            expression = new TableLiteral(hashSize, false);

                        if (options.InlineTableDefintions && hashSize > 0)
                            expression.MaxEntries = hashSize;

                        registers.LoadRegister(instruction.A, expression, block);
                        break;
                    }
                    case OpCode.SETLIST:
                    {
                        TableLiteral tableLiteral = (TableLiteral)registers.GetExpressionValue(instruction.A);

                        for (int slot = instruction.B; slot <= instruction.C; slot++)
                            tableLiteral.AddEntry(new TableLiteral.Entry(null, registers.GetRefExpressionValue(slot)));

                        // Skip next instruction because we didn't use AUX
                        pc++;
                        break;
                    }
                    case OpCode.DUPTABLE:
                    {
                        TableConstant target = (TableConstant)function.Constants[instruction.D];

                        TableLiteral tableLiteral = new TableLiteral(target.Value.Count, false);

                        if (options.InlineTableDefintions)    
                            tableLiteral.MaxEntries = target.Value.Count;
                        

                        registers.LoadRegister(instruction.A, tableLiteral, block);
                        break;
                    }
                    case OpCode.GETVARARGS:
                    { 
                        int count = instruction.B - 1;

                        if (count > 0)
                        {
                            ExpressionList expressions = new ExpressionList(count);

                            for (int i = 0; i < count; ++i)
                            {
                                int register = instruction.A + i;

                                expressions.Append(registers.LoadTempRegister(register, new Vararg(), block, Decleration.DeclerationType.Local));

                                // All of the variables need to be referenced more than once so that we don't end up 
                                // printing '...' for each of their references.
                                registers.GetDeclerationDict()[register].Referenced = 2;
                            }

                            block.AddStatement(new LocalAssignment(expressions, new Vararg()));
                        }
                        else
                            registers.LoadRegister(instruction.A, new Vararg(), block);
                        break;
                    }
                    case OpCode.JUMPIF:
                    case OpCode.JUMPIFNOT:
                    case OpCode.JUMPIFEQ:
                    case OpCode.JUMPIFNOTEQ:
                    case OpCode.JUMPIFNOTLT:
                    case OpCode.JUMPIFLT:
                    case OpCode.JUMPIFNOTLE:
                    case OpCode.JUMPIFLE:
                    case OpCode.JUMPXEQKNIL:
                    case OpCode.JUMPXEQKB:
                    case OpCode.JUMPXEQKN:
                    case OpCode.JUMPXEQKS:
                    {
                        bool auxUsed = false;
                        Expression condition = GetCondition(registers, instruction, function.Instructions[pc + 1], function.Constants, ref auxUsed);

                        Registers newRegisters = new Registers(registers);
                        Statement statement = new IfElse(condition, LiftBlock(function, newRegisters, auxUsed ? pc + 2 : pc + 1, (pc += instruction.D) + 1));

                        Instruction nextInstruction = function.Instructions[pc];
                        switch (nextInstruction.GetProperties().Code)
                        {
                            case OpCode.JUMP:
                            {
                                IfElse ifElse = statement as IfElse;

                                ifElse.ElseBody = LiftBlock(function, registers, pc + 1, (pc += nextInstruction.D) + 1);
                                Block elseBodyBlock = ifElse.ElseBody as Block; // We know it has to be a block

                                // Little optimization to include elseifs
                                if (elseBodyBlock.Statements.Count == 1 && elseBodyBlock.Statements[0] is IfElse)
                                    ifElse.ElseBody = elseBodyBlock.Statements[0] as IfElse;
                                break;
                            }
                            case OpCode.JUMPBACK:
                            {
                                IfElse ifElse = statement as IfElse;

                                statement = new WhileLoop(ifElse.Condition, ifElse.IfBody);
                                break;
                            }
                        }

                        // Check if the current instruction is a simple jump instruction
                        if ((properties.Code == OpCode.JUMPIF || properties.Code == OpCode.JUMPIFNOT) && statement is IfElse)
                        {
                            // These are the two instructions that are always used for `and` and `or` statements.
                            // We need to check if the register that the unary operator references is the same as 
                            // the one being reassigned in the body of the if statement.
                            byte register = instruction.A;
                            IfElse ifElse = (IfElse)statement;

                            // If our condition is a unary expression and the body of the if statement is a local assignment
                            // we can go on to check if the unary expression is referencing the same register as the local assignment.
                            if (ifElse.IfBody.Statements.Count == 1
                                && ifElse.IfBody.Statements.First() is LocalAssignment localAssignment)
                            {
                                LocalExpression? expression = localAssignment.Expression as LocalExpression;
                                UnaryExpression? unaryCondition = ifElse.Condition as UnaryExpression;

                                if (expression!.Decleration.Register == register)
                                {
                                    // We know that the current jump instruction represents an `and` or `or` statement.
                                    Expression left = unaryCondition is null ? ifElse.Condition : unaryCondition!.Expression; 
                                    Expression right = expression!.Expression;

                                    var operation = unaryCondition is null ? BinaryExpression.BinaryOperation.And : BinaryExpression.BinaryOperation.Or;

                                    registers.LoadRegister(register, new BinaryExpression(left, operation, right), block);
                                    break;
                                }
                            }
                        }

                        block.AddStatement(statement);
                        break;
                    }
                    case OpCode.NEWCLOSURE:
                    case OpCode.DUPCLOSURE:
                    {
                        int functionId = properties.Code == OpCode.DUPCLOSURE ? ((ClosureConstant)function.Constants[instruction.D]).Value : instruction.D;

                        Function newFunction = function.GetFunction(functionId);
                        Registers newRegisters = CreateRegisters(newFunction);

                        while (newFunction.Upvalues.Count < newFunction.MaxUpvalues)
                        {
                            Instruction capture = function.Instructions[++pc];

                            if (capture.GetProperties().Code != OpCode.CAPTURE)
                                throw new DecompilerException(Stage.Lifter, "Expected capture instruction following NEWCLOSURE/DUPCLOSURE");

                            LocalExpression? expression;

                            switch ((CaptureType)capture.A)
                            {
                                case CaptureType.Value:
                                    var type = options.RenameUpvalues ? Decleration.DeclerationType.Upvalue : Decleration.DeclerationType.Local;

                                    expression = (LocalExpression)registers.GetExpression(capture.B);
                                    expression.Decleration.Type = type;
                                    break;
                                case CaptureType.Upvalue:
                                    // We've got an existing upvalue
                                    expression = function.Upvalues[capture.B];
                                    break;
                                default:
                                    throw new DecompilerException(Stage.Lifter, $"Unknown capture type {capture.A}");
                            }

                            expression!.Decleration.Referenced++;
                            
                            newFunction.Upvalues.Add(expression);
                        }

                        registers.LoadRegister(instruction.A, new Closure(newRegisters.GetDeclerations(), newFunction.IsVararg, LiftBlock(newFunction, newRegisters), functionId), block);
                        break;
                    }
                    case OpCode.GETUPVAL:
                    {
                        LocalExpression expression = function.Upvalues[instruction.B];

                        registers.LoadRegister(instruction.A, expression, block, expression.Decleration.Type);
                        break;
                    }
                    case OpCode.RETURN:
                    {
                        IList<Expression> expressions = new List<Expression>();

                        int numArgs = instruction.B > 0 ? instruction.B - 1: registers.Top - instruction.A + 1;

                        for (int slot = 0; slot < numArgs; ++slot)
                            expressions.Add(registers.GetExpression(instruction.A + slot));

                        if (numArgs > 0)
                            block.AddStatement(new Return(expressions));
                        break;
                    }
                    default:
                    {
                        // If we don't handle the instruction and it has an auxiliary value, we need to skip it
                        if (properties.HasAux)
                            pc++;
                        break;
                    }
                }
            }

            registers.FreeRegisters(block);

            return block;
        }

        private int IsSelf(Expression expression)
        {
            Expression value = ((LocalExpression)expression).Expression;

            if (value is NameIndex)
            {
                NameIndex nameIndex = (NameIndex)value;
                return nameIndex.IsSelf ? 1 : 0;
            }
            return 0;
        }

        private Expression BuildConcat(Registers registers, int from, int to)
        {
            Expression left = registers.GetExpression(from);
            Expression right = (from + 1) == to ? registers.GetExpression(to) : BuildConcat(registers, from + 1, to);

            return new BinaryExpression(left, BinaryExpression.BinaryOperation.Concat, right);
        }

        private Expression GetCondition(Registers registers, Instruction instruction, Instruction aux, IList<Constant> constants, ref bool auxUsed)
        {
            auxUsed = false;

            OpCode code = instruction.GetProperties().Code;

            if (code == OpCode.JUMPIF || code == OpCode.JUMPIFNOT)
                return code == OpCode.JUMPIFNOT ? registers.GetExpression(instruction.A)
                            : new UnaryExpression(registers.GetExpression(instruction.A), UnaryExpression.UnaryOperation.Not);

            auxUsed = true;

            BinaryExpression.BinaryOperation operation = BinaryExpression.GetBinaryOperation(code);

            if (operation == BinaryExpression.BinaryOperation.CompareGt || operation == BinaryExpression.BinaryOperation.CompareGe)
                return new BinaryExpression(registers.GetExpression((int)aux.Value), operation, registers.GetExpression(instruction.A));

            Expression right = registers.GetExpression((int)aux.Value);

            if (operation == BinaryExpression.BinaryOperation.CompareEq && code != OpCode.JUMPIFEQ)
            {
                if (code == OpCode.JUMPXEQKN || code == OpCode.JUMPXEQKS)
                    right = ConstantToExpression(constants[(int)aux.Value & 0xffffff]);
                else
                    right = ConstantToExpression(constants[(int)aux.Value]);
            }

            return new BinaryExpression(registers.GetExpression(instruction.A), operation, right);
        }

        private Registers CreateRegisters(Function function)
        {
            IDictionary<int, Decleration> declerations = LoadDeclerations(function);
            IDictionary<int, Expression> expressions = LoadExpressions(declerations);


            return new Registers(function, declerations, expressions, options);
        }

        private IDictionary<int, Decleration> LoadDeclerations(Function function)
        {
            IDictionary<int, Decleration> declerations = new Dictionary<int, Decleration>();

            if (function.DebugInfo != null)
            {
                // Load the entire debug info into the declerations
                foreach (LocalVariable variable in function.DebugInfo.Locals)
                    declerations[variable.Slot] = new Decleration(variable);
            }
            else
            {
                for (int slot = 0; slot < function.Parameters; slot++)
                    declerations[slot] = new Decleration(slot, "arg" + (slot + 1), 0);
            }

            return declerations;
        }

        private IDictionary<int, Expression> LoadExpressions(IDictionary<int, Decleration> declerations)
        {
            IDictionary<int, Expression> expressions = new Dictionary<int, Expression>();

            foreach (KeyValuePair<int, Decleration> decleration in declerations)
                expressions[decleration.Key] = new LocalExpression(null, decleration.Value);

            return expressions;
        }

        private Expression ConstantToExpression(Constant constant)
        {
            if (constant is StringConstant)
                return new StringLiteral(((StringConstant)constant).Value);

            if (constant is NumberConstant)
                 return new NumberLiteral(((NumberConstant)constant).Value);

            if (constant is BoolConstant)
                return new BooleanLiteral(((BoolConstant)constant).Value);

            if (constant is NilConstant)
                return new NilLiteral();

            if (constant is ImportConstant)
            {
                ImportConstant import = (ImportConstant)constant;
                IList<string> names = new List<string>(import.Value.Count);

                foreach (StringConstant stringConstant in import.Value)
                    names.Add(stringConstant.Value);

                return new Global(names);
            }

            throw new DecompilerException(Stage.Lifter, "unexpected constant passed to 'ConstantToExpression'");
        }

        private Expression GetConstantAsGlobal(Constant constant)
        {
            Expression expression;

            if ((expression = ConstantToExpression(constant)) is StringLiteral)
                return new Global(((StringLiteral)expression).Value);

            if (constant.Type != ConstantType.Import)
                throw new DecompilerException(Stage.Lifter, $"unable to get Import from constant of type \"{constant.Type}\"");

            return expression;
        }
    }
}
