﻿using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Yarn.Compiler;

namespace YarnLanguageServer.Handlers
{
    internal class CompletionHandler : ICompletionHandler
    {
        private Workspace workspace;

        public CompletionHandler(Workspace workspace)
        {
            this.workspace = workspace;
        }

        public Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
        {
            if (workspace.YarnFiles.TryGetValue(request.TextDocument.Uri.ToUri(), out var yarnFile))
            {
                var index = yarnFile.GetRawToken(request.Position);
                if (!index.HasValue)
                {
                    return Task.FromResult<CompletionList>(null);
                }

                var indexToken = yarnFile.Tokens[index.Value];

                // things fall apart with opening braces for some reason
                if (indexToken.Type == YarnSpinnerLexer.COMMAND_START || indexToken.Type == YarnSpinnerLexer.LPAREN || indexToken.Type == YarnSpinnerLexer.EXPRESSION_START)
                {
                    // if we are at the right edge of <<, then use what ever the next token is to get completions
                    if (PositionHelper.GetRange(yarnFile.LineStarts, indexToken).End == request.Position)
                    {
                        index++;
                        indexToken = yarnFile.Tokens[index.Value];
                    }
                    else
                    {
                        // don't run completions for left and middle of <<
                        return Task.FromResult<CompletionList>(null);
                    }
                }

                var indexTokenRange = PositionHelper.GetRange(yarnFile.LineStarts, indexToken);
                if (indexToken.Type == YarnSpinnerLexer.COMMAND_END || indexToken.Type == YarnSpinnerLexer.RPAREN || indexToken.Type == YarnSpinnerLexer.EXPRESSION_END)
                {
                    indexTokenRange = indexTokenRange.CollapseToStart(); // don't replace closing braces
                }

                var candidates = yarnFile.CodeCompletionCore.CollectCandidates(index.Value, null);
                var results = new List<CompletionItem>();
                var vocabulary = yarnFile.Parser.Vocabulary;
                foreach (var token in candidates.Tokens)
                {
                    var tokenname = vocabulary.GetSymbolicName(token.Key);

                    if (tokenname == null) { continue; } // unrecognized token

                    var label = UserFriendlyTokenText.GetValueOrDefault(tokenname, tokenname);
                    var text = TokenSnippets.GetValueOrDefault(tokenname, label);

                    results.Add(new CompletionItem
                    {
                        Label = label,
                        Kind = CompletionItemKind.Keyword,
                        Documentation = $"{label} keyword",
                        InsertText = text,
                        TextEdit = new TextEditOrInsertReplaceEdit(new TextEdit { NewText = text, Range = indexTokenRange }),
                        InsertTextFormat = (text == label) ? InsertTextFormat.PlainText : InsertTextFormat.Snippet,
                    });
                }

                foreach (var rule in candidates.Rules)
                {
                    switch (rule.Key)
                    {
                        case YarnSpinnerParser.RULE_jump_statement:

                            // We could be completing a jump rule. We don't
                            // currently know if we're at the start of the jump
                            // rule (i.e. the 'jump' keyword exists), or if
                            // we're in the middle of one (the 'jump' keyword
                            // DOES exist).
                            //
                            // Check the previous token, and if it's not
                            // COMMAND_JUMP, offer that as our completion.
                            var previousToken = yarnFile.Tokens[index > 0 ? index ?? -1 : 0];

                            if (previousToken.Type != YarnSpinnerLexer.COMMAND_JUMP)
                            {
                                // The previous token was not 'jump'. Offer to
                                // complete with a full jump statement.
                                const string jumpSnippet = "jump ${1:node}";
                                results.Add(
                                    new CompletionItem
                                    {
                                        Label = "jump",
                                        Documentation = $"jump statement",
                                        Kind = CompletionItemKind.Keyword,
                                        InsertText = jumpSnippet,
                                        TextEdit = new TextEditOrInsertReplaceEdit(new TextEdit { NewText = jumpSnippet, Range = indexTokenRange }),
                                        InsertTextFormat = InsertTextFormat.Snippet,
                                    }
                                );
                            }
                            else
                            {
                                // We're in the middle of a jump statement.
                                // Offer the list of node names we could jump
                                // to.
                                results.AddRange(
                                    workspace.GetNodeTitles().Select((nodeTitle, _) =>
                                    new CompletionItem
                                    {
                                        Label = nodeTitle.title,
                                        Kind = CompletionItemKind.Function,
                                        Detail = System.IO.Path.GetFileName(nodeTitle.uri.AbsolutePath),
                                        TextEdit = new TextEditOrInsertReplaceEdit(new TextEdit { NewText = nodeTitle.title, Range = indexTokenRange.CollapseToEnd() }),
                                    })
                                );
                            }

                            break;

                        case YarnSpinnerParser.RULE_function_call:

                            results.AddRange(
                                workspace.GetFunctions().Select(command =>
                                new CompletionItem
                                {
                                    Label = command.YarnName,
                                    Kind = CompletionItemKind.Function,
                                    Documentation = $"{command.Signature}\n{command.Documentation}",
                                    InsertTextFormat = InsertTextFormat.Snippet,
                                    TextEdit = new TextEditOrInsertReplaceEdit(new TextEdit { NewText = $"{command.YarnName}($0)", Range = indexTokenRange }),
                                    Command = Utils.TriggerParameterHintsCommand,
                                })
                            );
                            break;

                        case YarnSpinnerParser.RULE_command_statement:
                            results.AddRange(
                                workspace.GetCommands().Select(command =>
                                new CompletionItem
                                {
                                    Label = command.YarnName,
                                    Kind = CompletionItemKind.Function,
                                    Detail = command.DefinitionFile == null || command.IsBuiltIn ? null : System.IO.Path.GetFileName(command.DefinitionFile.AbsolutePath),
                                    Documentation = $"{command.Signature}\n{command.Documentation}",
                                    TextEdit = new TextEditOrInsertReplaceEdit(new TextEdit { NewText = command.YarnName, Range = indexTokenRange }),
                                })
                            );
                            break;

                        case YarnSpinnerParser.RULE_variable:
                            var variableResults = Enumerable.Empty<CompletionItem>();

                            variableResults = workspace.GetVariables().Select(variableDeclaration =>
                                new CompletionItem
                                {
                                    Label = variableDeclaration.Name,
                                    Kind = CompletionItemKind.Variable,
                                    Documentation = variableDeclaration.Description.OrDefault($"(variable) {variableDeclaration.Name}"),
                                    TextEdit = new TextEditOrInsertReplaceEdit(
                                        new TextEdit
                                        {
                                            NewText = variableDeclaration.Name,
                                            Range = indexTokenRange,
                                        }
                                    ),
                                }
                            );
                            results.AddRange(variableResults);

                            if (!workspace.Configuration.OnlySuggestDeclaredVariables)
                            {
                                results.AddRange(
                                workspace.GetVariableNames()
                                    .Where(variableName => !variableResults.Any(vr => vr.Label == variableName)) // Filter out any declared variables
                                    .Select(variableName =>
                                        new CompletionItem
                                        {
                                            Label = variableName,
                                            Kind = CompletionItemKind.Variable,
                                            Documentation = string.Empty,
                                            TextEdit = new TextEditOrInsertReplaceEdit(new TextEdit { NewText = variableName, Range = indexTokenRange }),
                                        }));
                            }

                            break;
                    }
                }

                return Task.FromResult(new CompletionList(results));
            }

            return Task.FromResult<CompletionList>(null);
        }

