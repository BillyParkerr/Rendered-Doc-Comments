using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RenderedDocComments.Parsing;

namespace RenderedDocComments.Navigation
{
    /// <summary>Documentation a comment inherits through &lt;inheritdoc/&gt;.</summary>
    internal sealed class InheritedDoc
    {
        public static readonly InheritedDoc NotFound = new InheritedDoc(null, null);

        public InheritedDoc(string xml, string sourceId, IReadOnlyCollection<string> sourceFiles = null)
        {
            Xml = xml;
            SourceId = sourceId;
            SourceFiles = sourceFiles ?? Array.Empty<string>();
        }

        /// <summary>The inherited documentation XML, or null when there's nothing to inherit.</summary>
        public string Xml { get; }

        /// <summary>Documentation ID of the member it came from (e.g. "M:Ns.Type.Method(System.Int32)").</summary>
        public string SourceId { get; }

        /// <summary>
        /// The files declaring every member looked at on the way (each step of an inheritance
        /// chain, documented or not), so an edit in any of them can refresh the card.
        /// </summary>
        public IReadOnlyCollection<string> SourceFiles { get; }

        public bool Found => Xml != null;
    }

    /// <summary>
    /// Works out what an &lt;inheritdoc/&gt; inherits: the member named by its cref, or else the
    /// overridden member, the implemented interface member, or (for a type) its base type or
    /// first interface. That member's own &lt;inheritdoc/&gt; is followed too, up to a limit.
    /// </summary>
    internal static class InheritDocResolver
    {
        private const int MaxDepth = 8;

        public static async Task<InheritedDoc> ResolveAsync(Document document, TextSpan commentSpan, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root == null || model == null)
            {
                return InheritedDoc.NotFound;
            }

            var trivia = root.DescendantTrivia(commentSpan).FirstOrDefault(t => t.GetStructure() is DocumentationCommentTriviaSyntax);
            if (!(trivia.GetStructure() is DocumentationCommentTriviaSyntax comment))
            {
                return InheritedDoc.NotFound;
            }

            var declared = DeclaredSymbolFor(trivia.Token, model, cancellationToken);
            var cref = comment.DescendantNodes()
                .OfType<XmlCrefAttributeSyntax>()
                .FirstOrDefault(a => IsInheritDoc(a.Parent));

            ISymbol source;
            if (cref != null)
            {
                var info = model.GetSymbolInfo(cref.Cref, cancellationToken);
                source = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            }
            else
            {
                source = declared == null ? null : InheritanceSource(declared);
            }

            // Walk up until a member actually has documentation (an undocumented override in
            // between shouldn't stop the search).
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int depth = 0; source != null && depth < MaxDepth; depth++)
            {
                // A cref like ExecuteAsync{T}(...) binds to a version of the method built from the
                // cref's own type parameters; its documentation ID can't be computed. Use the
                // method as declared.
                source = source.OriginalDefinition;
                AddFiles(source, files);

                var docs = Expand(source, model.Compilation, 0, files, cancellationToken);
                if (docs != null)
                {
                    KeepOnlyParametersOf(docs, declared);
                    return new InheritedDoc(InnerXml(docs), source.GetDocumentationCommentId(), files.ToList());
                }

                source = cref == null ? InheritanceSource(source) : null;
            }

