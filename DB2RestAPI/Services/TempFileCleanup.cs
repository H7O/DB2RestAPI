namespace DB2RestAPI.Services
{
    public class TempFileCleanup : IDisposable
    {
        // Dictionary provides O(1) lookup for duplicate checking
        private readonly Dictionary<string, string> _files = new();

        public void AddLocalFile(string tempFilePath, string originalFileName)
        {
            // O(1) duplicate check instead of O(n) with List.Any()
            if (!_files.TryAdd(tempFilePath, originalFileName))
                throw new InvalidOperationException("Duplicate temp file path, temp file path already added to cleanup list.");
        }

        // Return as IReadOnlyDictionary to prevent external modification
        public IReadOnlyDictionary<string, string> GetLocalFiles()
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
