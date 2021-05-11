using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Shared.Extensions;
using static System.FormattableString;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace FormatInvariantAnalyzer
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(FormatInvariantAnalyzerCodeFixProvider)), Shared]
    public class FormatInvariantAnalyzerCodeFixProvider : CodeFixProvider
    {
        public sealed override ImmutableArray<string> FixableDiagnosticIds
        {
            get { return ImmutableArray.Create(FormatInvariantAnalyzer.DiagnosticId); }
        }

        public sealed override FixAllProvider GetFixAllProvider()
        {
            // See https://github.com/dotnet/roslyn/blob/master/docs/analyzers/FixAllProvider.md for more information on Fix All Providers
            return WellKnownFixAllProviders.BatchFixer;
        }

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            SyntaxNode root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

            Diagnostic diagnostic = context.Diagnostics.First();
            Microsoft.CodeAnalysis.Text.TextSpan diagnosticSpan = diagnostic.Location.SourceSpan;

            // Find the type declaration identified by the diagnostic.
            InvocationExpressionSyntax declaration = root.FindToken(diagnosticSpan.Start).Parent.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().First();

            // Register a code action that will invoke the fix.
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: CodeFixResources.CodeFixTitle,
                    createChangedDocument: c => ReplaceWithFormattableStringInvariant(context.Document, declaration, c),
                    equivalenceKey: nameof(CodeFixResources.CodeFixTitle)),
                diagnostic);
        }

        private CompilationUnitSyntax UpdateUsingDirectives(CompilationUnitSyntax root)
        {
            // Add using static System.FormattableString;
            if (root?.Usings.Any(u => u.Name.ToString() == "System.FormattableString") == false)
            {
                QualifiedNameSyntax name = QualifiedName(IdentifierName("System"), IdentifierName("FormattableString"));
                UsingDirectiveSyntax usingStatement = UsingDirective(SyntaxFactory.Token(SyntaxKind.StaticKeyword), null, name);
                root = root.AddUsings(usingStatement);
            }

            return root;
        }

        private async Task<Document> ReplaceWithFormattableStringInvariant(Document document, InvocationExpressionSyntax invocationExpression, CancellationToken cancellationToken)
        {
            ArgumentSyntax firstArgument = invocationExpression.ArgumentList.Arguments.FirstOrDefault();
            var stringLiteralExpression = invocationExpression.ArgumentList.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
            string stringArgument = stringLiteralExpression?.Token.Text;
            var newArgs = new List<ArgumentSyntax>();

            var stringInterpolationPartsRegex = new Regex(@"(\{[0-9]+\})");
            MatchCollection matches = stringInterpolationPartsRegex.Matches(stringArgument);

            var interpolatedStringContents = new List<InterpolatedStringContentSyntax>();

            string partialStringArgument = GetTextWithoutQuotes(stringArgument);
            for (int i = 0; i < matches.Count; i++)
            {
                Match interpolationMatch = Regex.Match(partialStringArgument, $@"(?<innerString>.*(?=(?<interpolation>\{{{i}\}})))");
                string innerString = interpolationMatch.Groups["innerString"].Value;
                interpolatedStringContents.Add(InterpolatedStringText(Token(default(SyntaxTriviaList), SyntaxKind.InterpolatedStringTextToken, innerString, innerString, default(SyntaxTriviaList))));
                interpolatedStringContents.Add(Interpolation(invocationExpression.ArgumentList.Arguments[i + 1].Expression as IdentifierNameSyntax));
                partialStringArgument = partialStringArgument.Substring(interpolationMatch.Groups["interpolation"].Index + interpolationMatch.Groups["interpolation"].Length);
            }

            if (partialStringArgument.Length > 0)
            {
                interpolatedStringContents.Add(InterpolatedStringText(Token(default(SyntaxTriviaList), SyntaxKind.InterpolatedStringTextToken, partialStringArgument, partialStringArgument, default(SyntaxTriviaList))));
            }

            InterpolatedStringExpressionSyntax interpolatedStringArgument = InterpolatedStringExpression(
                Token(SyntaxKind.InterpolatedStringStartToken),
                List(interpolatedStringContents),
                Token(SyntaxKind.InterpolatedStringEndToken));

            newArgs.Add(Argument(interpolatedStringArgument));
            ArgumentListSyntax argumentList = ArgumentList(SeparatedList(newArgs));

            ArgumentListSyntax formattedArgumentList = argumentList.WithAdditionalAnnotations(Formatter.Annotation);

            SyntaxNode oldRoot = await document.GetSyntaxRootAsync();

            InvocationExpressionSyntax newInvocationExpression = InvocationExpression(IdentifierName(nameof(Invariant)), formattedArgumentList);
            SyntaxNode newRoot = oldRoot.ReplaceNode(invocationExpression, newInvocationExpression);

            CompilationUnitSyntax rootWithAddedUsings = UpdateUsingDirectives((CompilationUnitSyntax)newRoot);
            return document.WithSyntaxRoot(rootWithAddedUsings);
        }
        protected string GetTextWithoutQuotes(string text)
            => text.Substring("'".Length, text.Length - "''".Length);
    }
}
