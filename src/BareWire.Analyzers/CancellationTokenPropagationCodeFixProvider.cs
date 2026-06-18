using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace BareWire.Analyzers;

/// <summary>
/// Code fix for BW1001: appends <c>CancellationToken cancellationToken = default</c> to the
/// method's parameter list and adds the <c>using System.Threading;</c> directive when absent.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(CancellationTokenPropagationCodeFixProvider))]
[Shared]
public sealed class CancellationTokenPropagationCodeFixProvider : CodeFixProvider
{
    private const string SystemThreadingNamespace = "System.Threading";

    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DiagnosticDescriptors.CancellationTokenPropagationId);

    /// <inheritdoc/>
    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc/>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
            return;

        var diagnostic = context.Diagnostics[0];
        var diagnosticSpan = diagnostic.Location.SourceSpan;

        // Find the MethodDeclarationSyntax that covers the diagnostic location
        var methodDecl = root
            .FindToken(diagnosticSpan.Start)
            .Parent?
            .AncestorsAndSelf()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();

        if (methodDecl is null)
            return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Dodaj parametr CancellationToken",
                createChangedDocument: ct => AddCancellationTokenParameterAsync(context.Document, methodDecl, ct),
                equivalenceKey: nameof(CancellationTokenPropagationCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> AddCancellationTokenParameterAsync(
        Document document,
        MethodDeclarationSyntax methodDecl,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return document;

        // Build: CancellationToken cancellationToken = default
        // Use the short name — we'll add the using directive below if needed.
        var cancellationTokenType = SyntaxFactory
            .IdentifierName("CancellationToken")
            .WithTrailingTrivia(SyntaxFactory.Space);

        var newParameter = SyntaxFactory.Parameter(
                SyntaxFactory.Identifier("cancellationToken"))
            .WithType(cancellationTokenType)
            .WithDefault(
                SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression))
                .WithLeadingTrivia(SyntaxFactory.Space));

        var newParamList = methodDecl.ParameterList.AddParameters(newParameter);
        var newRoot = root.ReplaceNode(methodDecl.ParameterList, newParamList);

        // Add using System.Threading; if not already present
        if (newRoot is CompilationUnitSyntax compilationUnit &&
            !HasUsingDirective(compilationUnit, SystemThreadingNamespace))
        {
            var newUsing = SyntaxFactory
                .UsingDirective(SyntaxFactory.ParseName(SystemThreadingNamespace))
                .WithTrailingTrivia(SyntaxFactory.ElasticEndOfLine("\n"));

            // Insert the using in sorted order
            var existingUsings = compilationUnit.Usings;
            var insertIndex = FindInsertionIndex(existingUsings, SystemThreadingNamespace);

            UsingDirectiveSyntax insertBefore;
            if (insertIndex < existingUsings.Count)
            {
                insertBefore = existingUsings[insertIndex];
                newRoot = compilationUnit.InsertNodesBefore(insertBefore, new[] { newUsing });
            }
            else
            {
                newRoot = compilationUnit.AddUsings(newUsing);
            }
        }

        return document.WithSyntaxRoot(newRoot);
    }

    private static bool HasUsingDirective(CompilationUnitSyntax root, string namespaceName)
    {
        foreach (var u in root.Usings)
        {
            if (u.Name?.ToString() == namespaceName)
                return true;
        }
        return false;
    }

    private static int FindInsertionIndex(
        SyntaxList<UsingDirectiveSyntax> usings,
        string namespaceName)
    {
        for (int i = 0; i < usings.Count; i++)
        {
            var existingName = usings[i].Name?.ToString() ?? string.Empty;
            if (string.Compare(existingName, namespaceName, StringComparison.Ordinal) > 0)
                return i;
        }
        return usings.Count;
    }
}
