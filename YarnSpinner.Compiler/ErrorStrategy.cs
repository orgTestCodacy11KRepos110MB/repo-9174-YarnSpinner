using Antlr4.Runtime;
using Yarn.Compiler;

class ErrorStrategy : DefaultErrorStrategy
{
    /// <inheritdoc/>
    protected override void ReportNoViableAlternative(Parser recognizer, NoViableAltException e)
    {
        string msg = null;

        if (this.IsInsideRule<YarnSpinnerParser.If_statementContext>(recognizer) && recognizer.RuleContext is YarnSpinnerParser.StatementContext && e.OffendingToken.Type == YarnSpinnerLexer.COMMAND_ELSE)
        {
            // We are inside an if statement, we're attempting to parse a
            // statement, and we got an '<<', 'else', and we weren't able
            // to match that. The programmer included an extra '<<else>>'.
            var enclosingIfStatement = this.GetEnclosingRule<YarnSpinnerParser.If_statementContext>(recognizer);

            msg = $"More than one <<else>> statement in an <<if>> statement isn't allowed";
        }
        else if (e.StartToken.Type == YarnSpinnerLexer.COMMAND_START && e.OffendingToken.Type == YarnSpinnerLexer.COMMAND_END)
        {
            // We saw a << immediately followed by a >>. The programmer
            // forgot to include command text.
            msg = $"Command text expected";
        }

        if (msg == null)
        {
            msg = $"Unexpected \"{e.OffendingToken.Text}\" while reading a {recognizer.RuleNames[recognizer.RuleContext.RuleIndex]}";
        }

        recognizer.NotifyErrorListeners(e.OffendingToken, msg, e);
    }

    /// <inheritdoc/>
    protected override void ReportInputMismatch(Parser recognizer, InputMismatchException e)
    {
        string msg = null;

        switch (recognizer.RuleContext)
        {
            case YarnSpinnerParser.If_statementContext ifStatement:
                if (e.OffendingToken.Type == YarnSpinnerLexer.BODY_END)
                {
                    // We have exited a body in the middle of an if
                    // statement. The programmer forgot to include an
                    // <<endif>>.
                    msg = $"Expected an <<endif>> to match the <<if>> statement on line {ifStatement.Start.Line}";
                }
                else if (e.OffendingToken.Type == YarnSpinnerLexer.COMMAND_ELSE && recognizer.GetExpectedTokens().Contains(YarnSpinnerLexer.COMMAND_ENDIF))
                {
                    // We saw an else, but we expected to see an endif. The
                    // programmer wrote an additional <<else>>.
                    msg = $"More than one <<else>> statement in an <<if>> statement isn't allowed";
                }

                break;
            case YarnSpinnerParser.VariableContext variable:
                if (e.OffendingToken.Type == YarnSpinnerLexer.FUNC_ID)
                {
                    // We're parsing a variable (which starts with a '$'),
                    // but we encountered a FUNC_ID (which doesn't). The
                    // programmer forgot to include the '$'.
                    msg = "Variable names need to start with a $";
                }

                break;
        }

        if (msg != null)
        {
            this.NotifyErrorListeners(recognizer, msg, e);
        }
        else
        {
            base.ReportInputMismatch(recognizer, e);
        }
    }

    private bool IsInsideRule<TRuleType>(Parser recognizer)
        where TRuleType : RuleContext
    {
        RuleContext currentContext = recognizer.RuleContext;

        while (currentContext != null)
        {
            if (currentContext.GetType() == typeof(TRuleType))
            {
                return true;
            }

            currentContext = currentContext.Parent;
        }

        return false;
    }

    private TRuleType GetEnclosingRule<TRuleType>(Parser recognizer)
        where TRuleType : RuleContext
    {
        RuleContext currentContext = recognizer.RuleContext;

        while (currentContext != null)
        {
            if (currentContext.GetType() == typeof(TRuleType))
            {
                return currentContext as TRuleType;
            }

            currentContext = currentContext.Parent;
        }

        return null;
    }

    private ParserRuleContext GetParserRuleParent(RuleContext context)
    {
        RuleContext currentContext = context;

        while (currentContext != null)
        {
            if (currentContext is ParserRuleContext parserRuleContext)
            {
                return currentContext as ParserRuleContext;
            }

            currentContext = currentContext.Parent;
        }

        return null;
    }
}
