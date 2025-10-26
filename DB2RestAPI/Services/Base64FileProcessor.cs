using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public class Base64FileProcessor
{
    private readonly string _tempFileDirectory;

    public Base64FileProcessor(string tempFileDirectory)
    {
        _tempFileDirectory = tempFileDirectory;
        Directory.CreateDirectory(_tempFileDirectory);
    }

    // File info to process
    private class FileToProcess
    {
        public string Base64Content { get; set; }
        public string FileName { get; set; }
        public int FileIndex { get; set; }
    }

    public async Task<string> ProcessPayloadAsync(
        HttpContext context,
        string filesDataPropertyName = "files_data",
        string fileNameProperty = "file_name",
        string base64PropertyName = "base64_content",
        CancellationToken cancellationToken = default)
    {
        // Read entire request body into memory (from pipe)
        var pipe = new Pipe();
        var fillPipeTask = FillPipeFromRequest(
            context.Request.Body,
            pipe.Writer,
            cancellationToken);

        // Read all data from pipe
        var jsonData = await ReadAllFromPipe(pipe.Reader, cancellationToken);
        await fillPipeTask;

        // Phase 1: Synchronously parse JSON and collect base64 content
        var (transformedJson, filesToProcess) = ParseAndTransformJson(
            jsonData,
            filesDataPropertyName,
            fileNameProperty,
            base64PropertyName);

        // Phase 2: Asynchronously write base64 content to files
        foreach (var fileInfo in filesToProcess)
        {
            string tempPath = await WriteBase64ToTempFile(
                fileInfo.Base64Content,
                fileInfo.FileName ?? "unknown",
                cancellationToken);

            // Update the transformed JSON with file path
            transformedJson = transformedJson.Replace(
                $"\"__TEMP_PATH_PLACEHOLDER_{fileInfo.FileIndex}__\"",
                $"\"{tempPath.Replace("\\", "\\\\")}\"");
        }

        return transformedJson;
    }

    private async Task FillPipeFromRequest(
        Stream source,
        PipeWriter writer,
        CancellationToken cancellationToken)
    {
        const int minimumBufferSize = 81920;

        try
        {
            while (true)
            {
                Memory<byte> memory = writer.GetMemory(minimumBufferSize);
                int bytesRead = await source.ReadAsync(memory, cancellationToken);

                if (bytesRead == 0)
                    break;

                writer.Advance(bytesRead);
                await writer.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    private async Task<byte[]> ReadAllFromPipe(
        PipeReader reader,
        CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            foreach (var segment in buffer)
            {
                ms.Write(segment.Span);
            }

            reader.AdvanceTo(buffer.End);

            if (result.IsCompleted)
                break;
        }

        await reader.CompleteAsync();
        return ms.ToArray();
    }

    private (string transformedJson, List<FileToProcess> filesToProcess) ParseAndTransformJson(
        byte[] jsonData,
        string filesDataPropertyName,
        string fileNameProperty,
        string base64PropertyName)
    {
        var filesToProcess = new List<FileToProcess>();
        using var outputStream = new MemoryStream();
        using var writer = new Utf8JsonWriter(outputStream, new JsonWriterOptions { Indented = true });

        var reader = new Utf8JsonReader(jsonData, new JsonReaderOptions { });

        ProcessJsonRecursive(
            ref reader,
            writer,
            filesDataPropertyName,
            fileNameProperty,
            base64PropertyName,
            filesToProcess);

        writer.Flush();
        string transformedJson = Encoding.UTF8.GetString(outputStream.ToArray());
        return (transformedJson, filesToProcess);
    }

    private void ProcessJsonRecursive(
        ref Utf8JsonReader reader,
        Utf8JsonWriter writer,
        string filesDataPropertyName,
        string fileNameProperty,
        string base64PropertyName,
        List<FileToProcess> filesToProcess,
        string currentPropertyName = null)
    {
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    writer.WriteStartObject();
                    break;

                case JsonTokenType.EndObject:
                    writer.WriteEndObject();
                    return;

                case JsonTokenType.StartArray:
                    writer.WriteStartArray();

                    // Check if this is the files_data array
                    if (currentPropertyName == filesDataPropertyName)
                    {
                        ProcessFilesArray(
                            ref reader,
                            writer,
                            fileNameProperty,
                            base64PropertyName,
                            filesToProcess);
                        writer.WriteEndArray();
                        return;
                    }
                    break;

                case JsonTokenType.EndArray:
                    writer.WriteEndArray();
                    return;

                case JsonTokenType.PropertyName:
                    string propName = reader.GetString();
                    writer.WritePropertyName(propName);

                    // Peek ahead
                    if (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.StartObject)
                        {
                            writer.WriteStartObject();
                            ProcessJsonRecursive(
                                ref reader, writer,
                                filesDataPropertyName, fileNameProperty, base64PropertyName,
                                filesToProcess);
                        }
                        else if (reader.TokenType == JsonTokenType.StartArray)
                        {
                            ProcessJsonRecursive(
                                ref reader, writer,
                                filesDataPropertyName, fileNameProperty, base64PropertyName,
                                filesToProcess, propName);
                        }
                        else
                        {
                            WriteValue(ref reader, writer);
                        }
                    }
                    break;

                default:
                    WriteValue(ref reader, writer);
                    break;
            }
        }
    }

    private void ProcessFilesArray(
        ref Utf8JsonReader reader,
        Utf8JsonWriter writer,
        string fileNameProperty,
        string base64PropertyName,
        List<FileToProcess> filesToProcess)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return;
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                ProcessSingleFileObject(
                    ref reader,
                    writer,
                    fileNameProperty,
                    base64PropertyName,
                    filesToProcess);
            }
        }
    }

    private void ProcessSingleFileObject(
        ref Utf8JsonReader reader,
        Utf8JsonWriter writer,
        string fileNameProperty,
        string base64PropertyName,
        List<FileToProcess> filesToProcess)
    {
        writer.WriteStartObject();

        string fileName = null;
        string base64Content = null;
        int fileIndex = filesToProcess.Count;
        int depth = reader.CurrentDepth;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
            {
                break;
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                string propName = reader.GetString();
                reader.Read();

                if (propName == fileNameProperty)
                {
                    fileName = reader.GetString();
                    writer.WriteString(propName, fileName);
                }
                else if (propName == base64PropertyName)
                {
                    base64Content = reader.GetString();
                    // Write empty string or keep property with placeholder
                    writer.WriteString(propName, string.Empty);
                }
                else
                {
                    writer.WritePropertyName(propName);
                    WriteValue(ref reader, writer);
                }
            }
        }

        // Add placeholder for temp file path (will be replaced later)
        writer.WriteString("backend_base64_temp_file_path", $"__TEMP_PATH_PLACEHOLDER_{fileIndex}__");

        writer.WriteEndObject();

        // Store file info for async processing
        if (!string.IsNullOrEmpty(base64Content))
        {
            filesToProcess.Add(new FileToProcess
            {
                Base64Content = base64Content,
                FileName = fileName,
                FileIndex = fileIndex
            });
        }
    }

    private void WriteValue(ref Utf8JsonReader reader, Utf8JsonWriter writer)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                writer.WriteStringValue(reader.GetString());
                break;
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out long longValue))
                    writer.WriteNumberValue(longValue);
                else
                    writer.WriteNumberValue(reader.GetDouble());
                break;
            case JsonTokenType.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonTokenType.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonTokenType.Null:
                writer.WriteNullValue();
                break;
        }
    }

    private async Task<string> WriteBase64ToTempFile(
        string base64Content,
        string originalFileName,
        CancellationToken cancellationToken)
    {
        string tempFileName = $"{Path.GetFileNameWithoutExtension(originalFileName)}_{Guid.NewGuid()}{Path.GetExtension(originalFileName)}";
        string tempFilePath = Path.Combine(_tempFileDirectory, tempFileName);

        await using var fileStream = new FileStream(
            tempFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        using var transform = new FromBase64Transform();

        const int chunkSize = 4096;
        int offset = 0;

        while (offset < base64Content.Length)
        {
            int length = Math.Min(chunkSize, base64Content.Length - offset);

            if (offset + length < base64Content.Length && length % 4 != 0)
            {
                length = (length / 4) * 4;
            }

            if (length == 0)
                break;

            byte[] inputBuffer = ArrayPool<byte>.Shared.Rent(length);
            byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(length);

            try
            {
                int bytesEncoded = Encoding.ASCII.GetBytes(
                    base64Content.AsSpan(offset, length),
                    inputBuffer);

                bool isFinalBlock = (offset + length >= base64Content.Length);

                if (isFinalBlock)
                {
                    byte[] finalOutput = transform.TransformFinalBlock(inputBuffer, 0, bytesEncoded);
                    await fileStream.WriteAsync(finalOutput, cancellationToken);
                }
                else
                {
                    int outputBytes = transform.TransformBlock(
                        inputBuffer, 0, bytesEncoded,
                        outputBuffer, 0);

                    await fileStream.WriteAsync(
                        outputBuffer.AsMemory(0, outputBytes),
                        cancellationToken);
                }

                offset += length;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(inputBuffer);
                ArrayPool<byte>.Shared.Return(outputBuffer);
            }
        }

        return tempFilePath;
    }
}
