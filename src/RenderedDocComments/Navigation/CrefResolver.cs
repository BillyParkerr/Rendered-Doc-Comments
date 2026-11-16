using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RenderedDocComments.Navigation
{
    /// <summary>Finds the symbol a doc comment's cref points at, the same way the compiler binds it.</summary>
    internal static class CrefResolver
    {
        private static readonly SymbolDisplayFormat MemberFormat = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                | SymbolDisplayMemberOptions.IncludeType
                | SymbolDisplayMemberOptions.IncludeContainingType
                | SymbolDisplayMemberOptions.IncludeRef,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType
                | SymbolDisplayParameterOptions.IncludeName
                | SymbolDisplayParameterOptions.IncludeParamsRefOut
                | SymbolDisplayParameterOptions.IncludeDefaultValue,
            propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

        /// <param name="commentSpan">The doc comment's span in the document's text.</param>
        public static async Task<ISymbol> ResolveAsync(Document document, TextSpan commentSpan, string cref, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root == null || model == null)
            {
                return null;
            }

            // The comment is structured trivia on the declaration below it. Find the cref
            // attribute with the same text and let the semantic model bind it.
            var comment = root.DescendantTrivia(commentSpan)
                .Select(t => t.GetStructure())
                .OfType<DocumentationCommentTriviaSyntax>()
                .FirstOrDefault();

            if (comment != null)
            {
                string wanted = Normalize(cref);
                foreach (var attribute in comment.DescendantNodes().OfType<XmlCrefAttributeSyntax>())
                {
                    if (Normalize(attribute.Cref.ToString()) != wanted) continue;

                    var info = model.GetSymbolInfo(attribute.Cref, cancellationToken);
                    var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                    if (symbol != null) return symbol;
                }
            }

            // Documentation IDs ("T:System.Threading.Tasks.Task") aren't bound by the compiler.
            if (cref.Length > 2 && cref[1] == ':')
            {
                return DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, model.Compilation);
            }

            return null;
        }

        /// <summary>Signature for a link's tooltip, e.g. "class Contoso.Resilience.RetryPolicy".</summary>
        public static string Describe(ISymbol symbol)
        {
            if (symbol is INamedTypeSymbol type)
            {
                return TypeKeyword(type) + " " + type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            }

            return symbol.ToDisplayString(MemberFormat);
        }

        private static string TypeKeyword(INamedTypeSymbol type)
        {
            switch (type.TypeKind)
            {
                case TypeKind.Interface: return "interface";
                case TypeKind.Enum: return "enum";
                case TypeKind.Delegate: return "delegate";
                case TypeKind.Struct: return type.IsRecord ? "record struct" : "struct";
                default: return type.IsRecord ? "record" : "class";
            }
        }

        private static string Normalize(string cref) =>
            new string(WebUtility.HtmlDecode(cref).Where(c => !char.IsWhiteSpace(c)).ToArray());
    }
}
