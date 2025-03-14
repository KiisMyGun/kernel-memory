﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.DataFormats.Image;
using Microsoft.KernelMemory.DataFormats.Office;
using Microsoft.KernelMemory.DataFormats.Pdf;
using Microsoft.KernelMemory.DataFormats.WebPages;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.Pipeline;

namespace Microsoft.KernelMemory.Handlers;

/// <summary>
/// Memory ingestion pipeline handler responsible for extracting text from files and saving it to content storage.
/// </summary>
public class TextExtractionHandler : IPipelineStepHandler
{
    private readonly IPipelineOrchestrator _orchestrator;
    private readonly WebScraper _webScraper;
    private readonly IOcrEngine? _ocrEngine;
    private readonly ILogger<TextExtractionHandler> _log;

    /// <inheritdoc />
    public string StepName { get; }

    /// <summary>
    /// Handler responsible for extracting text from documents.
    /// Note: stepName and other params are injected with DI.
    /// </summary>
    /// <param name="stepName">Pipeline step for which the handler will be invoked</param>
    /// <param name="orchestrator">Current orchestrator used by the pipeline, giving access to content and other helps.</param>
    /// <param name="ocrEngine">The ocr engine to use for parsing image files</param>
    /// <param name="log">Application logger</param>
    public TextExtractionHandler(
        string stepName,
        IPipelineOrchestrator orchestrator,
        IOcrEngine? ocrEngine = null,
        ILogger<TextExtractionHandler>? log = null)
    {
        this.StepName = stepName;
        this._orchestrator = orchestrator;
        this._ocrEngine = ocrEngine;
        this._log = log ?? DefaultLogger<TextExtractionHandler>.Instance;
        this._webScraper = new WebScraper(this._log);

        this._log.LogInformation("Handler '{0}' ready", stepName);
    }

    /// <inheritdoc />
    public async Task<(bool success, DataPipeline updatedPipeline)> InvokeAsync(
        DataPipeline pipeline, CancellationToken cancellationToken = default)
    {
        this._log.LogDebug("Extracting text, pipeline '{0}/{1}'", pipeline.Index, pipeline.DocumentId);

        foreach (DataPipeline.FileDetails uploadedFile in pipeline.Files)
        {
            if (uploadedFile.AlreadyProcessedBy(this))
            {
                this._log.LogTrace("File {0} already processed by this handler", uploadedFile.Name);
                continue;
            }

            var sourceFile = uploadedFile.Name;
            var destFile = $"{uploadedFile.Name}.extract.txt";
            BinaryData fileContent = await this._orchestrator.ReadFileAsync(pipeline, sourceFile, cancellationToken).ConfigureAwait(false);

            string text = string.Empty;
            string extractType = MimeTypes.PlainText;
            bool skipFile = false;

            if (fileContent.ToArray().Length > 0)
            {
                (text, extractType, skipFile) = await this.ExtractTextAsync(uploadedFile, fileContent, cancellationToken).ConfigureAwait(false);
            }

            // If the handler cannot extract text, we move on. There might be other handlers in the pipeline
            // capable of doing so, and in any case if a document contains multiple docs, the pipeline will
            // not fail, only do its best to export as much data as possible. The user can inspect the pipeline
            // status to know if a file has been ignored.
            if (!skipFile)
            {
                this._log.LogDebug("Saving extracted text file {0}", destFile);
                await this._orchestrator.WriteFileAsync(pipeline, destFile, new BinaryData(text), cancellationToken).ConfigureAwait(false);

                var destFileDetails = new DataPipeline.GeneratedFileDetails
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ParentId = uploadedFile.Id,
                    Name = destFile,
                    Size = text.Length,
                    MimeType = extractType,
                    ArtifactType = DataPipeline.ArtifactTypes.ExtractedText,
                    Tags = pipeline.Tags,
                };
                destFileDetails.MarkProcessedBy(this);

                uploadedFile.GeneratedFiles.Add(destFile, destFileDetails);
            }

            uploadedFile.MarkProcessedBy(this);
        }

