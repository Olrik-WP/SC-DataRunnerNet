using DataRunner.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace DataRunner.Ocr;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PaddleOCR-backed pipeline factory. <see cref="IOcrPipeline"/> resolves
    /// asynchronously the first time you request it via <see cref="IOcrPipelineFactory"/>.
    /// </summary>
    public static IServiceCollection AddPaddleOcrPipeline(this IServiceCollection services)
    {
        services.AddSingleton<IOcrPipelineFactory, PaddleOcrPipelineFactory>();
        return services;
    }
}
