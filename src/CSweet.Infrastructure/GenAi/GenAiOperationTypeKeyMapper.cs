using CSweet.Domain.Setup;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.GenAi;

internal static class GenAiOperationTypeKeyMapper
{
    public static string Map(GenAiOperationType operationType) => operationType switch
    {
        GenAiOperationType.ImageGeneration => MediaOperationTypeKeys.ImageGenerateV1,
        GenAiOperationType.ImageEditing => MediaOperationTypeKeys.ImageEditV1,
        GenAiOperationType.VideoGeneration => MediaOperationTypeKeys.VideoGenerateV1,
        GenAiOperationType.VideoEditing => MediaOperationTypeKeys.VideoEditV1,
        _ => throw new ArgumentOutOfRangeException(nameof(operationType))
    };
}
