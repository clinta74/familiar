using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Familiar.Persistence;

/// <summary>
/// Converts every table, column, key, foreign key and index name to snake_case, so the convention is
/// enforced by code rather than by review.
/// </summary>
/// <remarks>
/// A column given an explicit name with <c>HasColumnName</c> keeps it. That is checked through the
/// annotation, not <c>GetColumnName()</c>, which always returns a value and can't tell an explicit
/// name from a default one.
/// </remarks>
public static class SnakeCaseNaming
{
    public static void ApplyTo(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            if (!entity.IsOwned() && entity.GetTableName() is { } table)
            {
                entity.SetTableName(ToSnakeCase(table));
            }

            foreach (var property in entity.GetProperties())
            {
                if (property.FindAnnotation(RelationalAnnotationNames.ColumnName) is null)
                {
                    property.SetColumnName(ToSnakeCase(property.GetColumnName()));
                }
            }

            foreach (var key in entity.GetKeys())
            {
                if (key.GetName() is { Length: > 0 } name)
                {
                    key.SetName(ToSnakeCase(name));
                }
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                if (foreignKey.GetConstraintName() is { Length: > 0 } name)
                {
                    foreignKey.SetConstraintName(ToSnakeCase(name));
                }
            }

            foreach (var index in entity.GetIndexes())
            {
                if (index.GetDatabaseName() is { Length: > 0 } name)
                {
                    index.SetDatabaseName(ToSnakeCase(name));
                }
            }
        }
    }

    /// <summary>
    /// <c>SecretHash</c> → <c>secret_hash</c>; runs of capitals stay together (<c>IPAddress</c> →
    /// <c>ip_address</c>); existing underscores are kept but never doubled (<c>IX_Accounts_Email</c>
    /// → <c>ix_accounts_email</c>).
    /// </summary>
    public static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];

            if (c == '_')
            {
                if (builder.Length > 0 && builder[^1] != '_')
                {
                    builder.Append('_');
                }

                continue;
            }

            if (char.IsUpper(c))
            {
                var previous = i > 0 ? name[i - 1] : '\0';
                var next = i + 1 < name.Length ? name[i + 1] : '\0';
                var startsWord = char.IsLower(previous) || char.IsDigit(previous)
                    || (char.IsUpper(previous) && char.IsLower(next));

                if (startsWord && builder.Length > 0 && builder[^1] != '_')
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
