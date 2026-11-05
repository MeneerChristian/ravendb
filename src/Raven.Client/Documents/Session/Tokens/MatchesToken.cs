using System.Text;

namespace Raven.Client.Documents.Session.Tokens
{
    /// <summary>matches(alias): the row is joined with a document that the sub-query of the join matched.</summary>
    internal sealed class MatchesToken : WhereToken
    {
        private MatchesToken()
        {
        }

        public static MatchesToken Create(string joinAlias)
        {
            return new MatchesToken
            {
                FieldName = joinAlias,
                Options = WhereOptions.Default()
            };
        }

        public override WhereToken AddAlias(string alias) => this;

        public override void WriteTo(StringBuilder writer)
        {
            writer.Append("matches(").Append(FieldName).Append(')');
        }
    }
}
