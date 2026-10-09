using CSweet.Memory;

namespace CSweet.Infrastructure.Core;

// The linked production eligibility policy delegates non-employee sensitivity to a
// database-backed platform authorizer. This standalone synthetic harness has no platform
// identity database. Fail closed rather than inventing conversation/case permissions.
// Employee partitions short-circuit before this method; scoped acceptance stays in the
// actual platform tests and is explicitly excluded from this report.
internal static class MemoryScopedAudienceAuthorization
{
    public static MemoryNamespace? Resolve(MemoryPartition partition) =>
        throw new NotSupportedException("This evaluator measures employee partitions only; scoped audience authorization requires the platform.");
}
