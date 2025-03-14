﻿// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.AI;
using Microsoft.KernelMemory.AI.AzureOpenAI;
using Microsoft.KernelMemory.AI.Tokenizers;

// ReSharper disable once CheckNamespace
namespace Microsoft.KernelMemory;

public static partial class KernelMemoryBuilderExtensions
{
    /// <summary>
    /// Use Azure OpenAI to generate text embeddings.
    /// </summary>
    /// <param name="builder">Kernel Memory builder</param>
    /// <param name="config">Azure OpenAI settings</param>
    /// <param name="textTokenizer">Tokenizer used to count tokens sent to the embedding generator</param>
    /// <param name="loggerFactory">.NET Logger factory</param>
    /// <param name="onlyForRetrieval">Whether to use this embedding generator only during data ingestion, and not for retrieval (search and ask API)</param>
    /// <returns>KM builder instance</returns>
    public static IKernelMemoryBuilder WithAzureOpenAITextEmbeddingGeneration(
        this IKernelMemoryBuilder builder,
        AzureOpenAIConfig config,
        ITextTokenizer? textTokenizer = null,
        ILoggerFactory? loggerFactory = null,
        bool onlyForRetrieval = false)
    {
        config.Validate();
        textTokenizer ??= new DefaultGPTTokenizer();
        builder.Services.AddAzureOpenAIEmbeddingGeneration(config, textTokenizer);

        if (!onlyForRetrieval)
        {
            builder.AddIngestionEmbeddingGenerator(
                new AzureOpenAITextEmbeddingGenerator(
                    config: config,
                    textTokenizer: textTokenizer,
                    loggerFactory: loggerFactory));
        }

        return builder;
    }

    /// <summary>
    /// Use Azure OpenAI to generate text, e.g. answers and summaries.
    /// </summary>
    /// <param name="builder">Kernel Memory builder</param>
    /// <param name="config">Azure OpenAI settings</param>
    /// <param name="textTokenizer">Tokenizer used to count tokens used by prompts</param>
    /// <returns>KM builder instance</returns>
    public static IKernelMemoryBuilder WithAzureOpenAITextGeneration(
        this IKernelMemoryBuilder builder,
        AzureOpenAIConfig config,
        ITextTokenizer? textTokenizer = null)
    {
        config.Validate();
        textTokenizer ??= new DefaultGPTTokenizer();
        builder.Services.AddAzureOpenAITextGeneration(config, textTokenizer);
        return builder;
    }
}

public static partial class DependencyInjection
{
    public static IServiceCollection AddAzureOpenAIEmbeddingGeneration(
        this IServiceCollection services,
        AzureOpenAIConfig config,
        ITextTokenizer? textTokenizer = null)
    {
        config.Validate();
        textTokenizer ??= new DefaultGPTTokenizer();
        return services
            .AddSingleton<ITextEmbeddingGenerator>(serviceProvider => new AzureOpenAITextEmbeddingGenerator(
                config,
                textTokenizer: textTokenizer,
                loggerFactory: serviceProvider.GetService<ILoggerFactory>()));
    }

    public static IServiceCollection AddAzureOpenAITextGeneration(
        this IServiceCollection services,
        AzureOpenAIConfig config,
        ITextTokenizer? textTokenizer = null)
    {
        config.Validate();
        textTokenizer ??= new DefaultGPTTokenizer();
        return services
            .AddSingleton<ITextGenerator>(serviceProvider => new AzureOpenAITextGenerator(
                config: config,
                textTokenizer: textTokenizer,
                log: serviceProvider.GetService<ILogger<AzureOpenAITextGenerator>>()));
    }
}