        return (true, pipeline);
    }

    private async Task<(string text, string extractType, bool skipFile)> ExtractTextAsync(
        DataPipeline.FileDetails uploadedFile,
        BinaryData fileContent,
        CancellationToken cancellationToken)
    {
        bool skipFile = false;
        string text = string.Empty;
        string extractType = MimeTypes.PlainText;

        switch (uploadedFile.MimeType)
        {
            case MimeTypes.PlainText:
                this._log.LogDebug("Extracting text from plain text file {0}", uploadedFile.Name);
                text = fileContent.ToString();
                break;

            case MimeTypes.MarkDown:
                this._log.LogDebug("Extracting text from MarkDown file {0}", uploadedFile.Name);
                text = fileContent.ToString();
                extractType = MimeTypes.MarkDown;
                break;

            case MimeTypes.Json:
                this._log.LogDebug("Extracting text from JSON file {0}", uploadedFile.Name);
                text = fileContent.ToString();
                break;

            case MimeTypes.MsWord:
                this._log.LogDebug("Extracting text from MS Word file {0}", uploadedFile.Name);
                text = new MsWordDecoder().DocToText(fileContent);
                break;

            case MimeTypes.MsPowerPoint:
                this._log.LogDebug("Extracting text from MS PowerPoint file {0}", uploadedFile.Name);
                text = new MsPowerPointDecoder().DocToText(fileContent,
                    withSlideNumber: true,
                    withEndOfSlideMarker: false,
                    skipHiddenSlides: true);
                break;

            case MimeTypes.MsExcel:
                this._log.LogDebug("Extracting text from MS Excel file {0}", uploadedFile.Name);
                text = new MsExcelDecoder().DocToText(fileContent);
                break;

            case MimeTypes.Pdf:
                this._log.LogDebug("Extracting text from PDF file {0}", uploadedFile.Name);
                text = new PdfDecoder().DocToText(fileContent);
                break;

            case MimeTypes.WebPageUrl:
                var url = fileContent.ToString();
                this._log.LogDebug("Downloading web page specified in {0} and extracting text from {1}", uploadedFile.Name, url);
                if (string.IsNullOrWhiteSpace(url))
                {
                    skipFile = true;
                    uploadedFile.Log(this, "The web page URL is empty");
                    this._log.LogWarning("The web page URL is empty");
                    break;
                }

                var result = await this._webScraper.GetTextAsync(url).ConfigureAwait(false);
                if (!result.Success)
                {
                    skipFile = true;
                    uploadedFile.Log(this, $"Download error: {result.Error}");
                    this._log.LogWarning("Web page download error: {0}", result.Error);
                    break;
                }

                if (string.IsNullOrEmpty(result.Text))
                {
                    skipFile = true;
                    uploadedFile.Log(this, "The web page has no text content, skipping it");
                    this._log.LogWarning("The web page has no text content, skipping it");
                    break;
                }

                text = result.Text;
                this._log.LogDebug("Web page {0} downloaded, text length: {1}", url, text.Length);
                break;

            case "":
                skipFile = true;
                uploadedFile.Log(this, "File MIME type is empty, ignoring the file");
                this._log.LogWarning("Empty MIME type, the file will be ignored");
                break;

            case MimeTypes.ImageJpeg:
            case MimeTypes.ImagePng:
            case MimeTypes.ImageTiff:
                this._log.LogDebug("Extracting text from image file {0}", uploadedFile.Name);
                if (this._ocrEngine == null)
                {
                    throw new NotSupportedException($"Image extraction not configured: {uploadedFile.Name}");
                }

                text = await new ImageDecoder().ImageToTextAsync(this._ocrEngine, fileContent, cancellationToken).ConfigureAwait(false);
                break;

            default:
                skipFile = true;
                uploadedFile.Log(this, $"File MIME type not supported: {uploadedFile.MimeType}. Ignoring the file.");
                this._log.LogWarning("File MIME type not supported: {0} - ignoring the file", uploadedFile.MimeType);
                break;
        }

        return (text, extractType, skipFile);
    }
}
