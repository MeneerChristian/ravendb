using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Raven.Client.Documents.Conventions;

namespace Raven.Client.Documents.Session
{
    internal static class JoinedFieldPath
    {
        /// <summary>
        /// The path of a property of the joined documents (u => u.Country, u => u.Address.City) the way it is named in the index.
        /// Null for the identity property (u => u.Id): the joined documents are then matched by id.
        /// </summary>
        public static string Get(DocumentConventions conventions, LambdaExpression joinedKey)
        {
            var parts = new Stack<string>();
            var expression = joinedKey.Body;
            while (true)
            {
                if (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
                    expression = convert.Operand;
                else if (expression is MemberExpression member)
                {
                    parts.Push(conventions.GetConvertedPropertyNameFor(member.Member));
                    expression = member.Expression;
                }
                else
                    break;
            }

            if (expression is not ParameterExpression parameter || parts.Count == 0)
                throw new NotSupportedException("The field of the joined documents has to be a property or a path of properties, for example: u => u.Country. Expression: " + joinedKey);

            var identityProperty = conventions.GetIdentityProperty(parameter.Type);
            if (parts.Count == 1 && identityProperty != null && parts.Peek() == conventions.GetConvertedPropertyNameFor(identityProperty))
                return null;

            return string.Join(".", parts);
        }
    }
}
