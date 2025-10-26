namespace DB2RestAPI.Services
{
    public class TempFileCleanup : IDisposable
    {
        private readonly List<string> _filePaths = new();

        public void AddLocalFile(string path) => _filePaths.Add(path);

        public void Dispose()
        {
            // Clean up local files
            foreach (var path in _filePaths)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch // (Exception ex)
                {
                    // Log but don't throw in Dispose
                }
            }

        }
    }
}
