// Every documentation tag the cards handle, for trying the extension in the experimental instance.

using System;
using System.Collections.Generic;

namespace Contoso.Gallery
{
    public static class TagGallery
    {
        /// <summary>
        /// A table list:
        /// <list type="table">
        /// <listheader><term>Mode</term><description>What it does</description></listheader>
        /// <item><term>Exponential</term><description>Doubles the delay each time.</description></item>
        /// <item><term>Linear</term><description>Adds the initial delay each time, which takes longer to describe so it wraps.</description></item>
        /// <item><term>Constant</term><description>Always waits the initial delay.</description></item>
        /// </list>
        /// </summary>
        public static void TableList() { }

        /// <summary>
        /// Lists inside lists, and paragraphs in items:
        /// <list type="bullet">
        /// <item><description>A plain point.</description></item>
        /// <item>
        /// <description>
        /// A point with its own list:
        /// <list type="number">
        /// <item><description>First step.</description></item>
        /// <item><description>Second step.</description></item>
        /// </list>
        /// </description>
        /// </item>
        /// <item><term>Term only</term></item>
        /// <item>An item with no description element.</item>
        /// </list>
        /// </summary>
        public static void NestedLists() { }

        /// <summary>Code in other languages.</summary>
        /// <example>
        /// XML:
        /// <code language="xml">
        /// &lt;retry attempts="3" delay="00:00:01" /&gt;
        /// </code>
        /// JSON:
        /// <code lang="json">
        /// { "attempts": 3, "delay": "00:00:01" }
        /// </code>
        /// C#:
        /// <code language="csharp">
        /// var policy = new RetryPolicy { MaxAttempts = 3 };
        /// </code>
        /// </example>
        public static void CodeLanguages() { }

        /// <summary>
        /// Links: <see cref="String"/>, <see cref="T:System.String"/>, <see cref="List{T}.Add(T)"/>,
        /// <see cref="String.Join(string, string[])">a labelled link</see>, <see href="https://learn.microsoft.com/dotnet"/>,
        /// <see href="https://learn.microsoft.com/dotnet">a labelled web link</see>, and keywords
        /// <see langword="null"/>, <see langword="true"/>, <see langword="async"/>, <see langword="await"/>.
        /// </summary>
        /// <seealso cref="TableList"/>
        /// <seealso href="https://github.com">GitHub</seealso>
        public static void Links() { }

        /// <summary>
        /// HTML: <b>bold</b>, <i>italic</i>, <u>underline</u>, <strong>strong</strong>, <em>emphasis</em>,
        /// <tt>teletype</tt>, H<sub>2</sub>O, x<sup>2</sup>, <s>struck</s>, a<br/>line break,
        /// and <a href="https://example.com">a link</a>.
        /// <p>A paragraph in p tags.</p>
        /// <ul><li>HTML bullet one</li><li>HTML bullet two</li></ul>
        /// <ol><li>HTML number one</li><li>HTML number two</li></ol>
        /// <pre>Preformatted
        ///   text keeps its spacing.</pre>
        /// </summary>
        public static void Html() { }

        /// <summary>Notes and other callouts.</summary>
        /// <remarks>
        /// <note type="note">A plain note.</note>
        /// <note type="tip">A tip.</note>
        /// <note type="warning">A warning that is long enough to wrap onto a second line inside the callout box.</note>
        /// <note type="important">Something important.</note>
        /// <note type="caution">A caution.</note>
        /// <note>A note with no type.</note>
        /// </remarks>
        public static void Notes() { }

        /// <summary>Less common sections.</summary>
        /// <typeparam name="T">The item type.</typeparam>
        /// <param name="items">The items, with <paramref name="items"/> and <typeparamref name="T"/> referenced.</param>
        /// <value>Not used on a method, but shown.</value>
        /// <permission cref="System.Security.PermissionSet">Everyone can call this.</permission>
        /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"/>
        /// <returns/>
        public static int Sections<T>(IEnumerable<T> items) => 0;

        /// <summary>Tags that aren't for this card.</summary>
        /// <overloads>Overload group text, shown on overload pages, not for one member.</overloads>
        /// <threadsafety static="true" instance="false"/>
        /// <filterpriority>1</filterpriority>
        /// <custom>A custom tag some team uses.</custom>
        public static void Unusual() { }

        /// <include file="Docs.xml" path="docs/members[@name='Included']/*"/>
        public static void Included() { }

        /// <summary>
        /// <para>A summary that starts with a paragraph.</para>
        /// <para>And a second one, then a code block:</para>
        /// <code>
        /// Console.WriteLine("hi");
        /// </code>
        /// Then text with a very long unbreakable word: https://example.com/a/really/long/path/that/keeps/going/and/going/without/any/spaces/at/all
        /// </summary>
        public static void Paragraphs() { }
    }
}
