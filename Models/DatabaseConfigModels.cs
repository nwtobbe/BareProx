using System.ComponentModel.DataAnnotations;

namespace BareProx.Models
{
    public class DatabaseConfigModels : IValidatableObject
    {
        public string DbType { get; set; } = "SqlServer"; // SqlServer or Sqlite

        public string DbServer { get; set; }
        public string DbName { get; set; }
        public string DbUser { get; set; }
        public string DbPassword { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (string.IsNullOrWhiteSpace(DbType))
            {
                yield return new ValidationResult("Database type is required.", new[] { nameof(DbType) });
            }

            if (string.IsNullOrWhiteSpace(DbName))
            {
                yield return new ValidationResult("The DbName field is required.", new[] { nameof(DbName) });
            }

            if (DbType == "SqlServer")
            {
                if (string.IsNullOrWhiteSpace(DbServer))
                    yield return new ValidationResult("The DbServer field is required.", new[] { nameof(DbServer) });

                if (string.IsNullOrWhiteSpace(DbUser))
                    yield return new ValidationResult("The DbUser field is required.", new[] { nameof(DbUser) });

                if (string.IsNullOrWhiteSpace(DbPassword))
                    yield return new ValidationResult("The DbPassword field is required.", new[] { nameof(DbPassword) });
            }
        }
    }
}
