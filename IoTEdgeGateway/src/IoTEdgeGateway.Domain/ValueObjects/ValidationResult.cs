using System.Collections.Generic;
using System.Linq;

namespace IoTEdgeGateway.Domain.ValueObjects
{
    public sealed class ValidationResult
    {
        public bool IsValid { get; }
        public IReadOnlyList<string> Errors { get; }

        private ValidationResult(bool isValid, IReadOnlyList<string> errors)
        {
            IsValid = isValid;
            Errors = errors;
        }

        public static ValidationResult Success() =>
            new(true, new List<string>());

        public static ValidationResult Failure(IEnumerable<string> errors)
        {
            var errorList = errors.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
            return new(errorList.Count == 0, errorList);
        }

        public static ValidationResult Combine(params ValidationResult[] results)
        {
            var errors = results.SelectMany(r => r.Errors).ToList();
            return new(errors.Count == 0, errors);
        }
    }
}
