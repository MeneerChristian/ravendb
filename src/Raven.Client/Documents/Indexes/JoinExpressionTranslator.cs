using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Raven.Client.Documents.Conventions;

namespace Raven.Client.Documents.Indexes;

/// <summary>Turns the expressions of a join index declaration into the names and terms of a <see cref="JoinDefinition"/>.</summary>
internal static class JoinExpressionTranslator
{
    internal readonly struct Member
    {
        public Member(string name, Type type)
        {
            Name = name;
            Type = type;
        }

        public string Name { get; }

        public Type Type { get; }
    }

    /// <summary>The field of the index entry that a lambda like <c>x => x.Country</c> names. Only a direct property or field of the entry is accepted: the fields of an index entry are flat.</summary>
    public static Member ToMember(LambdaExpression lambda, DocumentConventions conventions, string role)
    {
        if (lambda == null)
            throw new ArgumentNullException(nameof(lambda));

        if (lambda.Parameters.Count != 1)
            throw Cannot(lambda, role, "it must have exactly one parameter, the index entry");

        Expression body = lambda.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs } unary)
            body = unary.Operand;

        if (body is not MemberExpression member)
            throw Cannot(lambda, role, DescribeUnsupported(body));

        if (member.Member is not (PropertyInfo or FieldInfo))
            throw Cannot(lambda, role, $"'{member.Member.Name}' is not a property or a field");

        if (member.Expression != lambda.Parameters[0])
        {
            string reason = member.Expression is MemberExpression
                ? "it names a nested path; the fields of an index entry are flat, declare the field under the name it has in the index"
                : "it does not start at the parameter of the lambda";
            throw Cannot(lambda, role, reason);
        }

        return new Member(GetName(member.Member, conventions), member.Type);
    }

    /// <summary>The term that is stored for the value of a grouping, the way the index writes it: 'true' for a boolean, the invariant text of an integer, the string itself.</summary>
    public static string ToTerm(object value, string field)
    {
        switch (value)
        {
            case null:
                throw new JoinValidationException(JoinValidationCode.UnsupportedExpression, $"The grouping on '{field}' has no value: a grouping is an equality with a value, null is not one.");
            case string text:
                return text;
            case char character:
                return character.ToString();
            case bool flag:
                return flag ? "true" : "false";
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            default:
                throw new JoinValidationException(JoinValidationCode.UnsupportedExpression,
                    $"The value of the grouping on '{field}' is a {value.GetType().Name}. A grouping value is a string, a char, a bool or an integer; " +
                    "for any other type pass the string as the index writes it, for example AddGrouping(\"Field\", \"2024-01-01T00:00:00.0000000\").");
        }
    }

    public static void CheckKey(Member key, bool joinsOnAField = false)
    {
        if (joinsOnAField)
        {
            if (key.Type != typeof(string) && key.Type != typeof(object))
            {
                throw new JoinValidationException(JoinValidationCode.OuterFieldNotSupported,
                    $"The join key '{key.Name}' is a {key.Type.Name}. The key of a join on a field of the inner index is a string field with one value per entry.");
            }

            return;
        }

        if (key.Type != typeof(string) && key.Type != typeof(object) && IsCollectionOf(key.Type, typeof(string)) == false)
        {
            throw new JoinValidationException(JoinValidationCode.OuterFieldNotSupported,
                $"The join key '{key.Name}' is a {key.Type.Name}. The key of a join index is the field of the outer index that holds the id of the joined document, a string, or an array of ids, a collection of strings.");
        }
    }

    public static void CheckInnerKey(Member key)
    {
        if (key.Type != typeof(string) && key.Type != typeof(object))
        {
            throw new JoinValidationException(JoinValidationCode.InnerFieldNotSupported,
                $"The inner join key '{key.Name}' is a {key.Type.Name}. The inner key of a join on a field is a string field with one value per entry.");
        }
    }

    public static void CheckField(Member member, JoinFieldKind kind)
    {
        Type type = Nullable.GetUnderlyingType(member.Type) ?? member.Type;
        if (type == typeof(object))
            return;

        switch (kind)
        {
            case JoinFieldKind.Long:
                if (IsInteger(type) == false)
                    throw Mismatch(member, kind, type, "a Long field is an integer (sbyte, byte, short, ushort, int, uint, long, ulong)");
                break;
            case JoinFieldKind.Text:
                if (type != typeof(string) && IsCollectionOf(type, typeof(string)) == false)
                    throw Mismatch(member, kind, type, "a Text field is a string, or a collection of strings");
                break;
            case JoinFieldKind.Exact:
                if (IsCollection(type))
                    throw Mismatch(member, kind, type, "an Exact field has one term per entry, declare a collection as Text");
                break;
            default:
                throw new JoinValidationException(JoinValidationCode.InvalidDefinition, $"The field '{member.Name}' has an unknown kind.");
        }
    }

    public static void CheckGrouping(Member member)
    {
        Type type = Nullable.GetUnderlyingType(member.Type) ?? member.Type;
        if (IsCollection(type))
        {
            throw new JoinValidationException(JoinValidationCode.FieldKindMismatch,
                $"The grouping on '{member.Name}' needs a field with one term per entry, but the property is a {type.Name}.");
        }
    }

    private static string GetName(MemberInfo member, DocumentConventions conventions)
    {
        if (conventions?.PropertyNameConverter == null)
            return member.Name;

        if (conventions.ShouldApplyPropertyNameConverter != null && conventions.ShouldApplyPropertyNameConverter(member) == false)
            return member.Name;

        return conventions.PropertyNameConverter(member);
    }

    private static bool IsInteger(Type type) =>
        type == typeof(sbyte) || type == typeof(byte) || type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);

    private static bool IsCollection(Type type) => type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);

    private static bool IsCollectionOf(Type type, Type element)
    {
        if (IsCollection(type) == false)
            return false;

        if (type.IsArray)
            return type.GetElementType() == element;

        foreach (Type candidate in type.GetInterfaces().Append(type))
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return candidate.GetGenericArguments()[0] == element;
        }

        return false;
    }

    private static string DescribeUnsupported(Expression body)
    {
        return body switch
        {
            MethodCallExpression call => $"it calls the method '{call.Method.Name}'; only the direct property or field of the entry can be named",
            ParameterExpression => "it names the entry itself, not one of its fields",
            ConstantExpression => "it is a constant, not a field of the entry",
            BinaryExpression { NodeType: ExpressionType.ArrayIndex } or IndexExpression => "it uses an indexer; only the direct property or field of the entry can be named",
            BinaryExpression or ConditionalExpression => "it computes a value; the value of a field is computed in the map of the index, name the field here",
            _ => $"a {body.NodeType} expression is not supported; only the direct property or field of the entry can be named"
        };
    }

    private static JoinValidationException Cannot(LambdaExpression lambda, string role, string reason)
    {
        return new JoinValidationException(JoinValidationCode.UnsupportedExpression,
            $"The expression '{lambda}' cannot declare {role}: {reason}. Use an expression like 'x => x.Country' or the name of the field as a string.");
    }

    private static JoinValidationException Mismatch(Member member, JoinFieldKind kind, Type type, string rule)
    {
        return new JoinValidationException(JoinValidationCode.FieldKindMismatch,
            $"The field '{member.Name}' is declared as {kind}, but the property is a {type.Name}: {rule}.");
    }
}
