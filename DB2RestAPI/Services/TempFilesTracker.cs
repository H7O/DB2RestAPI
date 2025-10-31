namespace DB2RestAPI.Services
{
    public class TempFileInfo
    {
        public string TempFilePath { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        
    }
    public class TempFilesTracker : IDisposable
    {
        // Dictionary provides O(1) lookup for duplicate checking
        private readonly Dictionary<string, TempFileInfo> _files = new();

        public void AddLocalFile(string tempFilePath, string originalFileName, string relativeFilePath)
        {
            // O(1) duplicate check instead of O(n) with List.Any()
            if (!_files.TryAdd(tempFilePath, 
                new TempFileInfo() 
                { 
                    TempFilePath = tempFilePath, 
                    OriginalFileName = originalFileName,
                    RelativePath = relativeFilePath
                }))
                throw new InvalidOperationException("Duplicate temp file path, temp file path already added to cleanup list.");
        }

        // Return as IReadOnlyDictionary to prevent external modification
        public IReadOnlyDictionary<string, TempFileInfo> GetLocalFiles()
        {
            return _files;
        }

        public void Dispose()
        {
            foreach (var (tempFilePath, _) in _files)
            {
                try
                {
                    if (File.Exists(tempFilePath))
                        File.Delete(tempFilePath);
                }
                catch { }
            }
        }
    }

}