        public static readonly HashSet<int> PreferedRules = new HashSet<int>
        {
            YarnSpinnerParser.RULE_command_statement,
            YarnSpinnerParser.RULE_variable,
            YarnSpinnerParser.RULE_function_call,
            YarnSpinnerParser.RULE_function_call,
            YarnSpinnerParser.RULE_jump_statement,

            // YarnSpinnerLexer.FUNC_ID,
            // YarnSpinnerLexer.COMMAND_NAME,
            // YarnSpinnerLexer.ID,
            // YarnSpinnerLexer.VAR_ID
        };

        public static readonly HashSet<int> IgnoredTokens = new HashSet<int>
        {
            YarnSpinnerLexer.OPERATOR_ASSIGNMENT,
            YarnSpinnerLexer.OPERATOR_MATHS_ADDITION,
            YarnSpinnerLexer.OPERATOR_MATHS_ADDITION_EQUALS,
            YarnSpinnerLexer.OPERATOR_MATHS_DIVISION,
            YarnSpinnerLexer.OPERATOR_MATHS_DIVISION_EQUALS,
            YarnSpinnerLexer.OPERATOR_MATHS_SUBTRACTION,
            YarnSpinnerLexer.OPERATOR_MATHS_SUBTRACTION_EQUALS,
            YarnSpinnerLexer.OPERATOR_MATHS_MULTIPLICATION,
            YarnSpinnerLexer.OPERATOR_MATHS_MULTIPLICATION_EQUALS,
            YarnSpinnerLexer.OPERATOR_MATHS_MODULUS,
            YarnSpinnerLexer.OPERATOR_MATHS_MODULUS_EQUALS,
            YarnSpinnerLexer.OPERATOR_LOGICAL_NOT,
            YarnSpinnerLexer.OPERATOR_LOGICAL_NOT_EQUALS,
            YarnSpinnerLexer.LPAREN,
            YarnSpinnerLexer.RPAREN,
            YarnSpinnerLexer.SHORTCUT_ARROW,
            YarnSpinnerLexer.TEXT,
            YarnSpinnerLexer.EXPRESSION_START,
            YarnSpinnerLexer.HASHTAG,
            YarnSpinnerLexer.COMMAND_TEXT,
            YarnSpinnerLexer.COMMAND_TEXT_END,
            YarnSpinnerLexer.COMMAND_EXPRESSION_START,
            YarnSpinnerLexer.INDENT,
            YarnSpinnerLexer.DEDENT,
            YarnSpinnerLexer.WHITESPACE,
            YarnSpinnerLexer.NUMBER,
            YarnSpinnerLexer.STRING,
            YarnSpinnerLexer.BODY_END,
            YarnSpinnerLexer.COMMAND_START,
            YarnSpinnerLexer.COMMAND_END,
            YarnSpinnerLexer.FUNC_ID, // This and var id ideally taken care of with rules
            YarnSpinnerLexer.VAR_ID,
        };

