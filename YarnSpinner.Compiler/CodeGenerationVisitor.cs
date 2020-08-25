namespace Yarn.Compiler
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Text.RegularExpressions;
    using Antlr4.Runtime;
    using Antlr4.Runtime.Misc;
    using Antlr4.Runtime.Tree;
    using static Yarn.Instruction.Types;

    // the visitor for the body of the node does not really return ints,
    // just has to return something might be worth later investigating
    // returning Instructions
    internal partial class CodeGenerationVisitor : YarnSpinnerParserBaseVisitor<int>
    {
        internal Compiler compiler;

        public CodeGenerationVisitor(Compiler compiler)
        {
            this.compiler = compiler;
            this.loadOperators();
        }

        private void GenerateFormattedText(IList<IParseTree> nodes, out string outputString, out int expressionCount)
        {
            expressionCount = 0;
            StringBuilder composedString = new StringBuilder();

            // First, visit all of the nodes, which are either terminal
            // text nodes or expressions. if they're expressions, we
            // evaluate them, and inject a positional reference into the
            // final string.
            foreach (var child in nodes)
            {
                if (child is ITerminalNode)
                {
                    composedString.Append(child.GetText());
                }
                else if (child is ParserRuleContext)
                {
                    // assume that this is an expression (the parser only
                    // permits them to be expressions, but we can't specify
                    // that here) - visit it, and we will emit code that
                    // pushes the final value of this expression onto the
                    // stack. running the line will pop these expressions
                    // off the stack.
                    //
                    // Expressions in the final string are denoted as the
                    // index of the expression, surrounded by braces { }.
                    // However, we don't need to write the braces here
                    // ourselves, because the text itself that the parser
                    // captured already has them. So, we just need to write
                    // the expression count.
                    Visit(child);
                    composedString.Append(expressionCount);
                    expressionCount += 1;
                }
            }

            outputString = composedString.ToString().Trim();
        }

        private string[] GetHashtagTexts(YarnSpinnerParser.HashtagContext[] hashtags)
        {
            // Add hashtag
            var hashtagText = new List<string>();
            foreach (var tag in hashtags)
            {
                hashtagText.Add(tag.HASHTAG_TEXT().GetText());
            }
            return hashtagText.ToArray();
        }

        // a regular ol' line of text
        public override int VisitLine_statement(YarnSpinnerParser.Line_statementContext context)
        {
            // TODO: add support for line conditions:
            //
            // Mae: here's a line <<if true>>
            //
            // is identical to
            //
            // <<if true>>
            // Mae: here's a line
            // <<endif>>

            // Convert the formatted string into a string with
            // placeholders, and evaluate the inline expressions and push
            // the results onto the stack.
            GenerateFormattedText(context.line_formatted_text().children, out var composedString, out var expressionCount);

            // Get the lineID for this string from the hashtags if it has
            // one; otherwise, a new one will be created
            string lineID = compiler.GetLineID(context.hashtag());

            var hashtagText = GetHashtagTexts(context.hashtag());

            int lineNumber = context.Start.Line;

            string stringID = compiler.RegisterString(
                composedString.ToString(),
                compiler.CurrentNode.Name,
                lineID,
                lineNumber,
                hashtagText);

            compiler.Emit(OpCode.RunLine, new Operand(stringID), new Operand(expressionCount));

            return 0;
        }

        // a jump statement [[ NodeName ]]
        public override int VisitOptionJump(YarnSpinnerParser.OptionJumpContext context)
        {
            string destination = context.NodeName.Text.Trim();
            compiler.Emit(OpCode.RunNode, new Operand(destination));
            return 0;
        }

        public override int VisitOptionLink(YarnSpinnerParser.OptionLinkContext context)
        {

            // Create the formatted string and evaluate any inline
            // expressions            
            GenerateFormattedText(context.option_formatted_text().children, out var composedString, out var expressionCount);

            string destination = context.NodeName.Text.Trim();
            string label = composedString;

            int lineNumber = context.Start.Line;

            // getting the lineID from the hashtags if it has one
            string lineID = compiler.GetLineID(context.hashtag());

            var hashtagText = GetHashtagTexts(context.hashtag());

            string stringID = compiler.RegisterString(label, compiler.CurrentNode.Name, lineID, lineNumber, hashtagText);

            compiler.Emit(OpCode.AddOption, new Operand(stringID), new Operand(destination), new Operand(expressionCount));

            return 0;
        }

        // A set command: explicitly setting a value to an expression <<set
        // $foo to 1>>
        public override int VisitSetVariableToValue(YarnSpinnerParser.SetVariableToValueContext context)
        {
            // add the expression (whatever it resolves to)
            Visit(context.expression());

            // validate the type of this expression
            var expressionTypeVisitor = new ExpressionTypeVisitor(compiler.VariableDeclarations, compiler.Library, false);
            var expressionType = expressionTypeVisitor.Visit(context.expression());
            var variableType = expressionTypeVisitor.Visit(context.variable());

            if (expressionType != variableType) {
                throw new TypeException(context, $"{context.variable().GetText()} ({variableType}) cannot be assigned a {expressionType}");
            }

            // now store the variable and clean up the stack
            string variableName = context.variable().GetText();
            compiler.Emit(OpCode.StoreVariable, new Operand(variableName));
            compiler.Emit(OpCode.Pop);
            return 0;
        }

        // A set command: evaluating an expression where the operator is an
        // assignment-type
        public override int VisitSetExpression(YarnSpinnerParser.SetExpressionContext context)
        {
            // checking the expression is of the correct form
            var expression = context.expression();
            // TODO: is there really no more elegant way of doing this?!
            if (expression is YarnSpinnerParser.ExpMultDivModEqualsContext ||
                expression is YarnSpinnerParser.ExpPlusMinusEqualsContext)
            {
                // run the expression, it handles it from here
                Visit(expression);
            }
            else
            {
                // throw an error
                throw new ParseException(context, "Invalid expression inside assignment statement");
            }
            return 0;
        }

        public override int VisitCall_statement(YarnSpinnerParser.Call_statementContext context)
        {
            // Visit our function call, which will invoke the function
            Visit(context.function());

            // TODO: if this function returns a value, it will be pushed
            // onto the stack, but there's no way for the compiler to know
            // that, so the stack will not be tidied up. is there a way for
            // that to work?
            return 0;
        }

        // semi-free form text that gets passed along to the game for
        // things like <<turn fred left>> or <<unlockAchievement
        // FacePlant>>
        public override int VisitCommand_statement(YarnSpinnerParser.Command_statementContext context)
        {
            GenerateFormattedText(context.command_formatted_text().children, out var composedString, out var expressionCount);

            // TODO: look into replacing this as it seems a bit odd
            switch (composedString)
            {
                case "stop":
                    // "stop" is a special command that immediately stops
                    // execution
                    compiler.Emit(OpCode.Stop);
                    break;
                default:
                    compiler.Emit(OpCode.RunCommand, new Operand(composedString), new Operand(expressionCount));
                    break;
            }

            return 0;
        }

        // emits the required bytecode for the function call
        private void HandleFunction(string functionName, YarnSpinnerParser.ExpressionContext[] parameters)
        {
            // generate the instructions for all of the parameters
            foreach (var parameter in parameters)
            {
                Visit(parameter);
            }

            // push the number of parameters onto the stack
            compiler.Emit(OpCode.PushFloat, new Operand(parameters.Length));

            // then call the function itself
            compiler.Emit(OpCode.CallFunc, new Operand(functionName));
        }
        // handles emiting the correct instructions for the function
        public override int VisitFunction(YarnSpinnerParser.FunctionContext context)
        {
            string functionName = context.FUNC_ID().GetText();

            this.HandleFunction(functionName, context.expression());

            return 0;
        }

        // if statement ifclause (elseifclause)* (elseclause)? <<endif>>
        public override int VisitIf_statement(YarnSpinnerParser.If_statementContext context)
        {
            // label to give us a jump point for when the if finishes
            string endOfIfStatementLabel = compiler.RegisterLabel("endif");

            // handle the if
            var ifClause = context.if_clause();
            generateClause(endOfIfStatementLabel, ifClause.statement(), ifClause.expression());

            // all elseifs
            foreach (var elseIfClause in context.else_if_clause())
            {
                generateClause(endOfIfStatementLabel, elseIfClause.statement(), elseIfClause.expression());
            }

            // the else, if there is one
            var elseClause = context.else_clause();
            if (elseClause != null)
            {
                generateClause(endOfIfStatementLabel, elseClause.statement(), null);
            }

            compiler.CurrentNode.Labels.Add(endOfIfStatementLabel, compiler.CurrentNode.Instructions.Count);

            return 0;
        }
        internal void generateClause(string jumpLabel, YarnSpinnerParser.StatementContext[] children, YarnSpinnerParser.ExpressionContext expression)
        {
            string endOfClauseLabel = compiler.RegisterLabel("skipclause");

            // handling the expression (if it has one) will only be called
            // on ifs and elseifs
            if (expression != null)
            {
                // Validate the expression's type
                new ExpressionTypeVisitor(compiler.VariableDeclarations, compiler.Library, false).Visit(expression);

                // Code-generate the expression
                Visit(expression);
                                
                compiler.Emit(OpCode.JumpIfFalse, new Operand(endOfClauseLabel));
            }

            // running through all of the children statements
            foreach (var child in children)
            {
                Visit(child);
            }

            compiler.Emit(OpCode.JumpTo, new Operand(jumpLabel));

            if (expression != null)
            {
                compiler.CurrentNode.Labels.Add(endOfClauseLabel, compiler.CurrentNode.Instructions.Count);
                compiler.Emit(OpCode.Pop);
            }
        }

        // for the shortcut options (-> line of text <<if expression>>
        // indent statements dedent)+
        public override int VisitShortcut_option_statement(YarnSpinnerParser.Shortcut_option_statementContext context)
        {

            string endOfGroupLabel = compiler.RegisterLabel("group_end");

            var labels = new List<string>();

            int optionCount = 0;

            // For each option, create an internal destination label that,
            // if the user selects the option, control flow jumps to. Then,
            // evaluate its associated line_statement, and use that as the
            // option text. Finally, add this option to the list of
            // upcoming options.
            foreach (var shortcut in context.shortcut_option())
            {
                // Generate the name of internal label that we'll jump to
                // if this option is selected. We'll emit the label itself
                // later.
                string optionDestinationLabel = compiler.RegisterLabel($"shortcutoption_{compiler.CurrentNode.Name ?? "node"}_{optionCount + 1}");
                labels.Add(optionDestinationLabel);

                // This line statement may have a condition on it. If it
                // does, emit code that evaluates the condition, and skips
                // over the code that prepares and adds the option.
                string endOfClauseLabel = null;
                if (shortcut.line_statement().line_condition() != null)
                {
                    // Register the label we'll jump to if the condition
                    // fails. We'll add it later.
                    endOfClauseLabel = compiler.RegisterLabel("conditional_" + optionCount);

                    // Evaluate the condition, and jump to the end of
                    // clause if it evaluates to false.

                    Visit(shortcut.line_statement().line_condition().expression());

                    compiler.Emit(OpCode.JumpIfFalse, new Operand(endOfClauseLabel));
                }

                // We can now prepare and add the option.

                // Start by figuring out the text that we want to add. This
                // will involve evaluating any inline expressions.
                GenerateFormattedText(shortcut.line_statement().line_formatted_text().children, out var composedString, out var expressionCount);

                // Get the line ID from the hashtags if it has one
                string lineID = compiler.GetLineID(shortcut.line_statement().hashtag());

                // Get the hashtags for the line
                var hashtags = GetHashtagTexts(shortcut.line_statement().hashtag());

                // Register this string
                string labelStringID = compiler.RegisterString(composedString, compiler.CurrentNode.Name, lineID, shortcut.Start.Line, hashtags);

                // And add this option to the list.
                compiler.Emit(OpCode.AddOption, new Operand(labelStringID), new Operand(optionDestinationLabel), new Operand(expressionCount));

                // If we had a line condition, now's the time to generate
                // the label that we'd jump to if its condition is false.
                if (shortcut.line_statement().line_condition() != null)
                {
                    compiler.CurrentNode.Labels.Add(endOfClauseLabel, compiler.CurrentNode.Instructions.Count);

                    // JumpIfFalse doesn't change the stack, so we need to
                    // tidy up            
                    compiler.Emit(OpCode.Pop);
                }

                optionCount++;
            }

            // All of the options that we intend to show are now ready to
            // go.
            compiler.Emit(OpCode.ShowOptions);

            // The top of the stack now contains the name of the label we
            // want to jump to. Jump to it now.
            compiler.Emit(OpCode.Jump);

            // We'll now emit the labels and code associated with each
            // option.
            optionCount = 0;
            foreach (var shortcut in context.shortcut_option())
            {
                // Emit the label for this option's code
                compiler.CurrentNode.Labels.Add(labels[optionCount], compiler.CurrentNode.Instructions.Count);

                // Run through all the children statements of the shortcut
                // option.
                foreach (var child in shortcut.statement())
                {
                    Visit(child);
                }

                // Jump to the end of this shortcut option group.
                compiler.Emit(OpCode.JumpTo, new Operand(endOfGroupLabel));

                optionCount++;
            }

            // We made it to the end! Mark the end of the group, so we can
            // jump to it.
            compiler.CurrentNode.Labels.Add(endOfGroupLabel, compiler.CurrentNode.Instructions.Count);
            compiler.Emit(OpCode.Pop);

            return 0;
        }

        // the calls for the various operations and expressions first the
        // special cases (), unary -, !, and if it is just a value by
        // itself
        #region specialCaseCalls
        // (expression)
        public override int VisitExpParens(YarnSpinnerParser.ExpParensContext context)
        {
            return Visit(context.expression());
        }
        // -expression
        public override int VisitExpNegative(YarnSpinnerParser.ExpNegativeContext context)
        {
            Visit(context.expression());

            // TODO: temp operator call

            // Indicate that we are pushing one parameter
            compiler.Emit(OpCode.PushFloat, new Operand(1));

            compiler.Emit(OpCode.CallFunc, new Operand(TokenType.UnaryMinus.ToString()));

            return 0;
        }
        // (not NOT !)expression
        public override int VisitExpNot(YarnSpinnerParser.ExpNotContext context)
        {
            Visit(context.expression());

            // TODO: temp operator call

            // Indicate that we are pushing one parameter
            compiler.Emit(OpCode.PushFloat, new Operand(1));

            compiler.Emit(OpCode.CallFunc, new Operand(TokenType.Not.ToString()));

            return 0;
        }
        // variable
        public override int VisitExpValue(YarnSpinnerParser.ExpValueContext context)
        {
            return Visit(context.value());
        }
        #endregion

        // left OPERATOR right style expressions the most common form of
        // expressions for things like 1 + 3
        #region lValueOperatorrValueCalls
        internal void genericExpVisitor(YarnSpinnerParser.ExpressionContext left, YarnSpinnerParser.ExpressionContext right, int op)
        {
            Visit(left);
            Visit(right);

            // TODO: temp operator call

            // Indicate that we are pushing two items for comparison
            compiler.Emit(OpCode.PushFloat, new Operand(2));

            compiler.Emit(OpCode.CallFunc, new Operand(tokens[op].ToString()));
        }
        // * / %
        public override int VisitExpMultDivMod(YarnSpinnerParser.ExpMultDivModContext context)
        {
            genericExpVisitor(context.expression(0), context.expression(1), context.op.Type);

            return 0;
        }
        // + -
        public override int VisitExpAddSub(YarnSpinnerParser.ExpAddSubContext context)
        {
            genericExpVisitor(context.expression(0), context.expression(1), context.op.Type);

            return 0;
        }
        // < <= > >=
        public override int VisitExpComparison(YarnSpinnerParser.ExpComparisonContext context)
        {
            genericExpVisitor(context.expression(0), context.expression(1), context.op.Type);

            return 0;
        }
        // == !=
        public override int VisitExpEquality(YarnSpinnerParser.ExpEqualityContext context)
        {
            genericExpVisitor(context.expression(0), context.expression(1), context.op.Type);

            return 0;
        }
        // and && or || xor ^
        public override int VisitExpAndOrXor(YarnSpinnerParser.ExpAndOrXorContext context)
        {
            genericExpVisitor(context.expression(0), context.expression(1), context.op.Type);

            return 0;
        }
        #endregion

        // operatorEquals style operators, eg += these two should only be
        // called during a SET operation eg << set $var += 1 >> the left
        // expression has to be a variable the right value can be anything
        #region operatorEqualsCalls
        // generic helper for these types of expressions
        internal void opEquals(string varName, YarnSpinnerParser.ExpressionContext expression, int op)
        {
            // Get the current value of the variable
            compiler.Emit(OpCode.PushVariable, new Operand(varName));

            // run the expression
            Visit(expression);

            // Stack now contains [currentValue, expressionValue]

            // Indicate that we are pushing two items for comparison
            compiler.Emit(OpCode.PushFloat, new Operand(2));

            // now we evaluate the operator op will match to one of + - / *
            // %
            compiler.Emit(OpCode.CallFunc, new Operand(tokens[op].ToString()));

            // Stack now has the destination value now store the variable
            // and clean up the stack
            compiler.Emit(OpCode.StoreVariable, new Operand(varName));
            compiler.Emit(OpCode.Pop);
        }
        // *= /= %=
        public override int VisitExpMultDivModEquals(YarnSpinnerParser.ExpMultDivModEqualsContext context)
        {
            // call the helper to deal with this
            opEquals(context.variable().GetText(), context.expression(), context.op.Type);
            return 0;
        }
        // += -=
        public override int VisitExpPlusMinusEquals(YarnSpinnerParser.ExpPlusMinusEqualsContext context)
        {
            // call the helper to deal with this
            opEquals(context.variable().GetText(), context.expression(), context.op.Type);

            return 0;
        }
        #endregion

        // the calls for the various value types this is a wee bit messy
        // but is easy to extend, easy to read and requires minimal
        // checking as ANTLR has already done all that does have code
        // duplication though
        #region valueCalls
        public override int VisitValueVar(YarnSpinnerParser.ValueVarContext context)
        {
            return Visit(context.variable());
        }
        public override int VisitValueNumber(YarnSpinnerParser.ValueNumberContext context)
        {
            float number = float.Parse(context.NUMBER().GetText(), CultureInfo.InvariantCulture);
            compiler.Emit(OpCode.PushFloat, new Operand(number));

            return 0;
        }
        public override int VisitValueTrue(YarnSpinnerParser.ValueTrueContext context)
        {
            compiler.Emit(OpCode.PushBool, new Operand(true));

            return 0;
        }
        public override int VisitValueFalse(YarnSpinnerParser.ValueFalseContext context)
        {
            compiler.Emit(OpCode.PushBool, new Operand(false));
            return 0;
        }
        public override int VisitVariable(YarnSpinnerParser.VariableContext context)
        {
            string variableName = context.VAR_ID().GetText();
            compiler.Emit(OpCode.PushVariable, new Operand(variableName));

            return 0;
        }
        public override int VisitValueString(YarnSpinnerParser.ValueStringContext context)
        {
            // stripping the " off the front and back actually is this what
            // we want?
            string stringVal = context.STRING().GetText().Trim('"');

            compiler.Emit(OpCode.PushString, new Operand(stringVal));

            return 0;
        }
        // all we need do is visit the function itself, it will handle
        // everything
        public override int VisitValueFunc(YarnSpinnerParser.ValueFuncContext context)
        {
            Visit(context.function());

            return 0;
        }
        // null value
        public override int VisitValueNull(YarnSpinnerParser.ValueNullContext context)
        {
            compiler.Emit(OpCode.PushNull);
            return 0;
        }
        #endregion

        public override int VisitDeclare_statement(YarnSpinnerParser.Declare_statementContext context)
        {
            // Declare statements do not participate in code generation
            return 0;
        }

        // TODO: figure out a better way to do operators
        Dictionary<int, TokenType> tokens = new Dictionary<int, TokenType>();
        private void loadOperators()
        {
            // operators for the standard expressions
            tokens[YarnSpinnerLexer.OPERATOR_LOGICAL_LESS_THAN_EQUALS] = TokenType.LessThanOrEqualTo;
            tokens[YarnSpinnerLexer.OPERATOR_LOGICAL_GREATER_THAN_EQUALS] = TokenType.GreaterThanOrEqualTo;
            tokens[YarnSpinnerLexer.OPERATOR_LOGICAL_LESS] = TokenType.LessThan;
            tokens[YarnSpinnerLexer.OPERATOR_LOGICAL_GREATER] = TokenType.GreaterThan;

            tokens[YarnSpinnerLexer.OPERATOR_LOGICAL_EQUALS] = TokenType.EqualTo;
            tokens[YarnSpinnerLexer.OPERATOR_LOGICAL_NOT_EQUALS] = TokenType.NotEqualTo;

            tokens[YarnSpinnerLexer.OPERATOR_LOGICAL_AND] = TokenType.And;
            tokens[YarnSpinnerLexer.OPERATOR_LOGICAL_OR] = TokenType.Or;
            tokens[YarnSpinnerLexer.OPERATOR_LOGICAL_XOR] = TokenType.Xor;

            tokens[YarnSpinnerLexer.OPERATOR_MATHS_ADDITION] = TokenType.Add;
            tokens[YarnSpinnerLexer.OPERATOR_MATHS_SUBTRACTION] = TokenType.Minus;
            tokens[YarnSpinnerLexer.OPERATOR_MATHS_MULTIPLICATION] = TokenType.Multiply;
            tokens[YarnSpinnerLexer.OPERATOR_MATHS_DIVISION] = TokenType.Divide;
            tokens[YarnSpinnerLexer.OPERATOR_MATHS_MODULUS] = TokenType.Modulo;
            // operators for the set expressions these map directly to the
            // operator if they didn't have the =
            tokens[YarnSpinnerLexer.OPERATOR_MATHS_ADDITION_EQUALS] = TokenType.Add;
            tokens[YarnSpinnerLexer.OPERATOR_MATHS_SUBTRACTION_EQUALS] = TokenType.Minus;
            tokens[YarnSpinnerLexer.OPERATOR_MATHS_MULTIPLICATION_EQUALS] = TokenType.Multiply;
            tokens[YarnSpinnerLexer.OPERATOR_MATHS_DIVISION_EQUALS] = TokenType.Divide;
            tokens[YarnSpinnerLexer.OPERATOR_MATHS_MODULUS_EQUALS] = TokenType.Modulo;
        }
    }
}