            // Nothing to inherit yet; documenting one of these members (in its own file) changes that.
            return files.Count == 0 ? InheritedDoc.NotFound : new InheritedDoc(null, null, files.ToList());
        }

        private static bool IsInheritDoc(SyntaxNode node) =>
            (node is XmlEmptyElementSyntax empty && empty.Name.LocalName.Text == "inheritdoc")
            || (node is XmlElementStartTagSyntax start && start.Name.LocalName.Text == "inheritdoc");

        private static ISymbol DeclaredSymbolFor(SyntaxToken token, SemanticModel model, CancellationToken cancellationToken)
        {
            var member = token.Parent?.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
            if (member is BaseFieldDeclarationSyntax field)
            {
                return field.Declaration.Variables
                    .Select(v => model.GetDeclaredSymbol(v, cancellationToken))
                    .FirstOrDefault(s => s != null);
            }

            return member == null ? null : model.GetDeclaredSymbol(member, cancellationToken);
        }

        private static void AddFiles(ISymbol symbol, HashSet<string> files)
        {
            foreach (var location in symbol.Locations)
            {
                string file = location.IsInSource ? location.SourceTree?.FilePath : null;
                if (!string.IsNullOrEmpty(file)) files.Add(file);
            }
        }

        /// <summary>
        /// A symbol's documentation, for the quick documentation popup: its own, with any
        /// &lt;inheritdoc/&gt; filled in. An undocumented override or interface implementation
        /// shows the documentation of the member it overrides or implements, as Rider does.
        /// </summary>
        /// <returns>
        /// The docs without a root element (null when there are none), and the documentation ID
        /// of the member they came from when that's another member (null when they're its own).
        /// </returns>
        public static (string Xml, string InheritedFrom) DocumentationOf(ISymbol symbol, Compilation compilation, CancellationToken cancellationToken)
        {
            symbol = symbol.OriginalDefinition;
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var own = Expand(symbol, compilation, 0, files, cancellationToken);
            if (own != null)
            {
                var inheritDoc = Parse(symbol, cancellationToken)?.Elements("inheritdoc").FirstOrDefault();
                if (inheritDoc == null)
                {
                    return (InnerXml(own), null);
                }

                // Inherited parameters this member doesn't have are dropped (its own are left as written).
                // An <inheritdoc/> that's still there found nothing to inherit.
                var from = own.Elements("inheritdoc").Any() ? null : InheritanceTarget(inheritDoc, symbol, compilation);
                KeepOnlyParametersOf(own, symbol);
                return (InnerXml(own), from?.OriginalDefinition.GetDocumentationCommentId());
            }

            // Types and constructors don't take their base's docs: they'd describe something else.
            if (symbol is INamedTypeSymbol || (symbol is IMethodSymbol method && method.MethodKind == MethodKind.Constructor))
            {
                return (null, null);
            }

            var source = InheritanceSource(symbol);
            for (int depth = 0; source != null && depth < MaxDepth; depth++)
            {
                source = source.OriginalDefinition;
                var docs = Expand(source, compilation, 0, files, cancellationToken);
                if (docs != null)
                {
                    KeepOnlyParametersOf(docs, symbol);
                    return (InnerXml(docs), source.GetDocumentationCommentId());
                }

                source = InheritanceSource(source);
            }

            return (null, null);
        }

        private static string InnerXml(XElement docs) =>
            string.Concat(docs.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting)));

        /// <summary>A symbol's own documentation as XML (a &lt;member&gt; element), or null.</summary>
        private static XElement Parse(ISymbol symbol, CancellationToken cancellationToken)
        {
            string raw = symbol.GetDocumentationCommentXml(expandIncludes: true, cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                return XElement.Parse(raw, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                return null;
            }
        }

        /// <summary>What an &lt;inheritdoc/&gt; inherits from: its cref, or the overridden or implemented member.</summary>
        private static ISymbol InheritanceTarget(XElement inheritDoc, ISymbol symbol, Compilation compilation)
        {
            string cref = (string)inheritDoc.Attribute("cref");
            return cref != null
                ? DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, compilation)
                : InheritanceSource(symbol);
        }

        /// <summary>A symbol's documentation with any &lt;inheritdoc/&gt; in it filled in.</summary>
        private static XElement Expand(ISymbol symbol, Compilation compilation, int depth, HashSet<string> files, CancellationToken cancellationToken)
        {
            var docs = Parse(symbol, cancellationToken);
            if (docs == null)
            {
                return null;
            }

            var inheritDoc = docs.Elements("inheritdoc").FirstOrDefault();
            if (inheritDoc == null || depth >= MaxDepth)
            {
                return docs;
            }

            var next = InheritanceTarget(inheritDoc, symbol, compilation);
            if (next != null)
            {
                AddFiles(next.OriginalDefinition, files);
            }

            var inherited = next == null ? null : Expand(next.OriginalDefinition, compilation, depth + 1, files, cancellationToken);

            // A path picks which part of the inherited docs this member takes.
            string path = (string)inheritDoc.Attribute("path");
            if (inherited != null && !string.IsNullOrWhiteSpace(path))
            {
                inherited = SelectPath(inherited, path);
            }

            return inherited == null ? docs : Merge(docs, inherited);
        }

        private static XElement SelectPath(XElement docs, string path)
        {
            string selected = InheritDocPath.Select(InnerXml(docs), path);
            if (selected == null)
            {
                return null;
            }

            try
            {
                return XElement.Parse("<member>" + selected + "</member>", LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                return null;
            }
        }

        /// <summary>Own sections win; inherited ones fill the gaps.</summary>
        private static XElement Merge(XElement own, XElement inherited)
        {
            var result = new XElement(own);
            result.Elements("inheritdoc").Remove();

            foreach (var element in inherited.Elements())
            {
                string name = element.Name.LocalName;
                bool present;
                switch (name)
                {
                    case "inheritdoc":
                        continue;
                    case "param":
                    case "typeparam":
                        present = result.Elements(name).Any(e => (string)e.Attribute("name") == (string)element.Attribute("name"));
                        break;
                    case "exception":
                    case "permission":
                    case "seealso":
                        present = result.Elements(name).Any(e =>
                            (string)e.Attribute("cref") == (string)element.Attribute("cref")
                            && (string)e.Attribute("href") == (string)element.Attribute("href"));
                        break;
                    default:
                        present = result.Element(name) != null;
                        break;
                }

                if (!present)
                {
                    result.Add(new XElement(element));
                }
            }

            return result;
        }

        /// <summary>Drop inherited &lt;param&gt;/&lt;typeparam&gt; for parameters this member doesn't have.</summary>
        private static void KeepOnlyParametersOf(XElement docs, ISymbol declared)
        {
            var parameters = ParameterNames(declared);
            if (parameters != null)
            {
                docs.Elements("param").Where(e => !parameters.Contains((string)e.Attribute("name"))).Remove();
            }

            var typeParameters = TypeParameterNames(declared);
            if (typeParameters != null)
            {
                docs.Elements("typeparam").Where(e => !typeParameters.Contains((string)e.Attribute("name"))).Remove();
            }
        }

        private static HashSet<string> ParameterNames(ISymbol symbol)
        {
            switch (symbol)
            {
                case IMethodSymbol method: return new HashSet<string>(method.Parameters.Select(p => p.Name));
                case IPropertySymbol property: return new HashSet<string>(property.Parameters.Select(p => p.Name));
                case INamedTypeSymbol type when type.DelegateInvokeMethod != null: return new HashSet<string>(type.DelegateInvokeMethod.Parameters.Select(p => p.Name));
                default: return null;
            }
        }

        private static HashSet<string> TypeParameterNames(ISymbol symbol)
        {
            switch (symbol)
            {
                case IMethodSymbol method: return new HashSet<string>(method.TypeParameters.Select(p => p.Name));
                case INamedTypeSymbol type: return new HashSet<string>(type.TypeParameters.Select(p => p.Name));
                default: return null;
            }
        }

        /// <summary>Where a symbol's &lt;inheritdoc/&gt; without a cref inherits from.</summary>
        private static ISymbol InheritanceSource(ISymbol symbol)
        {
            switch (symbol)
            {
                case INamedTypeSymbol type:
                    if (type.TypeKind == TypeKind.Class && type.BaseType != null && type.BaseType.SpecialType != SpecialType.System_Object)
                    {
                        return type.BaseType;
                    }

                    return type.Interfaces.FirstOrDefault();

                case IMethodSymbol method:
                    if (method.MethodKind == MethodKind.Constructor)
                    {
                        return method.ContainingType.BaseType?.InstanceConstructors.FirstOrDefault(c => SameParameters(c, method));
                    }

                    if (method.OverriddenMethod != null) return method.OverriddenMethod;
                    if (method.ExplicitInterfaceImplementations.Length > 0) return method.ExplicitInterfaceImplementations[0];
                    break;

                case IPropertySymbol property:
                    if (property.OverriddenProperty != null) return property.OverriddenProperty;
                    if (property.ExplicitInterfaceImplementations.Length > 0) return property.ExplicitInterfaceImplementations[0];
                    break;

                case IEventSymbol @event:
                    if (@event.OverriddenEvent != null) return @event.OverriddenEvent;
                    if (@event.ExplicitInterfaceImplementations.Length > 0) return @event.ExplicitInterfaceImplementations[0];
                    break;
            }

            return ImplementedInterfaceMember(symbol);
        }

        private static ISymbol ImplementedInterfaceMember(ISymbol symbol)
        {
            var type = symbol.ContainingType;
            if (type == null)
            {
                return null;
            }

            foreach (var @interface in type.AllInterfaces)
            {
                foreach (var member in @interface.GetMembers())
                {
                    if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), symbol))
                    {
                        return member;
                    }
                }
            }

            return null;
        }

        private static bool SameParameters(IMethodSymbol a, IMethodSymbol b) =>
            a.Parameters.Length == b.Parameters.Length
            && a.Parameters.Zip(b.Parameters, (x, y) => SymbolEqualityComparer.Default.Equals(x.Type, y.Type)).All(same => same);
    }
}
