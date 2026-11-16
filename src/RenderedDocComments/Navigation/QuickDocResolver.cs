using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RenderedDocComments.Rendering;

namespace RenderedDocComments.Navigation
{
    /// <summary>A symbol found for the quick documentation popup, and what to show for it.</summary>
    internal sealed class QuickDocTarget
    {
        public QuickDocTarget(ISymbol symbol, TextSpan span, QuickDocContent content)
        {
            Symbol = symbol;
            Span = span;
            Content = content;
        }

        /// <summary>The symbol shown, for Go To Definition.</summary>
        public ISymbol Symbol { get; }

        /// <summary>Its name in the document, where the popup goes (empty for a symbol reached through a link).</summary>
        public TextSpan Span { get; }

        public QuickDocContent Content { get; }
    }

    /// <summary>
    /// Finds the symbol at a position (a name in code, or a cref in a doc comment) and gathers
    /// what the quick documentation popup shows for it: signature, location and documentation.
    /// Framework symbols' docs come from .NET's documentation files, as for Quick Info.
    /// </summary>
    internal static class QuickDocResolver
    {
        private static readonly SymbolDisplayFormat TypeFormat = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
            kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword,
            delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
            parameterOptions: ParameterOptions,
            miscellaneousOptions: Miscellaneous);

