// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Utilities;
using Microsoft.CodeAnalysis.Editor.CSharp.Formatting.Indentation;
using Microsoft.CodeAnalysis.Editor.Implementation.Formatting.Indentation;
using Microsoft.CodeAnalysis.Editor.Shared.Options;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Formatting.Rules;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.CSharp.Formatting
{
    [ExportLanguageService(typeof(IEditorFormattingService), LanguageNames.CSharp), Shared]
    internal partial class CSharpEditorFormattingService : IEditorFormattingService
    {
        // All the characters that might potentially trigger formatting when typed
        private readonly char[] _supportedChars = ";{}#nte:)".ToCharArray();

        public CSharpEditorFormattingService()
        {
        }

        public bool SupportsFormatDocument { get { return true; } }

        public bool SupportsFormatOnPaste { get { return true; } }

        public bool SupportsFormatSelection { get { return true; } }

        public bool SupportsFormatOnReturn { get { return true; } }

        public bool SupportsFormattingOnTypedCharacter(Document document, char ch)
        {
            var options = document.GetOptionsAsync(CancellationToken.None).WaitAndGetResult(CancellationToken.None);
            var smartIndentOn = options.GetOption(FormattingOptions.SmartIndent) == FormattingOptions.IndentStyle.Smart;

            if ((ch == '}' && !options.GetOption(FeatureOnOffOptions.AutoFormattingOnCloseBrace) && !smartIndentOn) ||
                (ch == ';' && !options.GetOption(FeatureOnOffOptions.AutoFormattingOnSemicolon)))
            {
                return false;
            }

            // don't auto format after these keys if smart indenting is not on.
            if ((ch == '#' || ch == 'n') && !smartIndentOn)
            {
                return false;
            }

            return _supportedChars.Contains(ch);
        }

        public async Task<IList<TextChange>> GetFormattingChangesAsync(Document document, TextSpan? textSpan, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var options = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);

            var span = textSpan.HasValue ? textSpan.Value : new TextSpan(0, root.FullSpan.Length);
            var formattingSpan = CommonFormattingHelpers.GetFormattingSpan(root, span);
            return Formatter.GetFormattedTextChanges(root, new TextSpan[] { formattingSpan }, document.Project.Solution.Workspace, options, cancellationToken);
        }

        public async Task<IList<TextChange>> GetFormattingChangesOnPasteAsync(Document document, TextSpan textSpan, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var options = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);

            var formattingSpan = CommonFormattingHelpers.GetFormattingSpan(root, textSpan);
            var service = document.GetLanguageService<ISyntaxFormattingService>();
            if (service == null)
            {
                return SpecializedCollections.EmptyList<TextChange>();
            }

            var rules = new List<IFormattingRule>() { new PasteFormattingRule() };
            rules.AddRange(service.GetDefaultFormattingRules());

            return Formatter.GetFormattedTextChanges(root, SpecializedCollections.SingletonEnumerable(formattingSpan), document.Project.Solution.Workspace, options, rules, cancellationToken);
        }

        private IEnumerable<IFormattingRule> GetFormattingRules(Document document, int position)
        {
            var workspace = document.Project.Solution.Workspace;
            var formattingRuleFactory = workspace.Services.GetService<IHostDependentFormattingRuleFactoryService>();
            return formattingRuleFactory.CreateRule(document, position).Concat(Formatter.GetDefaultFormattingRules(document));
        }

        public async Task<IList<TextChange>> GetFormattingChangesOnReturnAsync(Document document, int caretPosition, CancellationToken cancellationToken)
        {
            var formattingRules = this.GetFormattingRules(document, caretPosition);

            // first, find the token user just typed.
            SyntaxToken token = await GetTokenBeforeTheCaretAsync(document, caretPosition, cancellationToken).ConfigureAwait(false);

            if (token.IsMissing)
            {
                return null;
            }

            string text = null;
            if (IsInvalidToken(token, ref text))
            {
                return null;
            }

            // Check to see if the token is ')' and also the parent is a using statement. If not, bail
            if (TokenShouldNotFormatOnReturn(token))
            {
                return null;
            }

            // if formatting range fails, do format token one at least
            var changes = await FormatRangeAsync(document, token, formattingRules, cancellationToken).ConfigureAwait(false);
            if (changes.Count > 0)
            {
                return changes;
            }

            // if we can't, do normal smart indentation
            return await FormatTokenAsync(document, token, formattingRules, cancellationToken).ConfigureAwait(false);
        }

        private static bool TokenShouldNotFormatOnReturn(SyntaxToken token)
        {
            return !token.IsKind(SyntaxKind.CloseParenToken) || !token.Parent.IsKind(SyntaxKind.UsingStatement);
        }

        private static async Task<bool> TokenShouldNotFormatOnTypeCharAsync(
            SyntaxToken token, CancellationToken cancellationToken)
        {
            if (token.IsKind(SyntaxKind.CloseParenToken) && !token.Parent.IsKind(SyntaxKind.UsingStatement))
            {
                return true;
            }

            if (token.IsKind(SyntaxKind.ColonToken) && !(token.Parent.IsKind(SyntaxKind.LabeledStatement) || token.Parent is SwitchLabelSyntax))
            {
                return true;
            }

            // Only format an { if it is the first token on a line.  We don't want to 
            // mess with it if it's inside a line.
            if (token.IsKind(SyntaxKind.OpenBraceToken))
            {
                var text = await token.SyntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (!token.IsFirstTokenOnLine(text))
                {
                    return true;
                }
            }

            return false;
        }

        public async Task<IList<TextChange>> GetFormattingChangesAsync(Document document, char typedChar, int caretPosition, CancellationToken cancellationToken)
        {
            var formattingRules = this.GetFormattingRules(document, caretPosition);

            // first, find the token user just typed.
            var token = await GetTokenBeforeTheCaretAsync(document, caretPosition, cancellationToken).ConfigureAwait(false);

            if (token.IsMissing ||
                !ValidSingleOrMultiCharactersTokenKind(typedChar, token.Kind()) ||
                token.IsKind(SyntaxKind.EndOfFileToken, SyntaxKind.None))
            {
                return null;
            }

            var service = document.GetLanguageService<ISyntaxFactsService>();
            if (service != null && service.IsInNonUserCode(token.SyntaxTree, caretPosition, cancellationToken))
            {
                return null;
            }

            // Check to see if any of the below. If not, bail.
            // case 1: The token is ')' and the parent is an using statement.
            // case 2: The token is ':' and the parent is either labelled statement or case switch or default switch
            var shouldNotFormat = await TokenShouldNotFormatOnTypeCharAsync(token, cancellationToken).ConfigureAwait(false);
            if (shouldNotFormat)
            {
                return null;
            }

            var options = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);

            // don't attempt to format on close brace if autoformat on close brace feature is off, instead just smart indent
            bool smartIndentOnly =
                token.IsKind(SyntaxKind.CloseBraceToken) &&
                !options.GetOption(FeatureOnOffOptions.AutoFormattingOnCloseBrace);

            if (!smartIndentOnly)
            {
                // if formatting range fails, do format token one at least
                var changes = await FormatRangeAsync(document, token, formattingRules, cancellationToken).ConfigureAwait(false);
                if (changes.Count > 0)
                {
                    return changes;
                }
            }

            // if we can't, do normal smart indentation
            return await FormatTokenAsync(document, token, formattingRules, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<SyntaxToken> GetTokenBeforeTheCaretAsync(Document document, int caretPosition, CancellationToken cancellationToken)
        {
            var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);

            var position = Math.Max(0, caretPosition - 1);
            var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            var token = root.FindToken(position, findInsideTrivia: true);
            return token;
        }

        private async Task<IList<TextChange>> FormatTokenAsync(Document document, SyntaxToken token, IEnumerable<IFormattingRule> formattingRules, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var options = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);
            var formatter = CreateSmartTokenFormatter(options, formattingRules, root);
            var changes = await formatter.FormatTokenAsync(document.Project.Solution.Workspace, token, cancellationToken).ConfigureAwait(false);
            return changes;
        }

        private ISmartTokenFormatter CreateSmartTokenFormatter(OptionSet optionSet, IEnumerable<IFormattingRule> formattingRules, SyntaxNode root)
        {
            return new SmartTokenFormatter(optionSet, formattingRules, (CompilationUnitSyntax)root);
        }

        private async Task<IList<TextChange>> FormatRangeAsync(
            Document document, SyntaxToken endToken, IEnumerable<IFormattingRule> formattingRules,
            CancellationToken cancellationToken)
        {
            if (!IsEndToken(endToken))
            {
                return SpecializedCollections.EmptyList<TextChange>();
            }

            var tokenRange = FormattingRangeHelper.FindAppropriateRange(endToken);
            if (tokenRange == null || tokenRange.Value.Item1.Equals(tokenRange.Value.Item2))
            {
                return SpecializedCollections.EmptyList<TextChange>();
            }

            if (IsInvalidTokenKind(tokenRange.Value.Item1) || IsInvalidTokenKind(tokenRange.Value.Item2))
            {
                return SpecializedCollections.EmptyList<TextChange>();
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var options = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);

            var formatter = new SmartTokenFormatter(options, formattingRules, (CompilationUnitSyntax)root);

            var changes = formatter.FormatRange(document.Project.Solution.Workspace, tokenRange.Value.Item1, tokenRange.Value.Item2, cancellationToken);
            return changes;
        }

        private bool IsEndToken(SyntaxToken endToken)
        {
            if (endToken.IsKind(SyntaxKind.OpenBraceToken))
            {
                return false;
            }

            return true;
        }

        private bool ValidSingleOrMultiCharactersTokenKind(char typedChar, SyntaxKind kind)
        {
            // We'll autoformat on n, t, e, only if they are the last character of the below
            // keywords.  
            switch (typedChar)
            {
                case ('n'):
                    return kind == SyntaxKind.RegionKeyword || kind == SyntaxKind.EndRegionKeyword;
                case ('t'):
                    return kind == SyntaxKind.SelectKeyword;
                case ('e'):
                    return kind == SyntaxKind.WhereKeyword;
                default:
                    return true;
            }
        }

        private bool IsInvalidToken(char typedChar, SyntaxToken token)
        {
            string text = null;
            if (IsInvalidToken(token, ref text))
            {
                return true;
            }

            return text[0] != typedChar;
        }

        private bool IsInvalidToken(SyntaxToken token, ref string text)
        {
            if (IsInvalidTokenKind(token))
            {
                return true;
            }

            text = token.ToString();
            if (text.Length != 1)
            {
                return true;
            }

            return false;
        }

        private bool IsInvalidTokenKind(SyntaxToken token)
        {
            // invalid token to be formatted
            return token.IsKind(SyntaxKind.None) ||
                   token.IsKind(SyntaxKind.EndOfDirectiveToken) ||
                   token.IsKind(SyntaxKind.EndOfFileToken);
        }
    }
}
