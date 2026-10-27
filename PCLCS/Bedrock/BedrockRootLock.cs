using System.Security.Cryptography;
using System.Text;

namespace PCLCS.Bedrock;

/// <summary>Serializes cooperating launcher operations on one selected data root.</summary>
internal sealed class BedrockRootLock : IDisposable {
    private readonly System.Threading.Mutex mutex;

    private BedrockRootLock(System.Threading.Mutex mutex) => this.mutex = mutex;

    public static BedrockRootLock Acquire(string root) {
        using var hash = SHA256.Create();
        string canonical = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        string name = "Local\\PCL-Bedrock-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "");
        var mutex = new System.Threading.Mutex(false, name);
        try {
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (System.Threading.AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("另一个启动器正在操作该数据目录。");
            return new(mutex);
        } catch {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose() {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