        private static readonly SymbolDisplayFormat SpecialTypeFormat =
            TypeFormat.RemoveMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        private static readonly SymbolDisplayFormat MemberFormat = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                | SymbolDisplayMemberOptions.IncludeType
                | SymbolDisplayMemberOptions.IncludeContainingType
                | SymbolDisplayMemberOptions.IncludeModifiers
                | SymbolDisplayMemberOptions.IncludeConstantValue
                | SymbolDisplayMemberOptions.IncludeExplicitInterface
                | SymbolDisplayMemberOptions.IncludeRef,
            extensionMethodStyle: SymbolDisplayExtensionMethodStyle.StaticMethod,
            parameterOptions: ParameterOptions,
            propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
            miscellaneousOptions: Miscellaneous);

        private static readonly SymbolDisplayFormat LocalFormat = new SymbolDisplayFormat(
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
            parameterOptions: ParameterOptions,
            localOptions: SymbolDisplayLocalOptions.IncludeType | SymbolDisplayLocalOptions.IncludeConstantValue | SymbolDisplayLocalOptions.IncludeRef,
            miscellaneousOptions: Miscellaneous);

        /// <summary>Short names for Back links and containers, e.g. "List&lt;T&gt;.Add".</summary>
        private static readonly SymbolDisplayFormat TitleFormat = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
            miscellaneousOptions: Miscellaneous);

        private const SymbolDisplayParameterOptions ParameterOptions =
            SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeDefaultValue
            | SymbolDisplayParameterOptions.IncludeExtensionThis;

        private const SymbolDisplayMiscellaneousOptions Miscellaneous =
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier;

        /// <summary>The symbol named at a position (on or just after a name), or null.</summary>
        public static async Task<QuickDocTarget> AtPositionAsync(Document document, int position, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root == null || model == null)
            {
                return null;
            }

            var token = NameTokenAt(root, position);
            if (token.RawKind == 0)
            {
                return null;
            }

            var symbol = SymbolFor(token, model, cancellationToken);
            return symbol == null ? null : new QuickDocTarget(symbol, token.Span, Describe(symbol, model.Compilation, cancellationToken));
        }

        /// <summary>The symbol with a documentation ID, as crefs appear in compiled docs ("T:System.String").</summary>
        public static QuickDocTarget ForId(string id, Compilation compilation, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(id) || compilation == null)
            {
                return null;
            }

            var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation)
                ?? DocumentationCommentId.GetFirstSymbolForReferenceId(id, compilation);
            return symbol == null ? null : new QuickDocTarget(symbol, default, Describe(symbol, compilation, cancellationToken));
        }

        /// <summary>
        /// The name token at a position: the one the position is in, or else the one just before
        /// it ("Name|(" or "Name|" at the end of a line).
        /// </summary>
        internal static SyntaxToken NameTokenAt(SyntaxNode root, int position)
        {
            if (position < 0 || position > root.FullSpan.End)
            {
                return default;
            }

            var at = root.FindToken(position, findInsideTrivia: true);
            if (IsName(at) && at.Span.Contains(position))
            {
                return at;
            }

            if (position > 0)
            {
                var before = root.FindToken(position - 1, findInsideTrivia: true);
                if (IsName(before) && before.Span.End == position)
                {
                    return before;
                }
            }

            return default;
        }

        private static bool IsName(SyntaxToken token) =>
            token.IsKind(SyntaxKind.IdentifierToken)
            || token.IsKind(SyntaxKind.ThisKeyword)
            || token.IsKind(SyntaxKind.BaseKeyword)
            || (SyntaxFacts.IsPredefinedType(token.Kind()) && token.Parent is PredefinedTypeSyntax);

        private static ISymbol SymbolFor(SyntaxToken token, SemanticModel model, CancellationToken cancellationToken)
        {
            var node = token.Parent;
            if (node == null)
            {
                return null;
            }

            if (token.IsKind(SyntaxKind.ThisKeyword) || token.IsKind(SyntaxKind.BaseKeyword))
            {
                return model.GetTypeInfo(node, cancellationToken).Type;
            }

            // A declaration's own name.
            if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                var declared = model.GetDeclaredSymbol(node, cancellationToken);
                if (declared != null)
                {
                    return declared;
                }
            }

            // In "new Widget(...)" and "[Obsolete(...)]" the name stands for the constructor.
            var name = node;
            while ((name.Parent is QualifiedNameSyntax qualified && qualified.Right == name)
                || (name.Parent is AliasQualifiedNameSyntax alias && alias.Name == name))
            {
                name = name.Parent;
            }

            ISymbol symbol = null;
            if (name.Parent is ObjectCreationExpressionSyntax creation && creation.Type == name)
            {
                symbol = Bind(creation, model, cancellationToken);
            }
            else if (name.Parent is AttributeSyntax attribute && attribute.Name == name)
            {
                symbol = Bind(attribute, model, cancellationToken);
            }

            symbol = symbol ?? Bind(node, model, cancellationToken);

            // A name inside a cref binds as part of the whole cref.
            if (symbol == null)
            {
                var cref = node.AncestorsAndSelf().OfType<CrefSyntax>().LastOrDefault();
                if (cref != null) symbol = Bind(cref, model, cancellationToken);
            }

            return symbol is IAliasSymbol aliasSymbol ? aliasSymbol.Target : symbol;
        }

        private static ISymbol Bind(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
        {
            var info = model.GetSymbolInfo(node, cancellationToken);
            return info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        }

        /// <summary>The popup's content for a symbol.</summary>
        internal static QuickDocContent Describe(ISymbol symbol, Compilation compilation, CancellationToken cancellationToken)
        {
            var content = new QuickDocContent
            {
                Title = symbol.ToDisplayString(TitleFormat),
                Assembly = FromMetadata(symbol) ? symbol.ContainingAssembly?.Name : null,
            };

            switch (symbol)
            {
                case IParameterSymbol parameter:
                    content.Kind = "parameter";
                    content.Signature = Parts(parameter.ToDisplayParts(LocalFormat), parameter);
                    DescribeAsPartOf(content, parameter.ContainingSymbol, "param", parameter.Name, compilation, cancellationToken);
                    break;

                case ITypeParameterSymbol typeParameter:
                    content.Kind = "type parameter";
                    content.Signature = Parts(typeParameter.ToDisplayParts(LocalFormat), typeParameter);
                    DescribeAsPartOf(content, (ISymbol)typeParameter.DeclaringMethod ?? typeParameter.DeclaringType, "typeparam", typeParameter.Name, compilation, cancellationToken);
                    break;

                case ILocalSymbol local:
                    content.Kind = local.IsConst ? "local constant" : "local variable";
                    content.Signature = Parts(local.ToDisplayParts(LocalFormat), local);
                    SetContainerMember(content, local.ContainingSymbol);
                    break;

                case IRangeVariableSymbol _:
                case ILabelSymbol _:
                case IDiscardSymbol _:
                    content.Kind = symbol is IRangeVariableSymbol ? "range variable" : symbol is ILabelSymbol ? "label" : "discard";
                    content.Signature = Parts(symbol.ToDisplayParts(LocalFormat), symbol);
                    break;

                case IMethodSymbol method when method.MethodKind == MethodKind.LocalFunction || method.MethodKind == MethodKind.AnonymousFunction:
                    content.Kind = method.MethodKind == MethodKind.LocalFunction ? "local function" : "lambda";
                    content.Signature = Parts(method.ToDisplayParts(LocalFormat), method);
                    SetContainerMember(content, method.ContainingSymbol);
                    break;

                case INamespaceSymbol ns:
                    content.Signature = new[] { new SignaturePart("namespace", CodeTokenKind.Keyword), new SignaturePart(" ", CodeTokenKind.Plain) }
                        .Concat(Parts(ns.ToDisplayParts(SymbolDisplayFormat.CSharpErrorMessageFormat), ns)).ToList();
                    content.Assembly = null;
                    break;

                case INamedTypeSymbol type:
                    // "class String", not "class string": keyword names are for uses of a type, not the type itself.
                    var format = type.SpecialType == SpecialType.None ? TypeFormat : SpecialTypeFormat;
                    content.Signature = TypeModifiers(type).Concat(Parts(type.ToDisplayParts(format), type)).ToList();
                    content.Container = Namespace(type);
                    SetDocs(content, type, compilation, cancellationToken);
                    break;

                default:
                    content.Signature = Parts(symbol.ToDisplayParts(MemberFormat), symbol);
                    content.Container = Namespace(symbol.ContainingType);
                    SetDocs(content, symbol, compilation, cancellationToken);
                    break;
            }

            return content;
        }

        private static void SetDocs(QuickDocContent content, ISymbol symbol, Compilation compilation, CancellationToken cancellationToken)
        {
            var (xml, inheritedFrom) = InheritDocResolver.DocumentationOf(DocumentedSymbol(symbol), compilation, cancellationToken);
            content.Xml = xml;
            content.InheritedFrom = inheritedFrom;
        }

        /// <summary>
        /// A parameter or type parameter: its description from the member's &lt;param&gt; or
        /// &lt;typeparam&gt;, shown as the summary, and the member as the container.
        /// </summary>
        private static void DescribeAsPartOf(QuickDocContent content, ISymbol member, string tag, string name, Compilation compilation, CancellationToken cancellationToken)
        {
            // A delegate's parameters are documented on the delegate type.
            if (member is IMethodSymbol invoke && invoke.MethodKind == MethodKind.DelegateInvoke)
            {
                member = invoke.ContainingType;
            }

            SetContainerMember(content, member);
            if (member == null || content.ContainerId == null)
            {
                return; // A lambda's parameters: no docs possible.
            }

            var (xml, inheritedFrom) = InheritDocResolver.DocumentationOf(DocumentedSymbol(member), compilation, cancellationToken);
            if (xml == null)
            {
                return;
            }

            XElement docs;
            try
            {
                docs = XElement.Parse("<member>" + xml + "</member>", LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                return;
            }

            var part = docs.Elements(tag).FirstOrDefault(e => (string)e.Attribute("name") == name);
            if (part != null && part.Nodes().Any())
            {
                content.Xml = "<summary>" + string.Concat(part.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting))) + "</summary>";
                content.InheritedFrom = inheritedFrom;
            }
        }

        /// <summary>The member a local or parameter belongs to, linked to its docs when it can have any.</summary>
        private static void SetContainerMember(QuickDocContent content, ISymbol member)
        {
            if (member == null || (member is IMethodSymbol method && method.MethodKind == MethodKind.AnonymousFunction))
            {
                return;
            }

            content.Container = member.ToDisplayString(TitleFormat);
            content.ContainerId = DocumentedSymbol(member).GetDocumentationCommentId();
        }

        /// <summary>The symbol whose docs apply: the declared one, not a generic instance or an extension call's reduced form.</summary>
        private static ISymbol DocumentedSymbol(ISymbol symbol)
        {
            if (symbol is IMethodSymbol method && method.ReducedFrom != null)
            {
                return method.ReducedFrom.OriginalDefinition;
            }

            return symbol.OriginalDefinition;
        }

        private static bool FromMetadata(ISymbol symbol) =>
            symbol.Locations.Length > 0 && symbol.Locations.All(l => l.IsInMetadata);

        private static string Namespace(ISymbol symbol)
        {
            var ns = symbol?.ContainingNamespace;
            return ns == null || ns.IsGlobalNamespace ? null : ns.ToDisplayString();
        }

        /// <summary>static / abstract / sealed / readonly / ref, which the type keyword display leaves out.</summary>
        private static IEnumerable<SignaturePart> TypeModifiers(INamedTypeSymbol type)
        {
            var words = new List<string>();
            if (type.TypeKind == TypeKind.Class)
            {
                if (type.IsStatic) words.Add("static");
                else if (type.IsAbstract) words.Add("abstract");
                else if (type.IsSealed) words.Add("sealed");
            }
            else if (type.TypeKind == TypeKind.Struct)
            {
                if (type.IsReadOnly) words.Add("readonly");
                if (type.IsRefLikeType) words.Add("ref");
            }

            foreach (var word in words)
            {
                yield return new SignaturePart(word, CodeTokenKind.Keyword);
                yield return new SignaturePart(" ", CodeTokenKind.Plain);
            }
        }

        /// <summary>Roslyn's display parts as coloured runs: named types link to their docs, the symbol's own name to its definition.</summary>
        private static List<SignaturePart> Parts(IEnumerable<SymbolDisplayPart> parts, ISymbol shown)
        {
            var result = new List<SignaturePart>();
            var own = DocumentedSymbol(shown);
            foreach (var part in parts)
            {
                string text = part.ToString();
                var kind = KindOf(part.Kind);
                bool isName = part.Symbol != null
                    && !(shown is INamespaceSymbol) // A namespace has no one definition to go to.
                    && IsNameKind(part.Kind)
                    && (SymbolEqualityComparer.Default.Equals(part.Symbol, shown) || SymbolEqualityComparer.Default.Equals(DocumentedSymbol(part.Symbol), own));

                string link = null;
                if (!isName && part.Symbol is INamedTypeSymbol type && type.TypeKind != TypeKind.Error)
                {
                    link = type.OriginalDefinition.GetDocumentationCommentId();
                }

                result.Add(new SignaturePart(text, kind, link, isName));
            }

            return result;
        }

        private static bool IsNameKind(SymbolDisplayPartKind kind) =>
            kind != SymbolDisplayPartKind.Keyword
            && kind != SymbolDisplayPartKind.Punctuation
            && kind != SymbolDisplayPartKind.Operator
            && kind != SymbolDisplayPartKind.Space
            && kind != SymbolDisplayPartKind.StringLiteral
            && kind != SymbolDisplayPartKind.NumericLiteral;

        internal static CodeTokenKind KindOf(SymbolDisplayPartKind kind)
        {
            switch (kind)
            {
                case SymbolDisplayPartKind.Keyword:
                    return CodeTokenKind.Keyword;
                case SymbolDisplayPartKind.ClassName:
                case SymbolDisplayPartKind.RecordClassName:
                case SymbolDisplayPartKind.StructName:
                case SymbolDisplayPartKind.RecordStructName:
                case SymbolDisplayPartKind.InterfaceName:
                case SymbolDisplayPartKind.EnumName:
                case SymbolDisplayPartKind.DelegateName:
                case SymbolDisplayPartKind.TypeParameterName:
                case SymbolDisplayPartKind.ErrorTypeName:
                case SymbolDisplayPartKind.ModuleName:
                    return CodeTokenKind.TypeName;
                case SymbolDisplayPartKind.MethodName:
                case SymbolDisplayPartKind.ExtensionMethodName:
                    return CodeTokenKind.MethodName;
                case SymbolDisplayPartKind.ParameterName:
                    return CodeTokenKind.Parameter;
                case SymbolDisplayPartKind.StringLiteral:
                    return CodeTokenKind.String;
                case SymbolDisplayPartKind.NumericLiteral:
                    return CodeTokenKind.Number;
                default:
                    return CodeTokenKind.Plain;
            }
        }
    }
}