        public static readonly Dictionary<string, string> UserFriendlyTokenText = new Dictionary<string, string>
        {
            { "COMMAND_IF", "if" }, { "COMMAND_ELSEIF", "elseif" }, { "COMMAND_ELSE", "else" }, { "COMMAND_SET", "set" },
            { "COMMAND_ENDIF", "endif" }, { "COMMAND_CALL", "call" }, { "COMMAND_DECLARE", "declare" }, { "COMMAND_JUMP", "jump " },
            { "KEYWORD_FALSE", "false" }, { "KEYWORD_TRUE", "true" }, { "KEYWORD_NULL", "null" },
        };

        public static readonly Dictionary<string, string> TokenSnippets = new Dictionary<string, string>
        {
            { "COMMAND_SET", "set \\$$1 to ${2:value}" }, { "COMMAND_DECLARE", "declare \\$$1 to ${2:value}" },
        };

        public CompletionRegistrationOptions GetRegistrationOptions(CompletionCapability capability, ClientCapabilities clientCapabilities)
        {
            return new CompletionRegistrationOptions
            {
                DocumentSelector = Utils.YarnDocumentSelector,
                TriggerCharacters = new Container<string>(new List<string> { "$", "<" }),
                AllCommitCharacters = new Container<string>(new List<string> { " " }), // maybe >> or }
            };
        }
    }
}