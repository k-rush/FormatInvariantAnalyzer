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
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static System.FormattableString;

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
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

            var diagnostic = context.Diagnostics.First();
            var diagnosticSpan = diagnostic.Location.SourceSpan;

            // Find the type declaration identified by the diagnostic.
            var declaration = root.FindToken(diagnosticSpan.Start).Parent.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().First();

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
                var name = QualifiedName(IdentifierName("System"), IdentifierName("FormattableString"));
                var usingStatement = UsingDirective(SyntaxFactory.Token(SyntaxKind.StaticKeyword), null, name);
                root = root.AddUsings(usingStatement);
            }

            return root;
        }

        private async Task<Document> ReplaceWithFormattableStringInvariant(Document document, InvocationExpressionSyntax invocationExpression, CancellationToken cancellationToken)
        {
            var firstArgument = invocationExpression.ArgumentList.Arguments.FirstOrDefault();
            var stringLiteralExpression = invocationExpression.ArgumentList.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
            string stringArgument = stringLiteralExpression?.Token.Text;
            var newArgs = new List<ArgumentSyntax>();

            Regex stringInterpolationPartsRegex = new Regex(@"(\{[0-9]+\})");
            var matches = stringInterpolationPartsRegex.Matches(stringArgument);

            List<InterpolatedStringContentSyntax> interpolatedStringContents = new List<InterpolatedStringContentSyntax>();

            string partialStringArgument = GetTextWithoutQuotes(stringArgument);
            for (int i = 0; i < matches.Count; i++)
            {
                var interpolationMatch = Regex.Match(partialStringArgument, $@"(?<innerString>.*(?=(?<interpolation>\{{{i}\}})))");
                var innerString = interpolationMatch.Groups["innerString"].Value;
                interpolatedStringContents.Add(SyntaxFactory.InterpolatedStringText(SyntaxFactory.Token(default(SyntaxTriviaList), SyntaxKind.InterpolatedStringTextToken, innerString, innerString, default(SyntaxTriviaList))));
                interpolatedStringContents.Add(SyntaxFactory.Interpolation(invocationExpression.ArgumentList.Arguments[i + 1].Expression as IdentifierNameSyntax));
                partialStringArgument = partialStringArgument.Substring(interpolationMatch.Groups["interpolation"].Index + interpolationMatch.Groups["interpolation"].Length);
            }

            if (partialStringArgument.Length > 0)
            {
                interpolatedStringContents.Add(SyntaxFactory.InterpolatedStringText(SyntaxFactory.Token(default(SyntaxTriviaList), SyntaxKind.InterpolatedStringTextToken, partialStringArgument, partialStringArgument, default(SyntaxTriviaList))));
            }

            var interpolatedStringArgument = SyntaxFactory.InterpolatedStringExpression(SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken), List(interpolatedStringContents), SyntaxFactory.Token(SyntaxKind.InterpolatedStringEndToken));
            newArgs.Add(Argument(interpolatedStringArgument));
            var argumentList = SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList<ArgumentSyntax>(newArgs));

            var formattedArgumentList = argumentList.WithAdditionalAnnotations(Formatter.Annotation);

            var oldRoot = await document.GetSyntaxRootAsync();

            var newInvocationExpression = SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName(nameof(Invariant)), formattedArgumentList);
            var newRoot = oldRoot.ReplaceNode(invocationExpression, newInvocationExpression);

            var rootWithAddedUsings = UpdateUsingDirectives((CompilationUnitSyntax)newRoot);
            return document.WithSyntaxRoot(rootWithAddedUsings);
        }
        protected string GetTextWithoutQuotes(string text)
            => text.Substring("'".Length, text.Length - "''".Length);
    }
}
